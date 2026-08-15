using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 服务端单会话：处理一个 Agent 连接的控制消息（登录/注册/注销/心跳）。
/// E2 阶段注册隧道仅分配端口；E3 起在端口上真正监听并建立数据隧道。
/// </summary>
public sealed class ServerSession : IAsyncDisposable
{
    public const string ServerVersion = "0.1.0";

    /// <summary>客户端主机名（Hello 提供，Dashboard 展示用）</summary>
    public string? Hostname { get; private set; }

    private readonly ChannelMultiplexer _mux;
    private readonly PortManager _ports;
    private readonly string _serverToken;
    private readonly IVhostRegistry? _vhostHttp;
    private readonly IVhostRegistry? _vhostHttps;
    private readonly string _subDomainHost;
    private readonly int _maxPortsPerClient;
    private readonly Dictionary<string, int> _proxyPorts = new();
    private readonly Dictionary<string, LinkType> _proxyTypes = new();
    private readonly Dictionary<string, string> _proxyGroups = new();
    private readonly Dictionary<string, ProxyListener> _listeners = new();
    private readonly Dictionary<string, UdpProxyListener> _udpListeners = new();
    private readonly Dictionary<string, string> _vhostHosts = new();
    private readonly Dictionary<string, Traffic.TrafficCounter> _traffic = new();
    private readonly CancellationTokenSource _cts = new();
    private long _lastHeartbeat;
    private int _disposed;

    /// <summary>已登录的 Agent 标识</summary>
    public string? ClientId { get; private set; }

    /// <summary>登录成功（Hello 通过后触发，供服务端做同 clientId 会话替换）</summary>
    public event Action<ServerSession>? LoggedIn;

    /// <summary>会话关闭（Dispose 后触发，供服务端清理 client 映射）</summary>
    public event Action<ServerSession>? Closed;

    public event Action<string, string>? LogLine;

    public ServerSession(ChannelMultiplexer mux, PortManager ports, string serverToken,
        IVhostRegistry? vhostHttp = null, IVhostRegistry? vhostHttps = null, string subDomainHost = "",
        int maxPortsPerClient = 0)
    {
        _mux = mux;
        _ports = ports;
        _serverToken = serverToken;
        _vhostHttp = vhostHttp;
        _vhostHttps = vhostHttps;
        _subDomainHost = subDomainHost;
        _maxPortsPerClient = maxPortsPerClient;
        _lastHeartbeat = Environment.TickCount64;
        _mux.ControlHandler = HandleControlAsync;
        // 客户端断开（优雅 FIN 或异常）→ 立即清理本会话并释放端口，无需等心跳超时
        _mux.ConnectionClosed += OnConnectionClosed;
    }

    public void Start()
    {
        _mux.Start();
        _ = HeartbeatWatchAsync();
    }

    private async ValueTask HandleControlAsync(ushort channelId, byte[] payload)
    {
        var msg = MessageCodec.Deserialize(payload);
        switch (msg)
        {
            case HelloMessage hello:
                await HandleHelloAsync(hello);
                break;

            case RegisterProxyMessage reg:
                await HandleRegisterAsync(reg);
                break;

            case UnregisterProxyMessage unreg:
                HandleUnregisterAsync(unreg.ProxyId);
                break;

            case CommandAckMessage ack:
                LogLine?.Invoke("server", $"指令回执：{ack.Command} {ack.ProxyId} → {(ack.Ok ? "ok" : ack.Error ?? "?")}");
                break;

            case HeartbeatMessage hb:
                _lastHeartbeat = Environment.TickCount64;
                await SendAsync(new HeartbeatAckMessage(hb.Ts));
                break;
        }
    }

    private async Task HandleHelloAsync(HelloMessage hello)
    {
        if (hello.Token != _serverToken)
        {
            LogLine?.Invoke("server", $"登录被拒：token 不匹配 ({hello.ClientId})");
            await SendAsync(new HelloAckMessage(false, "token 不匹配", ServerVersion));
            return;
        }

        ClientId = hello.ClientId;
        Hostname = hello.Hostname;
        LoggedIn?.Invoke(this);
        LogLine?.Invoke("server", $"Agent 登录成功：{hello.ClientId} ({hello.Hostname})");
        await SendAsync(new HelloAckMessage(true, null, ServerVersion));
    }

