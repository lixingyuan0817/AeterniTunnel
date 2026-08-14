using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

/// <summary>
/// S→C 指令闭环（服务端主动删除隧道）：
/// 服务端 RemoveProxyAsync → 下发 RemoveProxyCommandMessage → 客户端本地清理并回 CommandAckMessage。
/// 关键防护：客户端从重连重注册列表移除目标，避免「僵尸复活」。
/// </summary>
public class CommandTests
{
    private const string TestToken = "test-secret-token";

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<AgentSession> ConnectAgentAsync(int controlPort, string clientId)
    {
        var agent = new AgentSession(new AgentOptions(
            ServerAddr: "127.0.0.1",
            ServerPort: controlPort,
            Token: TestToken,
            ClientId: clientId,
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));
        await agent.ConnectAsync();
        return agent;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task ServerRemoveProxy_NotifiesClient_Acks_AndClearsServer()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _l = listener;

        var agent = await ConnectAgentAsync(controlPort, "agent-1");
        await using var _a = agent;

        // 注册隧道并等待成功
        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, proxyPort);
        var (_, ok1, _) = await regTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok1);

        // 订阅客户端本地移除事件（须在删除前）
        var removedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRemoved += id => removedTcs.TrySetResult(id);

        // 服务端主动删除该客户端隧道
        var removed = await listener.RemoveProxyAsync("agent-1", "p1");
        Assert.True(removed, "客户端在线时删除应返回 true");

        // 客户端收到指令并本地清理
        var removedId = await removedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("p1", removedId);

        // 服务端快照中该隧道消失
        await WaitForAsync(() =>
        {
            var client = listener.GetStatusSnapshot().Clients.FirstOrDefault(c => c.ClientId == "agent-1");
            return client is null || !client.Proxies.Any(p => p.ProxyId == "p1");
        });
        Assert.DoesNotContain(
            listener.GetStatusSnapshot().Clients.First(c => c.ClientId == "agent-1").Proxies,
            p => p.ProxyId == "p1");

        // 客户端连接保持
        Assert.True(agent.IsConnected);
    }

    [Fact]
    public async Task ServerRemoveProxy_ClientReconnect_DoesNotReregister()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();

        var agent = await ConnectAgentAsync(controlPort, "agent-rc");
        // 持续收集注册事件（重连后若僵尸复活会出现第二条 p1）
        var regEvents = new List<(string, bool, string?)>();
        agent.ProxyRegistered += (id, ok, addr) =>
        {
            lock (regEvents) regEvents.Add((id, ok, addr));
        };
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, proxyPort);
        await WaitForAsync(() =>
        {
            lock (regEvents) return regEvents.Count > 0;
        });
        lock (regEvents) Assert.True(regEvents[0].Item2);

        // 服务端删除隧道 → 客户端本地清理（含重连重注册列表）
        var removedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRemoved += id => removedTcs.TrySetResult(id);
        Assert.True(await listener.RemoveProxyAsync("agent-rc", "p1"));
        Assert.Equal("p1", await removedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // 模拟服务端重启：断开 → 同端口重启 → Agent 自动重连
        await listener.DisposeAsync();
        var reconnectDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < reconnectDeadline && agent.IsConnected)
            await Task.Delay(50);

        var listener2 = new ServerListener(controlPort, TestToken);
        listener2.Start();
        await using var _l2 = listener2;

        // 等重连完成 + 重注册消息到达
        reconnectDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < reconnectDeadline && !agent.IsConnected)
            await Task.Delay(50);
        Assert.True(agent.IsConnected, "客户端应自动重连");
        await Task.Delay(500); // 留出重注册消息传输时间

        // 关键断言：p1 不应被重新注册（僵尸复活防护）
        lock (regEvents)
        {
            Assert.Single(regEvents); // 只有初始注册一条，无重连重注册
        }
        var snapshot = listener2.GetStatusSnapshot().Clients.FirstOrDefault(c => c.ClientId == "agent-rc");
        Assert.NotNull(snapshot);
        Assert.DoesNotContain(snapshot.Proxies, p => p.ProxyId == "p1");

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task ServerRemoveProxy_UnknownProxy_IdempotentAndKeepsConnection()
    {
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _l = listener;

        var agent = await ConnectAgentAsync(controlPort, "agent-2");
        await using var _a = agent;

        // 客户端在线但从未注册 p-ghost：删除应幂等成功且连接保持
        // 先订阅 ack（重试循环内第一次下发即触发 ProxyRemoved），再调用
        var ackTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRemoved += id => ackTcs.TrySetResult(id);

        // 服务端 session 注册与 ConnectAgentAsync 返回存在时序竞态，限时重试
        var removed = false;
        for (var i = 0; i < 50 && !removed; i++)
        {
            removed = await listener.RemoveProxyAsync("agent-2", "p-ghost");
            if (!removed) await Task.Delay(100);
        }
        Assert.True(removed);

        Assert.Equal("p-ghost", await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(agent.IsConnected);
    }

    [Fact]
    public async Task ServerRemoveProxy_OfflineClient_ReturnsFalse()
    {
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _l = listener;

        // 客户端不存在
        var removed = await listener.RemoveProxyAsync("ghost-client", "p1");
        Assert.False(removed);
    }

    [Fact]
    public async Task ServerRemoveVhostProxy_ClearsHostRouting()
    {
        var controlPort = FreePort();
        var vhostHttpPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken, vhostHttpPort: vhostHttpPort);
        listener.Start();
        await using var _l = listener;
        Assert.NotNull(listener.VhostHttp);

        var agent = await ConnectAgentAsync(controlPort, "agent-vhost");
        await using var _a = agent;

        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("web", LinkType.Http, "127.0.0.1", 8080, domain: "web.example.com");
        var (_, ok, _) = await regTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok);

        // vhost 已路由
        Assert.True(listener.VhostHttp!.Contains("web.example.com"));

        // 服务端删除 vhost 隧道 → 客户端清理 + 服务端路由摘除（修复：vhost 注销此前不清理 _vhostHosts）
        var removedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRemoved += id => removedTcs.TrySetResult(id);
        Assert.True(await listener.RemoveProxyAsync("agent-vhost", "web"));
        Assert.Equal("web", await removedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await WaitForAsync(() => !listener.VhostHttp!.Contains("web.example.com"));
        Assert.False(listener.VhostHttp.Contains("web.example.com"), "vhost 应从路由表摘除");
    }
}
