using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Server;
using Aeterni.Tunnel.Engine.Transport;
using Aeterni.Tunnel.Engine.Wire;

namespace Aeterni.Tunnel.Engine.Tests;

public class ConnectionIsolationTests
{
    private const string TestToken = "test-secret-token";

    [Fact]
    public async Task Agent_BindsDataConnectionToSameAccessPort()
    {
        var controlPort = FreePort();
        await using var listener = new ServerListener(controlPort, TestToken,
            maxDataConnectionsPerClient: 1);
        listener.Start();
        await using var agent = new AgentSession(new AgentOptions(
            "127.0.0.1", controlPort, TestToken, "isolated-agent",
            UseTls: false,
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));

        await agent.ConnectAsync();
        await WaitUntilAsync(() => agent.DataConnectionCount == 1 &&
            listener.GetSession("isolated-agent")?.DataConnectionCount == 1);

        Assert.True(agent.NegotiatedCapabilities.HasFlag(ProtocolCapabilities.ConnectionIsolation));
        Assert.Equal(1, agent.DataConnectionCount);
        Assert.Equal(1, listener.GetSession("isolated-agent")!.DataConnectionCount);
        Assert.Single(listener.GetStatusSnapshot().Clients);
        using var cannotBindSecondListener = new TcpListener(IPAddress.Any, controlPort);
        Assert.Throws<SocketException>(cannotBindSecondListener.Start);
    }

