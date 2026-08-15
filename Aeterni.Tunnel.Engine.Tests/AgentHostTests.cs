using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

public class AgentHostTests
{
    private const string TestToken = "test-token";

    /// <summary>健康检查摘除/恢复：注入 fake checker 验证 AgentHost 逻辑（探测正确性由 HealthCheckerTests 覆盖）</summary>
    [Fact]
    public async Task HealthCheck_RemovesAndRestoresProxy()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();

        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _l = listener;

        var fake = new FakeHealthChecker();
        var host = new AgentHost(new AgentOptions("127.0.0.1", controlPort, TestToken, "hc-test",
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)), healthIntervalSeconds: 1,
            checkerFactory: _ => fake);
        host.AddProxy(new ProxyDefinition("svc", LinkType.Tcp, "127.0.0.1", 25565, proxyPort));
        await using var _h = host;

        var reg = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ProxyRegistered += (id, ok, _) => { if (id == "svc") reg.TrySetResult(ok); };
        await host.StartAsync();
        Assert.True(await reg.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(HasProxy(listener, "svc"));

        // 健康检查失败 → 自动摘除
        fake.SetHealthy(false);
        await WaitUntilAsync(5, () => !HasProxy(listener, "svc"));

        // 健康恢复 → 自动重新注册
        fake.SetHealthy(true);
        await WaitUntilAsync(5, () => HasProxy(listener, "svc"));
    }

    /// <summary>断线自动重连：服务端重启后 AgentSession 重连并重注册隧道（真实链路）</summary>
    [Fact]
    public async Task AutoReconnect_AfterServerRestart()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();

        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();

        var host = new AgentHost(new AgentOptions("127.0.0.1", controlPort, TestToken, "reconn",
            HeartbeatInterval: TimeSpan.FromMilliseconds(200)));
        host.AddProxy(new ProxyDefinition("p1", LinkType.Tcp, "127.0.0.1", 25565, proxyPort));
        await using var _h = host;

        var reg1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ProxyRegistered += (id, ok, _) => { if (id == "p1") reg1.TrySetResult(ok); };
        await host.StartAsync();
        Assert.True(await reg1.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // 杀服务端 → 触发断线重连
        await listener.DisposeAsync();
        await Task.Delay(1500);

        // 重启服务端（同端口）→ AgentSession 自动重连并重注册
        var listener2 = new ServerListener(controlPort, TestToken);
        listener2.Start();
        await using var _l2 = listener2;

        var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ProxyRegistered += (id, ok, _) => { if (id == "p1" && ok) reconnected.TrySetResult(true); };
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(10, () => HasProxy(listener2, "p1"));
    }

    /// <summary>配置热更新：增量增删隧道（FR-015）</summary>
    [Fact]
    public async Task Reload_IncrementalAddRemoveUpdate()
    {
        var controlPort = FreePort();

        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _l = listener;

        var host = new AgentHost(new AgentOptions("127.0.0.1", controlPort, TestToken, "reload",
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));
        host.AddProxy(new ProxyDefinition("keep", LinkType.Tcp, "127.0.0.1", 25565, 19001));
        host.AddProxy(new ProxyDefinition("old", LinkType.Tcp, "127.0.0.1", 25566, 19002));
        await using var _h = host;

        var regCount = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ProxyRegistered += (id, ok, _) =>
        {
            if (id == "keep" && ok) regCount.TrySetResult(true);
        };
        await host.StartAsync();
        Assert.True(await regCount.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntilAsync(5, () => HasProxy(listener, "old"));
        Assert.True(HasProxy(listener, "keep"));

        // 热更新：保留 keep、删除 old、更新 keep 端口、新增 fresh
        var changes = await host.ReloadAsync(new[]
        {
            new ProxyDefinition("keep", LinkType.Tcp, "127.0.0.1", 25567, 19003),
            new ProxyDefinition("fresh", LinkType.Tcp, "127.0.0.1", 8080, 19004),
        });

        Assert.Contains("移除 old", changes);
        Assert.Contains("更新 keep", changes);
        Assert.Contains("新增 fresh", changes);

        await WaitUntilAsync(5, () => !HasProxy(listener, "old"));
        await WaitUntilAsync(5, () => HasProxy(listener, "fresh"));
        await WaitUntilAsync(5, () => HasProxy(listener, "keep"));

        // keep 更新后端口应为 19003（旧 19002 已释放）
        var snap = listener.GetStatusSnapshot();
        var keepProxy = snap.Clients.SelectMany(c => c.Proxies).First(p => p.ProxyId == "keep");
        Assert.Contains("19003", keepProxy.RemoteAddr);
    }

    // ---------- helpers ----------

    private static bool HasProxy(ServerListener listener, string proxyId)
    {
        var snap = listener.GetStatusSnapshot();
        return snap.Clients.Any(c => c.Proxies.Any(p => p.ProxyId == proxyId));
    }

    private static async Task WaitUntilAsync(int seconds, Func<bool> condition, int intervalMs = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(intervalMs);
        }
        Assert.Fail($"等待条件超时（{seconds}s）");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>可控健康检查器（手动触发状态变化）</summary>
    private sealed class FakeHealthChecker : IHealthChecker
    {
        public event Action<bool>? StatusChanged;
        public bool IsHealthy { get; private set; } = true;

        public void Start() { }

        public void SetHealthy(bool healthy)
        {
            if (IsHealthy == healthy)
                return;
            IsHealthy = healthy;
            StatusChanged?.Invoke(healthy);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>端口策略下发：allowPorts + 每客户端上限在登录后下发（添加隧道前置校验数据源）</summary>
    [Fact]
    public async Task PortPolicy_DeliveredAfterLogin()
    {
        var controlPort = FreePort();
        var ports = new PortManager(allowed: new List<PortRange> { new(17061, 17062) });
        var listener = new ServerListener(controlPort, TestToken, ports: ports, maxPortsPerClient: 3);
        listener.Start();
        await using var _l = listener;

        var host = new AgentHost(new AgentOptions("127.0.0.1", controlPort, TestToken, "policy-test",
            HeartbeatInterval: TimeSpan.FromMilliseconds(200)));
        await using var _h = host;

        var got = new TaskCompletionSource<PortPolicyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PortPolicyReceived += p => got.TrySetResult(p);
        await host.StartAsync();

        var policy = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 17061, 17062 }, policy.AllowPorts);
        Assert.Equal(3, policy.MaxPortsPerClient);
    }
}
