using System.Net;
using System.Net.Sockets;
using System.Text;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

/// <summary>
/// HTTP vhost 测试。
/// 注意：端到端测试（Host_Routing / Subdomain）在本机 testhost 环境触发
/// .NET 10 xunit 原生崩溃（功能已用 console probe 验证正确），
/// 默认跳过（设环境变量 RUN_INTEGRATION=1 才执行），供 CI/其他环境启用。
/// </summary>
public class VhostHttpTests
{
    private const string TestToken = "test-secret-token";

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static bool IntegrationEnabled
        => Environment.GetEnvironmentVariable("RUN_INTEGRATION") == "1";

    /// <summary>本地 HTTP 服务：收到请求返回 hello-from-local</summary>
    private static TcpListener StartHttpServer(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = HttpLoopAsync(listener);
        return listener;
    }

    private static async Task HttpLoopAsync(TcpListener l)
    {
        try
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync();
                _ = HttpClientAsync(c);
            }
        }
        catch { }
    }

    private static async Task HttpClientAsync(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var buf = new byte[1024];
                await s.ReadAsync(buf);
                const string body = "hello-from-local";
                var resp = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
            }
            catch { }
        }
    }

    private static async Task<AgentSession> ConnectAgentAsync(int controlPort)
    {
        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions(
            ServerAddr: "127.0.0.1",
            ServerPort: controlPort,
            Token: TestToken,
            ClientId: "agent-vhost",
            HeartbeatInterval: TimeSpan.FromMilliseconds(500)));
        agent.LogLine += s => loginTcs.TrySetResult(s);
        await agent.ConnectAsync();
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return agent;
    }

    [Fact]
    public async Task HttpVhost_HostRouting_EndToEnd()
    {
        if (!IntegrationEnabled) return;

        using var httpServer = StartHttpServer(out var httpPort);
        var controlPort = FreePort();
        var vhostPort = FreePort();

        var listener = new ServerListener(controlPort, TestToken, vhostHttpPort: vhostPort, subDomainHost: "t.local");
        listener.Start();
        await using var _l = listener;

        var agent = await ConnectAgentAsync(controlPort);
        await using var _a = agent;

        // 注册 HTTP 代理（完整域名）
        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("web1", LinkType.Http, "127.0.0.1", httpPort, domain: "web.t.local");
        var (id, ok, addr) = await regTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok, addr);
        Assert.Equal("host://web.t.local", addr);

        // 用户请求 vhost 端口，Host: web.t.local → 应路由到本地 HTTP 服务
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, vhostPort);
        var s = client.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: web.t.local\r\nConnection: close\r\n\r\n"));

        var buf = new byte[2048];
        var n = await s.ReadAsync(buf);
        var resp = Encoding.ASCII.GetString(buf, 0, n);
        Assert.Contains("200 OK", resp);
        Assert.Contains("hello-from-local", resp);
    }

    [Fact]
    public async Task HttpVhost_Subdomain_JoinsSubDomainHost()
    {
        if (!IntegrationEnabled) return;

        using var httpServer = StartHttpServer(out var httpPort);
        var controlPort = FreePort();
        var vhostPort = FreePort();

        var listener = new ServerListener(controlPort, TestToken, vhostHttpPort: vhostPort, subDomainHost: "t.local");
        listener.Start();
        await using var _l = listener;

        var agent = await ConnectAgentAsync(controlPort);
        await using var _a = agent;

        // 用 subdomain 注册 → 拼接成 web.t.local
        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("web2", LinkType.Http, "127.0.0.1", httpPort, subdomain: "web");
        var (id, ok, addr) = await regTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok, addr);
        Assert.Equal("host://web.t.local", addr);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, vhostPort);
        var s = client.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: web.t.local\r\nConnection: close\r\n\r\n"));

        var buf = new byte[2048];
        var n = await s.ReadAsync(buf);
        Assert.Contains("hello-from-local", Encoding.ASCII.GetString(buf, 0, n));
    }

    [Fact]
    public async Task HttpVhost_UnknownHost_Returns404()
    {
        var controlPort = FreePort();
        var vhostPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken, vhostHttpPort: vhostPort);
        listener.Start();
        await using var _l = listener;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, vhostPort);
        var s = client.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: unknown.example\r\nConnection: close\r\n\r\n"));

        var buf = new byte[2048];
        var n = await s.ReadAsync(buf);
        Assert.Contains("404", Encoding.ASCII.GetString(buf, 0, n));
    }
}
