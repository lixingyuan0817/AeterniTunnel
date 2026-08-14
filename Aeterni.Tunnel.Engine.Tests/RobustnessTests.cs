using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

/// <summary>E5 健壮性：断线重连（FR-014）、多客户端并存（FR-043）</summary>
public class RobustnessTests
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

    private static async Task WaitConnectedAsync(AgentSession agent, bool expect, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (agent.IsConnected == expect)
                return;
            await Task.Delay(100);
        }
        Assert.Equal(expect, agent.IsConnected);
    }

    [Fact]
    public async Task Agent_Reconnects_AndReregisters_AfterServerRestart()
    {
        var controlPort = FreePort();
        var proxyPort = FreePort();

        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions("127.0.0.1", controlPort, TestToken, "agent-rc",
            HeartbeatInterval: TimeSpan.FromMilliseconds(300)));
        agent.LogLine += s => loginTcs.TrySetResult(s);
        await agent.ConnectAsync();
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(agent.IsConnected);

        // 注册代理（事件订阅放在最前，持续收集所有注册结果）
        var regEvents = new List<(string, bool, string?)>();
        agent.ProxyRegistered += (id, ok, addr) =>
        {
            lock (regEvents) regEvents.Add((id, ok, addr));
        };
        await agent.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, proxyPort);

        // 等待首次注册结果
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            bool hasEvent;
            lock (regEvents) hasEvent = regEvents.Count > 0;
            if (hasEvent) break;
            await Task.Delay(50);
        }
        lock (regEvents)
        {
            Assert.NotEmpty(regEvents);
            Assert.True(regEvents[0].Item2, regEvents[0].Item3);
        }

        // 关闭 Server → Agent 断开
        await listener.DisposeAsync();
        await WaitConnectedAsync(agent, expect: false);

        // 同端口重启 Server → Agent 自动重连并重注册
        var listener2 = new ServerListener(controlPort, TestToken);
        listener2.Start();
        await using var _l2 = listener2;

        // 等待重连 + 重注册（应出现第二条 p1 成功事件）
        var reconnectDeadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < reconnectDeadline)
        {
            lock (regEvents)
            {
                if (regEvents.Count >= 2)
                    break;
            }
            await Task.Delay(100);
        }

        lock (regEvents)
        {
            Assert.True(regEvents.Count >= 2, $"重注册未发生（仅 {regEvents.Count} 条事件）");
            var last = regEvents[^1];
            Assert.Equal("p1", last.Item1);
            Assert.True(last.Item2, last.Item3);
        }

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task FastRestart_SameClientId_PortReleasedImmediately()
    {
        // 场景：客户端退出后快速重启（同 clientId），注册同一端口应立即成功，
        // 不再等服务端心跳过期（旧会话由连接断开/同 clientId 替换即时清理）
        var controlPort = FreePort();
        var proxyPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _l = listener;

        // 第一次连接 + 注册
        var agent1 = await ConnectAgentAsync(controlPort, "fast-restart");
        var reg1 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent1.ProxyRegistered += (id, ok, addr) => reg1.TrySetResult((id, ok, addr));
        await agent1.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, proxyPort);
        var (_, ok1, _) = await reg1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok1);

        // 客户端退出
        await agent1.DisposeAsync();

        // 快速重启：同 clientId 立即重连并注册同一端口（不等 15s 心跳超时）
        var agent2 = await ConnectAgentAsync(controlPort, "fast-restart");
        await using var _a2 = agent2;
        var reg2 = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent2.ProxyRegistered += (id, ok, addr) => reg2.TrySetResult((id, ok, addr));
        await agent2.RegisterProxyAsync("p1", LinkType.Tcp, "127.0.0.1", 25565, proxyPort);
        var (_, ok2, addr2) = await reg2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ok2, $"快速重启后注册同端口应成功（当前错误：{addr2}）");
    }

    [Fact]
    public async Task MultipleAgents_Coexist_WithIsolatedPorts()
    {
        var controlPort = FreePort();
        var listener = new ServerListener(controlPort, TestToken);
        listener.Start();
        await using var _l = listener;

        var agentA = await ConnectAgentAsync(controlPort, "agent-a");
        var agentB = await ConnectAgentAsync(controlPort, "agent-b");
        await using var _a = agentA;
        await using var _b = agentB;
        await Task.Delay(300);

        var regA = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agentA.ProxyRegistered += (id, ok, addr) => regA.TrySetResult((id, ok, addr));
        await agentA.RegisterProxyAsync("pa", LinkType.Tcp, "127.0.0.1", 10001, FreePort());
        var (ia, oka, addra) = await regA.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var regB = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agentB.ProxyRegistered += (id, ok, addr) => regB.TrySetResult((id, ok, addr));
        await agentB.RegisterProxyAsync("pb", LinkType.Tcp, "127.0.0.1", 10002, FreePort());
        var (ib, okb, addrb) = await regB.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(oka, addra);
        Assert.True(okb, addrb);
        Assert.NotEqual(addra, addrb);
    }
}