    [Fact]
    public async Task BoundDataConnection_CarriesTunnelTraffic()
    {
        using var local = new TcpListener(IPAddress.Loopback, 0);
        local.Start();
        var localPort = ((IPEndPoint)local.LocalEndpoint).Port;
        var echoTask = Task.Run(async () =>
        {
            using var accepted = await local.AcceptTcpClientAsync();
            var buffer = new byte[32];
            var count = await accepted.GetStream().ReadAsync(buffer);
            await accepted.GetStream().WriteAsync(buffer.AsMemory(0, count));
        });

        var controlPort = FreePort();
        var proxyPort = FreePort();
        await using var listener = new ServerListener(controlPort, TestToken,
            maxDataConnectionsPerClient: 1);
        listener.Start();
        await using var agent = new AgentSession(new AgentOptions(
            "127.0.0.1", controlPort, TestToken, "data-path-agent", UseTls: false));
        await agent.ConnectAsync();
        await WaitUntilAsync(() => agent.DataConnectionCount == 1 &&
            listener.GetSession("data-path-agent")?.DataConnectionCount == 1);

        var registered = new TaskCompletionSource<(bool Ok, string? Detail)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, detail) =>
        {
            if (id == "data-path")
                registered.TrySetResult((ok, detail));
        };
        await agent.RegisterProxyAsync("data-path", LinkType.Tcp, "127.0.0.1", localPort, proxyPort);
        var registration = await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registration.Ok, registration.Detail);

        using var user = new TcpClient();
        await user.ConnectAsync(IPAddress.Loopback, proxyPort);
        var payload = new byte[] { 7, 8, 9, 10 };
        await user.GetStream().WriteAsync(payload);
        var response = new byte[payload.Length];
        await user.GetStream().ReadExactlyAsync(response);
        Assert.Equal(payload, response);
        await echoTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BindingToken_IsOneTimeAndConnectionQuotaIsEnforced()
    {
        var controlPort = FreePort();
        await using var listener = new ServerListener(controlPort, TestToken,
            maxDataConnectionsPerClient: 1);
        listener.Start();
        using var control = await ConnectRawControlAsync(controlPort, "raw-binding");
        var controlStream = control.GetStream();

        var token = await RequestTokenAsync(controlStream);
        using var bound = new TcpClient();
        await bound.ConnectAsync(IPAddress.Loopback, controlPort);
        var firstAck = await BindAsync(bound.GetStream(), token.Token);
        Assert.True(firstAck.Ok, firstAck.Error);
        await WaitUntilAsync(() => listener.GetSession("raw-binding")?.DataConnectionCount == 1);

        using var replay = new TcpClient();
        await replay.ConnectAsync(IPAddress.Loopback, controlPort);
        var replayAck = await BindAsync(replay.GetStream(), token.Token);
        Assert.False(replayAck.Ok);
        Assert.Contains("无效", replayAck.Error);

        await SendControlAsync(controlStream, new RequestDataConnectionMessage());
        var quotaError = Assert.IsType<ErrorMessage>(await ReadControlAsync(controlStream));
        Assert.Equal(429, quotaError.Code);
    }

    [Fact]
    public async Task InvalidAndExpiredBindingTokensAreRejected()
    {
        var controlPort = FreePort();
        await using var listener = new ServerListener(controlPort, TestToken,
            maxDataConnectionsPerClient: 1,
            dataConnectionTokenLifetime: TimeSpan.FromMilliseconds(50));
        listener.Start();

        using (var unauthorized = new TcpClient())
        {
            await unauthorized.ConnectAsync(IPAddress.Loopback, controlPort);
            var invalidAck = await BindAsync(unauthorized.GetStream(), "not-a-valid-token");
            Assert.False(invalidAck.Ok);
            Assert.Contains("无效", invalidAck.Error);
        }

        using var control = await ConnectRawControlAsync(controlPort, "expiring-binding");
        var token = await RequestTokenAsync(control.GetStream());
        await Task.Delay(100);
        using var expired = new TcpClient();
        await expired.ConnectAsync(IPAddress.Loopback, controlPort);
        var expiredAck = await BindAsync(expired.GetStream(), token.Token);
        Assert.False(expiredAck.Ok);
        Assert.Contains("过期", expiredAck.Error);
    }

    [Fact]
    public async Task SlowChannelReset_DoesNotBlockAnotherChannel()
    {
        var (clientMux, serverMux, transport) = await CreateMultiplexersAsync(
            new ChannelQueueOptions(MaxQueuedPackets: 2, MaxQueuedBytes: 1024));
        await using var _transport = transport;
        await using var _client = clientMux;
        await using var _server = serverMux;
        // 模拟不遵守协商窗口的对端：接收端必须只重置超限通道，而不是堵住全局读循环。
        serverMux.EnableSlowChannelIsolation();

        var slowClient = clientMux.OpenChannel();
        _ = serverMux.AcceptChannel(slowClient.ChannelId);
        var fastClient = clientMux.OpenChannel();
        var fastServer = serverMux.AcceptChannel(fastClient.ChannelId);

        await slowClient.WriteAsync(new byte[512]);
        await slowClient.WriteAsync(new byte[512]);
        await slowClient.WriteAsync(new byte[512]);

        var reset = await Assert.ThrowsAsync<CommunicationException>(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await slowClient.WriteAsync(new byte[] { 1 });
                await Task.Delay(10);
            }
            throw new TimeoutException("未收到慢通道重置");
        });
        Assert.Equal(CommunicationErrorCode.BackpressureLimit, reset.Code);

        await fastClient.WriteAsync(new byte[] { 42 });
        Assert.Equal(new byte[] { 42 }, await fastServer.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task OutboundScheduler_RotatesAcrossActiveChannels()
    {
        var stream = new GateWriteStream();
        await using var connection = new StreamConnection(stream);
        await using var mux = new ChannelMultiplexer(connection,
            new ChannelQueueOptions(MaxQueuedPackets: 16, MaxQueuedBytes: 1024));
        var flood = mux.OpenChannel();
        var latencySensitive = mux.OpenChannel();

        var first = flood.WriteAsync(new byte[] { 1 }).AsTask();
        await stream.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var floodWrites = Enumerable.Range(0, 8)
            .Select(_ => flood.WriteAsync(new byte[] { 2 }).AsTask())
            .ToArray();
        var priorityWrite = latencySensitive.WriteAsync(new byte[] { 3 }).AsTask();
        stream.ReleaseWrites();

        await Task.WhenAll(floodWrites.Append(first).Append(priorityWrite));
        var channelOrder = stream.Writes.Select(payload =>
            BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2))).ToArray();
        Assert.True(channelOrder.Length >= 2);
        Assert.Equal(flood.ChannelId, channelOrder[0]);
        Assert.Equal(latencySensitive.ChannelId, channelOrder[1]);
    }

    [Fact]
    public async Task ByteBudget_CancellationAndCloseReleaseWaiters()
    {
        var budget = new AsyncByteBudget(4);
        Assert.True(budget.TryAcquire(4));

        using var cancelled = new CancellationTokenSource();
        var cancelledWait = budget.AcquireAsync(4, cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        budget.Release(4);
        Assert.True(budget.TryAcquire(4));

        var closedWait = budget.AcquireAsync(4, CancellationToken.None).AsTask();
        budget.Close(new CommunicationException(CommunicationErrorCode.Closed, "closed"));
        var error = await Assert.ThrowsAsync<CommunicationException>(() => closedWait);
        Assert.Equal(CommunicationErrorCode.Closed, error.Code);
        budget.Release(4);
    }

    private static async Task<(ChannelMultiplexer Client, ChannelMultiplexer Server, IAsyncDisposable Transport)>
        CreateMultiplexersAsync(ChannelQueueOptions options)
    {
        var port = FreePort();
        var serverTransport = TcpTlsTransport.Server(IPAddress.Loopback, port);
        var clientTransport = TcpTlsTransport.Client("127.0.0.1", port, useTls: false);
        var clientTask = clientTransport.ConnectAsync("127.0.0.1", port).AsTask();
        var serverConnection = await serverTransport.AcceptAsync();
        var clientConnection = await clientTask;
        var serverMux = new ChannelMultiplexer(serverConnection, options);
        var clientMux = new ChannelMultiplexer(clientConnection, options);
        serverMux.Start();
        clientMux.Start();
        return (clientMux, serverMux, serverTransport);
    }

    private static async Task<TcpClient> ConnectRawControlAsync(int port, string clientId)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await SendControlAsync(client.GetStream(), new HelloMessage(clientId,
            ProtocolContract.CurrentVersion, TestToken, "host",
            (ulong)ProtocolContract.SupportedCapabilities));
        var hello = Assert.IsType<HelloAckMessage>(await ReadControlAsync(client.GetStream()));
        Assert.True(hello.Ok, hello.Error);
        Assert.IsType<PortPolicyMessage>(await ReadControlAsync(client.GetStream()));
        return client;
    }

    private static async Task<DataConnectionTokenMessage> RequestTokenAsync(NetworkStream stream)
    {
        await SendControlAsync(stream, new RequestDataConnectionMessage());
        return Assert.IsType<DataConnectionTokenMessage>(await ReadControlAsync(stream));
    }

    private static async Task<BindDataConnectionAckMessage> BindAsync(NetworkStream stream, string token)
    {
        await SendControlAsync(stream, new BindDataConnectionMessage(token));
        return Assert.IsType<BindDataConnectionAckMessage>(await ReadControlAsync(stream));
    }

    private static ValueTask SendControlAsync(NetworkStream stream, Message message)
        => FrameCodec.WriteAsync(stream, Frame.Control(FrameContract.ControlChannel,
            MessageCodec.Serialize(message)));

    private static async Task<Message?> ReadControlAsync(NetworkStream stream)
    {
        var frame = await FrameCodec.ReadAsync(stream).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameType.Control, frame.Type);
        Assert.Equal(FrameContract.ControlChannel, frame.ChannelId);
        return MessageCodec.Deserialize(frame.Payload);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class StreamConnection(Stream stream) : ITunnelConnection
    {
        public Stream Stream { get; } = stream;
        public string RemoteEndPoint => "test";
        public ValueTask DisposeAsync()
        {
            Stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GateWriteStream : Stream
    {
        private readonly TaskCompletionSource _firstWriteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<byte[]> _writes = [];
        private int _writeCount;

        public Task FirstWriteStarted => _firstWriteStarted.Task;
        public IReadOnlyList<byte[]> Writes
        {
            get { lock (_writes) return _writes.ToArray(); }
        }

        public void ReleaseWrites() => _release.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                _firstWriteStarted.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            lock (_writes)
                _writes.Add(buffer.ToArray());
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
