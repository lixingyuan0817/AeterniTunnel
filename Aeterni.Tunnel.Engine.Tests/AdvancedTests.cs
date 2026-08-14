using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;
using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

/// <summary>E6：TLS 传输端到端（FR-050）、背压（FR-024）、配置热更新（FR-015）</summary>
public class AdvancedTests
{
    private const string TestToken = "test-secret-token";

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var raw = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        const string pwd = "aeterni-test";
        var pfx = raw.Export(X509ContentType.Pkcs12, pwd);
        return X509CertificateLoader.LoadPkcs12(pfx, pwd);
    }

    [Fact]
    public async Task TlsTransport_EndToEnd_RegisterAndProbe()
    {
        using var cert = CreateSelfSignedCert();
        var controlPort = FreePort();

        var listener = new ServerListener(controlPort, TestToken, tlsCertificate: cert);
        listener.Start();
        await using var _l = listener;

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions("127.0.0.1", controlPort, TestToken, "agent-tls",
            UseTls: true, ValidateCertificate: false));
        agent.LogLine += s => loginTcs.TrySetResult(s);
        await agent.ConnectAsync();
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(agent.IsConnected);

        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("ptls", LinkType.Tcp, "127.0.0.1", 25565, FreePort());
        var (_, ok, addr) = await regTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok, addr);
    }

    [Fact]
    public async Task Channel_Backpressure_BlocksWriterUntilConsumed()
    {
        var port = FreePort();
        var serverTransport = TcpTlsTransport.Server(IPAddress.Loopback, port);
        var clientTransport = TcpTlsTransport.Client("127.0.0.1", port, useTls: false);

        var clientTask = clientTransport.ConnectAsync("127.0.0.1", port).AsTask();
        var serverConn = await serverTransport.AcceptAsync();
        var clientConn = await clientTask;

        var serverMux = new ChannelMultiplexer(serverConn);
        serverMux.Start();
        var clientMux = new ChannelMultiplexer(clientConn);
        clientMux.Start();

        var clientCh = clientMux.OpenChannel();
        var serverCh = serverMux.AcceptChannel(clientCh.ChannelId);

        // 不消费：写入量（100 × 64KB ≈ 6.4MB）远超 socket 缓冲 + 队列（64×64KB），写方应被背压阻塞
        var payload = new byte[64 * 1024];
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
                await clientCh.WriteAsync(payload);
        });

        var blocked = await Task.WhenAny(writer, Task.Delay(800));
        Assert.NotEqual(writer, blocked); // 写方被背压阻塞

        // 消费后写方得以继续
        var consumed = 0;
        while (consumed < 200 && await serverCh.ReadAsync() is not null)
            consumed++;
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, consumed);

        await serverMux.DisposeAsync();
        await clientMux.DisposeAsync();
        await serverTransport.DisposeAsync();
    }

    [Fact]
    public async Task HotUpdate_IncrementalRegisterUnregister()
    {
        var controlPort = FreePort();
        var ports = new PortManager();
        var listener = new ServerListener(controlPort, TestToken, ports);
        listener.Start();
        await using var _l = listener;

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions("127.0.0.1", controlPort, TestToken, "agent-hot",
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));
        agent.LogLine += s => loginTcs.TrySetResult(s);
        await agent.ConnectAsync();
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var regEvents = new List<(string, bool, string?)>();
        agent.ProxyRegistered += (id, ok, addr) =>
        {
            lock (regEvents) regEvents.Add((id, ok, addr));
        };

        async Task<(bool, string?)> RegisterAsync(string id, int port)
        {
            var before = 0;
            lock (regEvents) before = regEvents.Count;
            await agent.RegisterProxyAsync(id, LinkType.Tcp, "127.0.0.1", port, port);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (regEvents)
                {
                    if (regEvents.Count > before)
                        return (regEvents[^1].Item2, regEvents[^1].Item3);
                }
                await Task.Delay(50);
            }
            return (false, "timeout");
        }

        var proxyA = FreePort();
        var proxyB = FreePort();

        var (ok1, _) = await RegisterAsync("p1", proxyA);
        Assert.True(ok1);

        var (ok2, _) = await RegisterAsync("p2", proxyB);
        Assert.True(ok2);

        // 注销 p1 → 端口释放，可复用同端口重新注册
        await agent.UnregisterProxyAsync("p1");
        await Task.Delay(200);

        var (ok3, addr3) = await RegisterAsync("p1b", proxyA);
        Assert.True(ok3, addr3);
        Assert.Contains($":{proxyA}", addr3);
    }
}
