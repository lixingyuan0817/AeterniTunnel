using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;

namespace Aeterni.Tunnel.Engine.Hosting;

/// <summary>
/// Agent 宿主：代理列表管理 + Agent 会话生命周期 + 健康检查自动摘除/恢复。
/// 断线自动重连由 AgentSession 内部负责（重连后自动重注册全部代理，并通过
/// ProxyRegistered 事件通知宿主）。
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly int _healthIntervalSeconds;
    private readonly Func<ProxyDefinition, IHealthChecker>? _checkerFactory;
    private readonly List<ProxyDefinition> _proxies = new();
    private readonly HashSet<string> _registered = new();
    private readonly Dictionary<string, IHealthChecker> _healthChecks = new();
    private readonly object _lock = new();
    private AgentSession? _session;
    private bool _stopped;

    public event Action<string>? LogLine;
    public event Action<string, bool, string?>? ProxyRegistered;

    /// <summary>健康检查间隔（秒，0=关闭）。重连间隔由 AgentSession 管理（指数退避）。
    /// checkerFactory：健康检查器工厂（测试注入用）；null 时内部创建真实 HealthChecker。</summary>
    public AgentHost(AgentOptions options, int healthIntervalSeconds = 0, int reconnectIntervalSeconds = 5,
        Func<ProxyDefinition, IHealthChecker>? checkerFactory = null)
    {
        _options = options;
        _healthIntervalSeconds = healthIntervalSeconds;
        _checkerFactory = checkerFactory;
    }

    public bool IsConnected => _session?.IsConnected ?? false;

    /// <summary>ATS 服务端地址（TUI 展示用）</summary>
    public string ServerAddr => _options.ServerAddr;

    /// <summary>ATS 服务端端口（TUI 展示用）</summary>
    public int ServerPort => _options.ServerPort;

    /// <summary>客户端标识（TUI 展示用）</summary>
    public string ClientId => _options.ClientId;

    /// <summary>已配置的代理列表（TUI/管理用）</summary>
    public IReadOnlyList<ProxyDefinition> Proxies
    {
        get { lock (_lock) return _proxies.ToList(); }
    }

    /// <summary>代理流量快照：proxyId → (up, down)（TUI 用）</summary>
    public IReadOnlyDictionary<string, (long Up, long Down)> GetTraffic()
        => _session?.GetTrafficSnapshot() ?? new Dictionary<string, (long, long)>();

    /// <summary>连接 Server 并注册全部代理（重复调用幂等）</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_session is not null)
                return;
            _stopped = false;
        }

        try
        {
            await ConnectOnceAsync(ct);
        }
        catch (Exception ex)
        {
            // 初次连接失败（拒绝/超时/登录失败）：AgentSession 已进入后台重连循环，不抛给上层
            LogLine?.Invoke($"连接失败：{ex.Message}（后台自动重连中）");
        }

        List<ProxyDefinition> proxies;
        lock (_lock) proxies = _proxies.ToList();
        foreach (var p in proxies)
            EnsureHealthCheck(p);
    }

    /// <summary>停止：断开连接、停止健康检查</summary>
    public async Task StopAsync()
    {
        _stopped = true;

        List<IHealthChecker> checkers;
        lock (_lock)
        {
            checkers = _healthChecks.Values.ToList();
            _healthChecks.Clear();
        }
        foreach (var c in checkers)
            await c.DisposeAsync();

        AgentSession? session;
        lock (_lock) { session = _session; _session = null; _registered.Clear(); }
        if (session is not null)
            await session.DisposeAsync();
    }

    /// <summary>添加代理到列表（StartAsync 时统一注册；已在运行则热注册）</summary>
    public void AddProxy(ProxyDefinition proxy)
    {
        lock (_lock)
        {
            _proxies.RemoveAll(x => x.ProxyId == proxy.ProxyId);
            _proxies.Add(proxy);
        }
        EnsureHealthCheck(proxy);
    }

    /// <summary>添加代理（已在运行则热注册，FR-015）</summary>
    public async Task AddProxyAsync(ProxyDefinition proxy, CancellationToken ct = default)
    {
        AddProxy(proxy);
        var session = Volatile.Read(ref _session);
        if (session is { IsConnected: true })
            await session.RegisterProxyAsync(proxy.ProxyId, proxy.LinkType, proxy.LocalIp, proxy.LocalPort, proxy.RemotePort, proxy.Domain, proxy.Subdomain, proxy.Group, ct);
    }

    /// <summary>移除代理（已在运行则热注销；停止健康检查）</summary>
    public async Task RemoveProxyAsync(string proxyId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _proxies.RemoveAll(x => x.ProxyId == proxyId);
            _registered.Remove(proxyId);
        }

        IHealthChecker? checker;
        lock (_lock)
        {
            _healthChecks.TryGetValue(proxyId, out checker);
            _healthChecks.Remove(proxyId);
        }
        if (checker is not null)
            await checker.DisposeAsync();

        var session = Volatile.Read(ref _session);
        if (session is { IsConnected: true })
            await session.UnregisterProxyAsync(proxyId, ct);
    }

    /// <summary>配置热更新（FR-015）：对比新代理列表，增量增删/更新（同 id 参数变化视为更新）</summary>
    public async Task<List<string>> ReloadAsync(IReadOnlyList<ProxyDefinition> newProxies, CancellationToken ct = default)
    {
        var changes = new List<string>();

        List<ProxyDefinition> current;
        lock (_lock) current = _proxies.ToList();
        var newById = newProxies.ToDictionary(p => p.ProxyId);
        var curById = current.ToDictionary(p => p.ProxyId);

        // 移除或更新
        foreach (var cur in current)
        {
            if (!newById.TryGetValue(cur.ProxyId, out var np))
            {
                await RemoveProxyAsync(cur.ProxyId, ct);
                changes.Add($"移除 {cur.ProxyId}");
            }
            else if (np != cur)
            {
                await RemoveProxyAsync(cur.ProxyId, ct);
                await AddProxyAsync(np, ct);
                changes.Add($"更新 {cur.ProxyId}");
            }
        }

        // 新增
        foreach (var np in newProxies)
        {
            if (!curById.ContainsKey(np.ProxyId))
            {
                await AddProxyAsync(np, ct);
                changes.Add($"新增 {np.ProxyId}");
            }
        }

        return changes;
    }

    // ---------- 内部 ----------

    private async Task ConnectOnceAsync(CancellationToken ct = default)
    {
        var session = new AgentSession(_options);
        session.LogLine += s => LogLine?.Invoke(s);
        session.ProxyRegistered += OnProxyRegistered;
        session.ProxyRemoved += proxyId => _ = HandleProxyRemovedAsync(proxyId);

        await session.ConnectAsync(ct);
        lock (_lock) _session = session;

        List<ProxyDefinition> proxies;
        lock (_lock) proxies = _proxies.ToList();
        foreach (var p in proxies)
            await session.RegisterProxyAsync(p.ProxyId, p.LinkType, p.LocalIp, p.LocalPort, p.RemotePort, p.Domain, p.Subdomain, p.Group, ct);
    }

    /// <summary>
    /// 服务端指令删除代理：本地清理（停止健康检查、移除配置与注册标记）。
    /// 不向服务端重发注销（服务端为发起方）；停健康检查避免「健康恢复 → 重新注册」复活。
    /// </summary>
    private async Task HandleProxyRemovedAsync(string proxyId)
    {
        IHealthChecker? checker;
        lock (_lock)
        {
            _proxies.RemoveAll(x => x.ProxyId == proxyId);
            _registered.Remove(proxyId);
            _healthChecks.TryGetValue(proxyId, out checker);
            _healthChecks.Remove(proxyId);
        }
        if (checker is not null)
            await checker.DisposeAsync();
        LogLine?.Invoke($"代理 {proxyId} 已被服务端移除，本地已清理");
    }

    private void OnProxyRegistered(string id, bool ok, string? addr)
    {
        lock (_lock)
        {
            if (ok) _registered.Add(id);
            else _registered.Remove(id);
        }
        ProxyRegistered?.Invoke(id, ok, addr);
    }

    /// <summary>健康检查：失败自动摘除代理，恢复自动重新注册（core2）</summary>
    private void EnsureHealthCheck(ProxyDefinition proxy)
    {
        if (_healthIntervalSeconds <= 0)
            return;

        lock (_lock)
        {
            if (_healthChecks.ContainsKey(proxy.ProxyId))
                return;

            IHealthChecker checker = _checkerFactory is not null
                ? _checkerFactory(proxy)
                : new HealthChecker(proxy.LocalIp, proxy.LocalPort,
                    new HealthCheckOptions("tcp", null, _healthIntervalSeconds));
            checker.StatusChanged += healthy => _ = OnHealthChangedAsync(proxy, healthy);
            checker.Start();
            _healthChecks[proxy.ProxyId] = checker;
        }
    }

    private async Task OnHealthChangedAsync(ProxyDefinition proxy, bool healthy)
    {
        if (_stopped)
            return;

        var session = Volatile.Read(ref _session);
        if (healthy)
        {
            // 恢复：若当前未注册则重新注册
            bool needRegister;
            lock (_lock) needRegister = !_registered.Contains(proxy.ProxyId);
            if (needRegister && session is { IsConnected: true })
            {
                LogLine?.Invoke($"代理 {proxy.ProxyId} 健康恢复，重新注册");
                await session.RegisterProxyAsync(proxy.ProxyId, proxy.LinkType, proxy.LocalIp, proxy.LocalPort, proxy.RemotePort, proxy.Domain, proxy.Subdomain, proxy.Group);
            }
        }
        else
        {
            lock (_lock) _registered.Remove(proxy.ProxyId);
            if (session is { IsConnected: true })
            {
                LogLine?.Invoke($"代理 {proxy.ProxyId} 健康检查失败，自动摘除");
                await session.UnregisterProxyAsync(proxy.ProxyId);
            }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
