using System.Net;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

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

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = await ConnectAgentAsync(listener, TestToken, "agent-1",
            a => a.LogLine += s => loginTcs.TrySetResult(s));
        await using var _2 = agent;

        var loggedIn = await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("登录成功", loggedIn);

        // 注册代理成功，返回远程地址
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, remotePort: proxyPort);
        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
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
        var agent = await ConnectAgentAsync(listener, "wrong-token", "agent-bad",
            a => a.LogLine += s => loginTcs.TrySetResult(s));
        await using var _2 = agent;

        var log = await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("登录失败", log);

        // 登录失败后会话应已断开
        await Task.Delay(300);
        Assert.False(agent.IsConnected);
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
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, remotePort: proxyPort);
        var reg1 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => reg1.TrySetResult((id, ok, addr));
        var (id1, ok1, addr1) = await reg1.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(ok1);
        Assert.Equal($"0.0.0.0:{proxyPort}", addr1);

        // 第二次注册同一端口 → 冲突报错
        await agent.RegisterProxyAsync("p2", LinkType.Tcp, "127.0.0.1", 25566, remotePort: proxyPort);
        var reg2 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => reg2.TrySetResult((id, ok, addr));
        var (id2, ok2, err2) = await reg2.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(ok2);
        Assert.Contains("占用", err2);
    }
}
