using System.Net;
using System.Net.Sockets;
using System.Text;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

/// <summary>E5：allowPorts 白名单（FR-041）、Dashboard /api/status（FR-042）</summary>
public class DashboardTests
{
    private const string TestToken = "test-secret-token";

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<(ServerListener Listener, AgentSession Agent)> SetupAsync(
        PortManager? ports = null, int dashboardPort = 0)
    {
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken, ports, dashboardPort: dashboardPort);
        listener.Start();

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions("127.0.0.1", controlPort, TestToken, "agent-dash",
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));
        agent.LogLine += s => loginTcs.TrySetResult(s);
        await agent.ConnectAsync();
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (listener, agent);
    }

    private static async Task<(string, bool, string?)> RegisterAsync(AgentSession agent, string id, int port)
    {
        var tcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (pid, ok, addr) => tcs.TrySetResult((pid, ok, addr));
        await agent.RegisterProxyAsync(id, LinkType.Tcp, "127.0.0.1", port + 1000, port);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AllowPorts_RejectsOutOfRange()
    {
        var (listener, agent) = await SetupAsync(ports: new PortManager(allowed: [new PortRange(50000, 50100)]));
        await using var _l = listener;
        await using var _a = agent;

        var (idIn, okIn, _) = await RegisterAsync(agent, "in-range", 50050);
        Assert.True(okIn);

        var (idOut, okOut, err) = await RegisterAsync(agent, "out-range", 20001);
        Assert.False(okOut);
        Assert.Contains("不在允许范围", err);
    }

    [Fact]
    public async Task Dashboard_StatusJson_ShowsClientsAndProxies()
    {
        var dashPort = FreePort();
        var (listener, agent) = await SetupAsync(dashboardPort: dashPort);
        await using var _l = listener;
        await using var _a = agent;

        var (_, ok, _) = await RegisterAsync(agent, "p1", 50010);
        Assert.True(ok);

        // 请求 /api/status
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, dashPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /api/status HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

        var buf = new byte[8192];
        var n = await stream.ReadAsync(buf);
        var resp = Encoding.UTF8.GetString(buf, 0, n);

        Assert.Contains("200 OK", resp);
        Assert.Contains("\"clients\"", resp);
        Assert.Contains("agent-dash", resp);
        Assert.Contains("\"p1\"", resp);
    }

    [Fact]
    public async Task Dashboard_UnknownPath_Returns404()
    {
        var dashPort = FreePort();
        var (listener, _) = await SetupAsync(dashboardPort: dashPort);
        await using var _l = listener;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, dashPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /nope HTTP/1.1\r\nConnection: close\r\n\r\n"));

        var buf = new byte[2048];
        var n = await stream.ReadAsync(buf);
        Assert.Contains("404", Encoding.ASCII.GetString(buf, 0, n));
    }

    [Fact]
    public async Task Dashboard_Auth_RequiredWhenConfigured()
    {
        var dashPort = FreePort();
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken, dashboardPort: dashPort,
            dashboardUser: "admin", dashboardPassword: "secret");
        listener.Start();
        await using var _l = listener;

        // 无凭证 → 401
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, dashPort);
            var s = client.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes("GET /api/status HTTP/1.1\r\nConnection: close\r\n\r\n"));
            var buf = new byte[2048];
            var n = await s.ReadAsync(buf);
            Assert.Contains("401", Encoding.ASCII.GetString(buf, 0, n));
        }

        // 错误凭证 → 401
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, dashPort);
            var s = client.GetStream();
            var bad = Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:wrong"));
            await s.WriteAsync(Encoding.ASCII.GetBytes($"GET /api/status HTTP/1.1\r\nAuthorization: Basic {bad}\r\nConnection: close\r\n\r\n"));
            var buf = new byte[2048];
            var n = await s.ReadAsync(buf);
            Assert.Contains("401", Encoding.ASCII.GetString(buf, 0, n));
        }

        // 正确凭证 → 200
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, dashPort);
            var s = client.GetStream();
            var good = Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:secret"));
            await s.WriteAsync(Encoding.ASCII.GetBytes($"GET /api/health HTTP/1.1\r\nAuthorization: Basic {good}\r\nConnection: close\r\n\r\n"));
            var buf = new byte[2048];
            var n = await s.ReadAsync(buf);
            Assert.Contains("200 OK", Encoding.ASCII.GetString(buf, 0, n));
        }
    }

    [Fact]
    public async Task MaxPortsPerClient_LimitsProxyCount()
    {
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken, maxPortsPerClient: 1);
        listener.Start();
        await using var _l = listener;

        // 连接 agent（先订阅再连接）
        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions("127.0.0.1", controlPort, TestToken, "max-ports",
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));
        agent.LogLine += s => loginTcs.TrySetResult(s);
        await agent.ConnectAsync();
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var _a = agent;

        // 第 1 个端口代理 → 成功
        var reg1 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => reg1.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, FreePort());
        var (_, ok1, _) = await reg1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok1);

        // 第 2 个端口代理 → 超过上限拒绝
        var reg2 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => reg2.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("p2", LinkType.Tcp, "127.0.0.1", 25566, FreePort());
        var (_, ok2, err2) = await reg2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ok2);
        Assert.Contains("上限", err2);
    }
}
