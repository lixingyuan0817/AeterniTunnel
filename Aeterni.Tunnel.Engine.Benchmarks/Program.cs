using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Transport;
using Aeterni.Tunnel.Engine.Wire;

internal static class Program
{
    private const int Warmups = 3;
    private const int FrameRepeats = 5;
    private const int ChannelMessages = 5_000;
    private const int TransportMessages = 500;
    private const int ConcurrentConnections = 4;
    private const int ControlSamples = 500;

    private static async Task Main(string[] args)
    {
        var quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
        var frameRepeats = quick ? 2 : FrameRepeats;
        var channelMessages = quick ? 500 : ChannelMessages;
        var transportMessages = quick ? 100 : TransportMessages;

        PrintEnvironment(quick);

        foreach (var size in new[] { 64, 1_024, 65_536 })
            RunFrameCodec(size, frameRepeats);

        await RunChannelMultiplexerAsync(channelMessages);
        await RunControlLatencyAsync(useTls: false, quick ? 100 : ControlSamples);
        await RunControlLatencyAsync(useTls: true, quick ? 100 : ControlSamples);
        await RunTransportAsync(useTls: false, transportMessages);
        await RunTransportAsync(useTls: true, transportMessages);
        await RunConcurrentTransportAsync(useTls: false, transportMessages / 2);
        await RunConcurrentTransportAsync(useTls: true, transportMessages / 2);
    }

    private static void PrintEnvironment(bool quick)
    {
        var assembly = Assembly.GetEntryAssembly()?.GetName().Version;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        Console.WriteLine("Aeterni.Tunnel.Engine.Benchmarks");
        Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os={RuntimeInformation.OSDescription}");
        Console.WriteLine($"architecture={RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"processor_count={Environment.ProcessorCount}");
        Console.WriteLine($"server_gc={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"entry_version={assembly}");
        Console.WriteLine($"configuration={configuration}");
        Console.WriteLine($"warmups={Warmups};quick={quick}");
        Console.WriteLine();
    }

    private static void RunFrameCodec(int payloadSize, int repeats)
    {
        var payload = new byte[payloadSize];
        Random.Shared.NextBytes(payload);
        var frame = Frame.Data(1, payload);
        var encoded = FrameCodec.Encode(frame);

        for (var i = 0; i < Warmups; i++)
        {
            _ = FrameCodec.Encode(frame);
            using var warmupStream = new MemoryStream(encoded, writable: false);
            _ = FrameCodec.ReadAsync(warmupStream).AsTask().GetAwaiter().GetResult();
        }

        var operations = payloadSize <= 1_024 ? 10_000 : 1_000;
        var elapsed = TimeSpan.Zero;
        long allocated = 0;
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < operations; i++)
            {
                var bytes = FrameCodec.Encode(frame);
                using var stream = new MemoryStream(bytes, writable: false);
                _ = FrameCodec.ReadAsync(stream).AsTask().GetAwaiter().GetResult();
            }

            timer.Stop();
            elapsed += timer.Elapsed;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var totalOps = (double)operations * repeats;
        var seconds = elapsed.TotalSeconds;
        var throughput = totalOps * payloadSize / seconds / 1024 / 1024;
        Console.WriteLine($"frame_codec,payload={payloadSize},ops={totalOps:0},ops_per_sec={totalOps / seconds:0},payload_mib_per_sec={throughput:0.00},alloc_bytes_per_op={allocated / totalOps:0.0}");
    }

    private static async Task RunChannelMultiplexerAsync(int messages)
    {
        await using var server = TcpTlsTransport.Server(IPAddress.Loopback, 0);
        var port = GetListeningPort(server);
        await using var client = TcpTlsTransport.Client("127.0.0.1", port, useTls: false);
        var clientConnect = client.ConnectAsync("127.0.0.1", port).AsTask();
        var serverAccept = server.AcceptAsync().AsTask();
        var clientConnection = await clientConnect;
        var serverConnection = await serverAccept;
        await using var clientMux = new ChannelMultiplexer(clientConnection);
        await using var serverMux = new ChannelMultiplexer(serverConnection);
        clientMux.Start();
        serverMux.Start();
        var clientChannel = clientMux.OpenChannel();
        var serverChannel = serverMux.AcceptChannel(clientChannel.ChannelId);
        var payload = new byte[1_024];
        Random.Shared.NextBytes(payload);

        for (var i = 0; i < Warmups; i++)
        {
            await clientChannel.WriteAsync(payload);
            _ = await serverChannel.ReadAsync();
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new Stopwatch();
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < messages; i++)
                await clientChannel.WriteAsync(payload);
        });
        var reader = Task.Run(async () =>
        {
            await start.Task;
            for (var i = 0; i < messages; i++)
            {
                var value = await serverChannel.ReadAsync();
                if (value is null || value.Length != payload.Length)
                    throw new InvalidDataException("Channel benchmark received an invalid payload.");
            }
        });
        timer.Start();
        start.SetResult();
        await Task.WhenAll(writer, reader);
        timer.Stop();