    private async Task HandleRegisterAsync(RegisterProxyMessage reg)
    {
        try
        {
            // HTTP/HTTPS：vhost 路由，不占用独立端口
            if ((reg.LinkType & LinkType.Http) != 0 || (reg.LinkType & LinkType.Https) != 0)
            {
                var isHttps = (reg.LinkType & LinkType.Https) != 0;
                var registry = isHttps ? _vhostHttps : _vhostHttp;
                var host = BuildVhostHost(reg)
                    ?? throw new InvalidOperationException("HTTP/HTTPS 隧道需配置 domain 或 subdomain");
                if (registry is null)
                    throw new InvalidOperationException(isHttps
                        ? "服务端未启用 HTTPS vhost（vhostHTTPSPort 未配置）"
                        : "服务端未启用 HTTP vhost（vhostHTTPPort 未配置）");

                _vhostHosts[reg.ProxyId] = host;
                _traffic[reg.ProxyId] = new Traffic.TrafficCounter();
                registry.Register(host, _mux, reg.ProxyId);
                LogLine?.Invoke("server", $"隧道注册：{reg.ProxyId} ({reg.LinkType}) → {(isHttps ? "https" : "http")}://{host}");
                await SendAsync(new RegisterProxyAckMessage(reg.ProxyId, true, $"{(isHttps ? "https" : "http")}://{host}", null));
                return;
            }

            var port = _ports.Allocate(reg.RemotePort);

            // maxPortsPerClient：限制每客户端端口隧道数（0 = 不限）
            if (_maxPortsPerClient > 0 && _proxyPorts.Count >= _maxPortsPerClient)
                throw new InvalidOperationException($"隧道端口数超过客户端上限（{_maxPortsPerClient}）");

            _proxyPorts[reg.ProxyId] = port;
            _proxyTypes[reg.ProxyId] = reg.LinkType;
            _proxyGroups[reg.ProxyId] = string.IsNullOrWhiteSpace(reg.Group) ? "default" : reg.Group!;
            _traffic[reg.ProxyId] = new Traffic.TrafficCounter();

            // 按类型创建监听：TCP → ProxyListener（每用户连接一条隧道）；UDP → UdpProxyListener（固定隧道）
            if (reg.LinkType == LinkType.Tcp)
            {
                var listener = new ProxyListener(_mux, reg.ProxyId, port, _traffic[reg.ProxyId]);
                _listeners[reg.ProxyId] = listener;
                listener.Start();
            }
            else if (reg.LinkType == LinkType.Udp)
            {
                var listener = new UdpProxyListener(_mux, reg.ProxyId, port, _traffic[reg.ProxyId]);
                _udpListeners[reg.ProxyId] = listener;
                listener.Start();
            }

            LogLine?.Invoke("server", $"隧道注册：{reg.ProxyId} ({reg.LinkType}) → 0.0.0.0:{port}");
            await SendAsync(new RegisterProxyAckMessage(reg.ProxyId, true, $"0.0.0.0:{port}", null));
        }
        catch (Exception ex)
        {
            LogLine?.Invoke("server", $"隧道注册失败：{reg.ProxyId} ({ex.Message})");
            await SendAsync(new RegisterProxyAckMessage(reg.ProxyId, false, null, ex.Message));
        }
    }

    /// <summary>隧道快照（Dashboard/TUI 用）：ProxyId, Group, Type, RemoteAddr, UpBytes, DownBytes, Online</summary>
    public IReadOnlyList<(string ProxyId, string Group, string Type, string RemoteAddr, long UpBytes, long DownBytes, bool Online)> GetProxiesSnapshot()
    {
        var list = new List<(string, string, string, string, long, long, bool)>();
        foreach (var (proxyId, port) in _proxyPorts)
        {
            var type = _proxyTypes.TryGetValue(proxyId, out var t) ? t.ToString() : "?";
            var tr = _traffic.TryGetValue(proxyId, out var tc) ? tc : null;
            var group = _proxyGroups.TryGetValue(proxyId, out var g) ? g : "default";
            list.Add((proxyId, group, type, $"0.0.0.0:{port}", tr?.UpBytes ?? 0, tr?.DownBytes ?? 0, true));
        }
        foreach (var (proxyId, host) in _vhostHosts)
            list.Add((proxyId, "default", "vhost", $"host://{host}", 0, 0, true));
        return list;
    }

