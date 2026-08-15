using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Hosting;

namespace Aeterni.Tunnel.Desktop.Services;

/// <summary>
/// ATC 客户端服务（对应 Web 的 AeterniServerStatusService 角色）：
/// 包装 Engine 的 AgentHost，向 ViewModel 暴露连接/隧道/日志事件。
/// 事件均从引擎后台线程触发，调用方需自行调度到 UI 线程。
/// </summary>
public sealed class AgentClientService : IAsyncDisposable
{
    private readonly AgentHost _agent;

    /// <summary>引擎日志行（后台线程）</summary>
    public event Action<string>? LogReceived;

    /// <summary>隧道注册结果（后台线程）：proxyId / ok / remoteAddr|error</summary>
    public event Action<string, bool, string?>? ProxyRegistered;

    /// <summary>连接建立（含重连成功，后台线程）</summary>
    public event Action? Connected;

    /// <summary>连接断开（断线进入重连 / 停止，后台线程）</summary>
    public event Action? Disconnected;

    public AgentClientService(AgentOptions options)
    {
        _agent = new AgentHost(options);
        _agent.LogLine += line => LogReceived?.Invoke(line);
        _agent.ProxyRegistered += (id, ok, addr) => ProxyRegistered?.Invoke(id, ok, addr);
        _agent.Connected += () => Connected?.Invoke();
        _agent.Disconnected += () => Disconnected?.Invoke();
    }

    public bool IsConnected => _agent.IsConnected;

    public IReadOnlyList<ProxyDefinition> Proxies => _agent.Proxies;

    public Task StartAsync() => _agent.StartAsync();

    public Task StopAsync() => _agent.StopAsync();

    public Task AddTunnelAsync(ProxyDefinition def) => _agent.AddProxyAsync(def);

    public Task RemoveTunnelAsync(string proxyId) => _agent.RemoveProxyAsync(proxyId);

    /// <summary>每隧道流量快照：proxyId → (up, down)</summary>
    public IReadOnlyDictionary<string, (long Up, long Down)> GetTraffic() => _agent.GetTraffic();

    public ValueTask DisposeAsync() => _agent.DisposeAsync();
}