        var mib = (double)messages * payload.Length / 1024 / 1024;
        Console.WriteLine($"channel_multiplexer,payload={payload.Length},messages={messages},mib_per_sec={mib / timer.Elapsed.TotalSeconds:0.00},elapsed_ms={timer.Elapsed.TotalMilliseconds:0.0}");
    }

    private static async Task RunTransportAsync(bool useTls, int messages)
    {
        using var certificate = useTls ? CreateCertificate() : null;
        await using var server = TcpTlsTransport.Server(IPAddress.Loopback, 0, certificate);
        var port = GetListeningPort(server);
        await using var client = TcpTlsTransport.Client("127.0.0.1", port, useTls, "localhost", validateCertificate: false);

        var connectTimer = Stopwatch.StartNew();
        var clientTask = client.ConnectAsync("127.0.0.1", port);
        var serverTask = server.AcceptAsync();
        var clientConnection = await clientTask;
        var serverConnection = await serverTask;
        connectTimer.Stop();
        await RunEchoAsync(useTls ? "tcp_tls" : "tcp", clientConnection, serverConnection, messages, connectTimer.Elapsed);
    }

    private static async Task RunControlLatencyAsync(bool useTls, int samples)
    {
        using var certificate = useTls ? CreateCertificate() : null;
        await using var server = TcpTlsTransport.Server(IPAddress.Loopback, 0, certificate);
        var port = GetListeningPort(server);
        await using var client = TcpTlsTransport.Client("127.0.0.1", port, useTls, "localhost", validateCertificate: false);
        var clientTask = client.ConnectAsync("127.0.0.1", port).AsTask();
        var serverTask = server.AcceptAsync().AsTask();
        var clientConnection = await clientTask;
        var serverConnection = await serverTask;
        var payload = new byte[16];
        var total = Warmups + samples;
        var serverLoop = ControlEchoAsync(serverConnection.Stream, total);
        var latency = new double[samples];
        for (var i = 0; i < total; i++)
        {
            var timer = Stopwatch.StartNew();
            await FrameCodec.WriteAsync(clientConnection.Stream, Frame.Control(FrameContract.ControlChannel, payload));
            _ = await FrameCodec.ReadAsync(clientConnection.Stream);
            timer.Stop();
            if (i >= Warmups)
                latency[i - Warmups] = timer.Elapsed.TotalMilliseconds * 1_000;
        }

        await serverLoop;
        await clientConnection.DisposeAsync();
        await serverConnection.DisposeAsync();
        Array.Sort(latency);
        var mode = useTls ? "tcp_tls" : "tcp";
        Console.WriteLine($"control_latency,mode={mode},samples={samples},p50_us={Percentile(latency, 0.50):0.0},p95_us={Percentile(latency, 0.95):0.0},p99_us={Percentile(latency, 0.99):0.0}");
    }

    private static async Task ControlEchoAsync(Stream stream, int messages)
    {
        for (var i = 0; i < messages; i++)
        {
            var frame = await FrameCodec.ReadAsync(stream);
            await FrameCodec.WriteAsync(stream, frame);
        }
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
    }

    private static async Task RunConcurrentTransportAsync(bool useTls, int messages)
    {
        using var certificate = useTls ? CreateCertificate() : null;
        await using var server = TcpTlsTransport.Server(IPAddress.Loopback, 0, certificate);
        var port = GetListeningPort(server);
        await using var client = TcpTlsTransport.Client("127.0.0.1", port, useTls, "localhost", validateCertificate: false);
        var connectTimer = Stopwatch.StartNew();
        var clientTasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(_ => client.ConnectAsync("127.0.0.1", port).AsTask()).ToArray();
        var serverTasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(_ => server.AcceptAsync().AsTask()).ToArray();
        var clientConnections = await Task.WhenAll(clientTasks);
        var serverConnections = await Task.WhenAll(serverTasks);
        connectTimer.Stop();

        var payload = new byte[1_024];
        Random.Shared.NextBytes(payload);
        var timer = Stopwatch.StartNew();
        var serverLoops = serverConnections.Select(connection => EchoServerAsync(connection.Stream, payload.Length, messages)).ToArray();
        var clientLoops = clientConnections.Select(connection => EchoClientAsync(connection.Stream, payload, messages)).ToArray();
        await Task.WhenAll(clientLoops.Concat(serverLoops));
        timer.Stop();

        await DisposeConnectionsAsync(clientConnections);
        await DisposeConnectionsAsync(serverConnections);
        var mib = (double)ConcurrentConnections * messages * payload.Length / 1024 / 1024;
        Console.WriteLine($"transport_concurrent,mode={(useTls ? "tcp_tls" : "tcp")},connections={ConcurrentConnections},payload={payload.Length},messages_per_connection={messages},connect_ms={connectTimer.Elapsed.TotalMilliseconds:0.0},mib_per_sec={mib / timer.Elapsed.TotalSeconds:0.00},elapsed_ms={timer.Elapsed.TotalMilliseconds:0.0}");
    }

    private static async Task RunEchoAsync(string mode, ITunnelConnection client, ITunnelConnection server, int messages, TimeSpan connectElapsed)
    {
        var payload = new byte[4_096];
        Random.Shared.NextBytes(payload);
        for (var i = 0; i < Warmups; i++)
        {
            var warmupServer = EchoServerAsync(server.Stream, payload.Length, 1);
            await client.Stream.WriteAsync(payload);
            await ReadExactlyAsync(client.Stream, payload.Length);
            await warmupServer;
        }

        var serverLoop = EchoServerAsync(server.Stream, payload.Length, messages);
        var timer = Stopwatch.StartNew();
        await EchoClientAsync(client.Stream, payload, messages);
        await serverLoop;
        timer.Stop();
        var mib = (double)messages * payload.Length / 1024 / 1024;
        await client.DisposeAsync();
        await server.DisposeAsync();
        Console.WriteLine($"transport,mode={mode},payload={payload.Length},messages={messages},connect_ms={connectElapsed.TotalMilliseconds:0.0},mib_per_sec={mib / timer.Elapsed.TotalSeconds:0.00},elapsed_ms={timer.Elapsed.TotalMilliseconds:0.0}");
    }

    private static async Task EchoServerAsync(Stream stream, int payloadLength, int messages)
    {
        var buffer = new byte[payloadLength];
        for (var i = 0; i < messages; i++)
        {
            await ReadExactlyAsync(stream, buffer.Length, buffer);
            await stream.WriteAsync(buffer);
        }
    }

    private static async Task EchoClientAsync(Stream stream, byte[] payload, int messages)
    {
        for (var i = 0; i < messages; i++)
        {
            await stream.WriteAsync(payload);
            await ReadExactlyAsync(stream, payload.Length);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, int length, byte[]? buffer = null)
    {
        buffer ??= new byte[length];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, length));
    }

    private static int GetListeningPort(TcpTlsTransport transport)
    {
        var listener = typeof(TcpTlsTransport).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(transport) as TcpListener;
        return ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? throw new InvalidOperationException("Unable to determine benchmark listener port.");
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    private static async Task DisposeConnectionsAsync(IEnumerable<ITunnelConnection> connections)
    {
        foreach (var connection in connections)
            await connection.DisposeAsync();
    }
}