    private string? BuildVhostHost(RegisterProxyMessage reg)
    {
        if (!string.IsNullOrWhiteSpace(reg.Domain))
            return reg.Domain;
        if (!string.IsNullOrWhiteSpace(reg.Subdomain) && !string.IsNullOrWhiteSpace(_subDomainHost))
            return $"{reg.Subdomain}.{_subDomainHost}";
        return null;
    }

    private void HandleUnregisterAsync(string proxyId)
    {
        // 端口隧道：释放端口与监听器
        if (_proxyPorts.Remove(proxyId, out var port))
        {
            _proxyTypes.Remove(proxyId);
            if (_listeners.Remove(proxyId, out var listener))
                _ = listener.DisposeAsync();
            if (_udpListeners.Remove(proxyId, out var udpListener))
                _ = udpListener.DisposeAsync();
            _ports.Release(port);
            LogLine?.Invoke("server", $"隧道注销：{proxyId}（释放端口 {port}）");
        }

        // vhost 隧道：从 Host 路由表摘除（端口隧道无此条目，独立清理，避免泄漏）
        if (_vhostHosts.Remove(proxyId, out var host))
        {
            _vhostHttp?.Unregister(host);
            _vhostHttps?.Unregister(host);
            LogLine?.Invoke("server", $"隧道注销：{proxyId}（vhost {host}）");
        }

        _proxyGroups.Remove(proxyId);
        _traffic.Remove(proxyId);
    }

    /// <summary>
    /// 服务端主动删除隧道（管理端发起）：仅向客户端下发 RemoveProxyCommandMessage，
    /// 服务端不直接动手删。客户端本地删除后回 UnregisterProxyMessage（服务端响应释放
    /// 端口/监听/vhost，走既有 C→S 注销路径）与 CommandAckMessage。
    /// 指令为尽力送达：连接已断开时跳过下发（此时客户端断线，服务端随会话自动清理全部隧道）。
    /// </summary>
    public async ValueTask RemoveProxyAsync(string proxyId, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            await SendAsync(new RemoveProxyCommandMessage(proxyId) { TargetClientId = ClientId });
        }
        catch
        {
            LogLine?.Invoke("server", $"下发删除指令失败（连接可能已断开）：{proxyId}");
        }
    }

    /// <summary>连接断开（客户端退出/网络中断）→ 立即清理会话并释放端口</summary>
    private void OnConnectionClosed()
    {
        LogLine?.Invoke("server", $"Agent 连接断开，清理会话：{ClientId ?? "?"}");
        _ = DisposeAsync();
    }

    /// <summary>心跳看护：15s 未收到心跳判定离线。会话 Dispose 后立即停止（避免断开后误报超时日志）</summary>
    private async Task HeartbeatWatchAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                if (Volatile.Read(ref _disposed) != 0)
                    break; // 会话已清理（连接断开/替换），不再检查
                if (Environment.TickCount64 - _lastHeartbeat > 15_000)
                {
                    LogLine?.Invoke("server", $"Agent 心跳超时，断开：{ClientId}");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await DisposeAsync();
        }
    }

    private ValueTask SendAsync(Message msg)
        => _mux.SendControlAsync(MessageCodec.Serialize(msg));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _cts.Cancel(); // 停止心跳看护，避免断开后误报超时

        // 释放本会话占用端口与监听器
        foreach (var listener in _listeners.Values)
            _ = listener.DisposeAsync();
        _listeners.Clear();
        foreach (var listener in _udpListeners.Values)
            _ = listener.DisposeAsync();
        _udpListeners.Clear();
        foreach (var port in _proxyPorts.Values)
            _ports.Release(port);
        _proxyPorts.Clear();
        Closed?.Invoke(this);
        return _mux.DisposeAsync();
    }
}
