using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Server;
using Aeterni.Tunnel.Engine.Wire;

namespace Aeterni.Tunnel.Engine.Tests;

public class ControlPlaneTests
{
    private const string TestToken = "test-secret-token";

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static ValueTask SendControlAsync(NetworkStream stream, Message message)
        => FrameCodec.WriteAsync(stream, Frame.Control(
            FrameContract.ControlChannel, MessageCodec.Serialize(message)));

    private static async Task<Message?> ReadControlAsync(NetworkStream stream)
    {
        var frame = await FrameCodec.ReadAsync(stream).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameType.Control, frame.Type);
        Assert.Equal(FrameContract.ControlChannel, frame.ChannelId);
        return MessageCodec.Deserialize(frame.Payload);
    }

    private static async Task WaitForClientCountAsync(ServerListener listener, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (listener.GetStatusSnapshot().Clients.Count == expected)
                return;
            await Task.Delay(25);
        }
        Assert.Equal(expected, listener.GetStatusSnapshot().Clients.Count);
    }

    /// <summary>先订阅事件再连接（避免 HelloAck 在订阅前到达的竞态）</summary>
    private static async Task<AgentSession> ConnectAgentAsync(
        ServerListener listener, string token, string clientId, Action<AgentSession>? beforeConnect)
    {
        var agent = new AgentSession(new AgentOptions(
            ServerAddr: "127.0.0.1",
            ServerPort: listener.BindPort,
            Token: token,
            ClientId: clientId,
            HeartbeatInterval: TimeSpan.FromMilliseconds(500)));
        beforeConnect?.Invoke(agent);
        await agent.ConnectAsync();
        return agent;
    }

    [Fact]
    public async Task Login_RegisterHeartbeat_RoundTrip()
    {
        var port = FreePort();
        var proxyPort = FreePort();
        var listener = new ServerListener(port, TestToken);
        listener.Start();
        await using var _ = listener;

        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var agent = await ConnectAgentAsync(listener, TestToken, "agent-1",
            a => a.LogLine += s => logs.Enqueue(s));
        await using var _2 = agent;

        // 等"登录成功"日志（"正在连接"之后出现）
        await WaitForLogAsync(logs, "登录成功", TimeSpan.FromSeconds(15));

        // 注册隧道成功，返回远程地址
        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, remotePort: proxyPort);
        var (proxyId, ok, remoteAddr) = await regTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("p1", proxyId);
        Assert.True(ok);
        Assert.Equal($"0.0.0.0:{proxyPort}", remoteAddr);

        // 心跳持续期间连接保持（500ms 间隔，等 1.5s 断言仍在线）
        await Task.Delay(1500);
        Assert.True(agent.IsConnected);
    }

    [Fact]
    public async Task Login_WithWrongToken_Fails()
    {
        var port = FreePort();
        var proxyPort = FreePort();
        var listener = new ServerListener(port, TestToken);
        listener.Start();
        await using var _ = listener;

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions(
            ServerAddr: "127.0.0.1",
            ServerPort: port,
            Token: "wrong-token",
            ClientId: "agent-bad",
            HeartbeatInterval: TimeSpan.FromMilliseconds(500)));
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        agent.LogLine += s => { logs.Enqueue(s); loginTcs.TrySetResult(s); };
        await using var _2 = agent;

        // 握手校验：token 不匹配 → ConnectAsync 抛异常（不再误判"已连接"）
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ConnectAsync());
        Assert.False(agent.IsConnected);

        // 日志含失败原因（"正在连接"之后应有"登录失败"）
        await WaitForLogAsync(logs, "登录失败", TimeSpan.FromSeconds(5));
        Assert.Contains(logs, x => x.Contains("登录失败"));
        await WaitForClientCountAsync(listener, 0);
    }

    [Fact]
    public async Task Login_WithEmptyClientId_FailsAndClosesServerSession()
    {
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _listener = listener;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, controlPort);
        await SendControlAsync(tcp.GetStream(), new HelloMessage(" ", 1, TestToken, "host"));

        var ack = Assert.IsType<HelloAckMessage>(await ReadControlAsync(tcp.GetStream()));
        Assert.False(ack.Ok);
        Assert.Contains("clientId", ack.Error);
        await WaitForClientCountAsync(listener, 0);
    }

    [Fact]
    public async Task ControlMessages_BeforeHello_AreRejectedWithoutAllocatingResources()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();
        var ports = new PortManager();
        var listener = new ServerListener(controlPort, TestToken, ports);
        listener.Start();
        await using var _listener = listener;

        Message[] messages =
        [
            new RegisterProxyMessage("unauthorized", LinkType.Tcp, "127.0.0.1", 25565,
                proxyPort, null, null),
            new UnregisterProxyMessage("unauthorized"),
            new CommandAckMessage("removeProxy", "unauthorized", true),
            new HeartbeatMessage(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ];

        foreach (var message in messages)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, controlPort);
            await SendControlAsync(tcp.GetStream(), message);

            var error = Assert.IsType<ErrorMessage>(await ReadControlAsync(tcp.GetStream()));
            Assert.Equal(401, error.Code);
            await WaitForClientCountAsync(listener, 0);
        }

        Assert.False(ports.IsAllocated(proxyPort));
        using var probe = new TcpListener(IPAddress.Loopback, proxyPort);
        probe.Start();
    }

    [Fact]
    public async Task DuplicateHello_IsRejectedAndClosesAuthenticatedSession()
    {
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _listener = listener;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, controlPort);
        var stream = tcp.GetStream();

        await SendControlAsync(stream, new HelloMessage("duplicate", 1, TestToken, "host"));
        var firstAck = Assert.IsType<HelloAckMessage>(await ReadControlAsync(stream));
        Assert.True(firstAck.Ok, firstAck.Error);
        Assert.IsType<PortPolicyMessage>(await ReadControlAsync(stream));
        Assert.NotNull(listener.GetSession("duplicate"));

        await SendControlAsync(stream, new HelloMessage("duplicate", 1, TestToken, "host"));
        var duplicateAck = Assert.IsType<HelloAckMessage>(await ReadControlAsync(stream));
        Assert.False(duplicateAck.Ok);
        Assert.Contains("已完成登录", duplicateAck.Error);
        await WaitForClientCountAsync(listener, 0);
        Assert.Null(listener.GetSession("duplicate"));
    }

    private static async Task WaitForLogAsync(System.Collections.Concurrent.ConcurrentQueue<string> logs, string needle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (logs.Any(x => x.Contains(needle)))
                return;
            await Task.Delay(50);
        }
        Assert.Contains(logs, x => x.Contains(needle));
    }

    [Fact]
    public async Task RegisterProxy_PortConflict_ReportsError()
    {
        var port = FreePort();
        var proxyPort = FreePort();
        var listener = new ServerListener(port, TestToken);
        listener.Start();
        await using var _ = listener;

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = await ConnectAgentAsync(listener, TestToken, "agent-2",
            a => a.LogLine += s => loginTcs.TrySetResult(s));
        await using var _2 = agent;
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // 第一次注册指定端口成功
        var reg1 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => reg1.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, remotePort: proxyPort);
        var (id1, ok1, addr1) = await reg1.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(ok1);
        Assert.Equal($"0.0.0.0:{proxyPort}", addr1);

        // 第二次注册同一端口 → 冲突报错
        var reg2 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => reg2.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("p2", LinkType.Tcp, "127.0.0.1", 25566, remotePort: proxyPort);
        var (id2, ok2, err2) = await reg2.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(ok2);
        Assert.Contains("占用", err2);
    }
}
