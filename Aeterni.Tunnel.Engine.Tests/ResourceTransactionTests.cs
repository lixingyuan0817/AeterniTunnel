using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

public class ResourceTransactionTests
{
    private const string TestToken = "test-secret-token";

    [Fact]
    public async Task QuotaRejection_DoesNotReserveRejectedPort()
    {
        var controlPort = FreePort();
        var acceptedPort = FreePort();
        var rejectedPort = FreePort();
        var ports = new PortManager();
        await using var listener = new ServerListener(controlPort, TestToken, ports, maxPortsPerClient: 1);
        listener.Start();
        await using var agent = await ConnectAgentAsync(controlPort, "quota-agent");

        var first = await RegisterAsync(agent, "accepted", LinkType.Tcp, acceptedPort);
        Assert.True(first.Ok, first.Detail);

        var second = await RegisterAsync(agent, "rejected", LinkType.Tcp, rejectedPort);
        Assert.False(second.Ok);
        Assert.Contains("上限", second.Detail);
        Assert.False(ports.IsAllocated(rejectedPort));
        using var probe = new TcpListener(IPAddress.Any, rejectedPort);
        probe.Start();
    }

    [Fact]
    public async Task ListenerBindFailure_RollsBackPortAndAllowsRetry()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();
        var ports = new PortManager();
        await using var listener = new ServerListener(controlPort, TestToken, ports);
        listener.Start();
        await using var agent = await ConnectAgentAsync(controlPort, "bind-agent");

        using (var blocker = new TcpListener(IPAddress.Any, proxyPort))
        {
            blocker.Start();
            var failed = await RegisterAsync(agent, "blocked", LinkType.Tcp, proxyPort);
            Assert.False(failed.Ok);
            Assert.False(ports.IsAllocated(proxyPort));
            Assert.Empty(listener.GetSession("bind-agent")!.GetProxiesSnapshot());
        }

        var retry = await RegisterAsync(agent, "retry", LinkType.Tcp, proxyPort);
        Assert.True(retry.Ok, retry.Detail);
        Assert.True(ports.IsAllocated(proxyPort));
    }

    [Fact]
    public async Task DuplicateProxyId_IsRejectedWithoutReplacingCommittedListener()
    {
        var controlPort = FreePort();
        var firstPort = FreePort();
        var secondPort = FreePort();
        var ports = new PortManager();
        await using var listener = new ServerListener(controlPort, TestToken, ports);
        listener.Start();
        await using var agent = await ConnectAgentAsync(controlPort, "duplicate-agent");

        var first = await RegisterAsync(agent, "same-id", LinkType.Tcp, firstPort);
        Assert.True(first.Ok, first.Detail);
        var duplicate = await RegisterAsync(agent, "same-id", LinkType.Tcp, secondPort);

        Assert.False(duplicate.Ok);
        Assert.Contains("已注册", duplicate.Detail);
        Assert.True(ports.IsAllocated(firstPort));
        Assert.False(ports.IsAllocated(secondPort));
        var proxy = Assert.Single(listener.GetSession("duplicate-agent")!.GetProxiesSnapshot());
        Assert.Equal($"0.0.0.0:{firstPort}", proxy.RemoteAddr);
    }

    [Fact]
    public async Task ConcurrentSessions_OnlyOneCanCommitSamePort()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();
        var ports = new PortManager();
        await using var listener = new ServerListener(controlPort, TestToken, ports);
        listener.Start();
        await using var firstAgent = await ConnectAgentAsync(controlPort, "contender-a");
        await using var secondAgent = await ConnectAgentAsync(controlPort, "contender-b");

        var attempts = await Task.WhenAll(
            RegisterAsync(firstAgent, "proxy-a", LinkType.Tcp, proxyPort),
            RegisterAsync(secondAgent, "proxy-b", LinkType.Tcp, proxyPort));

        Assert.Single(attempts, x => x.Ok);
        Assert.Single(attempts, x => !x.Ok);
        Assert.Contains("占用", attempts.Single(x => !x.Ok).Detail);
        Assert.True(ports.IsAllocated(proxyPort));
    }

    [Fact]
    public async Task VhostCollision_IsRejectedAndDisconnectReleasesOwnedRoute()
    {
        var controlPort = FreePort();
        var vhostPort = FreePort();
        await using var listener = new ServerListener(controlPort, TestToken, vhostHttpPort: vhostPort);
        listener.Start();
        var firstAgent = await ConnectAgentAsync(controlPort, "vhost-owner");
        await using var secondAgent = await ConnectAgentAsync(controlPort, "vhost-contender");

        var first = await RegisterAsync(firstAgent, "web-a", LinkType.Http, domain: "shared.example");
        Assert.True(first.Ok, first.Detail);
        var collision = await RegisterAsync(secondAgent, "web-b", LinkType.Http, domain: "SHARED.example");
        Assert.False(collision.Ok);
        Assert.Contains("占用", collision.Detail);
        Assert.True(listener.VhostHttp!.Contains("shared.example"));

        await firstAgent.DisposeAsync();
        await WaitUntilAsync(() => !listener.VhostHttp.Contains("shared.example"));

        var retry = await RegisterAsync(secondAgent, "web-c", LinkType.Http, domain: "shared.example");
        Assert.True(retry.Ok, retry.Detail);
    }

    [Fact]
    public async Task Disconnect_WaitsForListenerCleanupAndReleasesPort()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();
        var ports = new PortManager();
        await using var listener = new ServerListener(controlPort, TestToken, ports);
        listener.Start();
        var agent = await ConnectAgentAsync(controlPort, "disconnect-agent");

        var registered = await RegisterAsync(agent, "proxy", LinkType.Tcp, proxyPort);
        Assert.True(registered.Ok, registered.Detail);
        await agent.DisposeAsync();

        await WaitUntilAsync(() => !ports.IsAllocated(proxyPort));
        using var probe = new TcpListener(IPAddress.Any, proxyPort);
        probe.Start();
    }

    [Fact]
    public void PortManager_ConcurrentAllocationAndRelease_IsAtomic()
    {
        var port = FreePort();
        var ports = new PortManager(allowed: [new PortRange(port, port)]);
        var outcomes = new bool[16];

        Parallel.For(0, outcomes.Length, i =>
        {
            try
            {
                outcomes[i] = ports.Allocate(port) == port;
            }
            catch (InvalidOperationException)
            {
                outcomes[i] = false;
            }
        });

        Assert.Single(outcomes, x => x);
        ports.Release(port);
        Assert.Equal(port, ports.Allocate(port));
    }

    private static async Task<AgentSession> ConnectAgentAsync(int controlPort, string clientId)
    {
        var agent = new AgentSession(new AgentOptions(
            "127.0.0.1", controlPort, TestToken, clientId,
            UseTls: false,
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));
        await agent.ConnectAsync();
        return agent;
    }

    private static async Task<(bool Ok, string? Detail)> RegisterAsync(
        AgentSession agent, string proxyId, LinkType linkType, int? remotePort = null, string? domain = null)
    {
        var result = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string id, bool ok, string? detail)
        {
            if (id == proxyId)
                result.TrySetResult((ok, detail));
        }

        agent.ProxyRegistered += Handler;
        try
        {
            await agent.RegisterProxyAsync(proxyId, linkType, "127.0.0.1", 12345, remotePort, domain);
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            agent.ProxyRegistered -= Handler;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.True(condition());
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
