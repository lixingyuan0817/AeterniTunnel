using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 服务端单会话：处理一个 Agent 连接的控制消息（登录/注册/注销/心跳）。
/// E2 阶段注册隧道仅分配端口；E3 起在端口上真正监听并建立数据隧道。
/// </summary>
public sealed class ServerSession : IAsyncDisposable, IPeerSignalEndpoint
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
    private readonly int _maxDataConnections;
    private readonly DataConnectionBindingRegistry? _dataConnectionBindings;
    private readonly IClientIdentityResolver? _identityResolver;
    private readonly Dictionary<string, int> _proxyPorts = new();
    private readonly Dictionary<string, LinkType> _proxyTypes = new();
    private readonly Dictionary<string, string> _proxyGroups = new();
    private readonly Dictionary<string, ProxyListener> _listeners = new();
    private readonly Dictionary<string, UdpProxyListener> _udpListeners = new();
    private readonly Dictionary<string, string> _vhostHosts = new();
    private readonly Dictionary<string, IVhostRegistry> _vhostRegistries = new();
    private readonly Dictionary<string, Traffic.TrafficCounter> _traffic = new();
    private readonly SemaphoreSlim _resourceGate = new(1, 1);
    private readonly object _resourceStateLock = new();
    private readonly object _dataConnectionsLock = new();
    private readonly List<ChannelMultiplexer> _dataMultiplexers = [];
    private readonly CancellationTokenSource _cts = new();
    private long _lastHeartbeat;
    private int _authenticated;
    private int _disposed;
    private int _nextDataMultiplexer;

    /// <summary>已登录的 Agent 标识</summary>
    public string? ClientId { get; private set; }

    /// <summary>由宿主身份解析器确认的稳定 Peer 身份；旧共享 token 会话为 null。</summary>
    public string? AuthenticatedPeerId { get; private set; }

    /// <summary>本次 Hello 与服务端能力的交集。</summary>
    public ProtocolCapabilities NegotiatedCapabilities { get; private set; }

    public int DataConnectionCount
    {
        get
        {
            lock (_dataConnectionsLock)
                return _dataMultiplexers.Count(mux => !mux.IsClosed);
        }
    }

    /// <summary>登录成功（Hello 通过后触发，供服务端做同 clientId 会话替换）</summary>
    public event Action<ServerSession>? LoggedIn;

    /// <summary>会话关闭（Dispose 后触发，供服务端清理 client 映射）</summary>
    public event Action<ServerSession>? Closed;

    public event Action<string, string>? LogLine;
    internal event Func<IPeerSignalEndpoint, PeerRequestMessage, Task>? PeerRequestReceived;
    internal event Func<IPeerSignalEndpoint, PeerDescriptionMessage, Task>? PeerDescriptionReceived;
    internal event Func<IPeerSignalEndpoint, PeerCandidateMessage, Task>? PeerCandidateReceived;

    string IPeerSignalEndpoint.PeerId => AuthenticatedPeerId ?? throw new InvalidOperationException("Peer 尚未完成稳定身份认证");

    ValueTask IPeerSignalEndpoint.SendPeerSignalAsync(Message message) => SendAsync(message);

    public ServerSession(ChannelMultiplexer mux, PortManager ports, string serverToken,
        IVhostRegistry? vhostHttp = null, IVhostRegistry? vhostHttps = null, string subDomainHost = "",
        int maxPortsPerClient = 0, IClientIdentityResolver? identityResolver = null)
        : this(mux, ports, serverToken, vhostHttp, vhostHttps, subDomainHost,
            maxPortsPerClient, identityResolver, null, 0)
    {
    }

    internal ServerSession(ChannelMultiplexer mux, PortManager ports, string serverToken,
        IVhostRegistry? vhostHttp, IVhostRegistry? vhostHttps, string subDomainHost,
        int maxPortsPerClient, IClientIdentityResolver? identityResolver,
        DataConnectionBindingRegistry? dataConnectionBindings, int maxDataConnections)
    {
        _mux = mux;
        _ports = ports;
        _serverToken = serverToken;
        _vhostHttp = vhostHttp;
        _vhostHttps = vhostHttps;
        _subDomainHost = subDomainHost;
        _maxPortsPerClient = maxPortsPerClient;
        _maxDataConnections = Math.Max(0, maxDataConnections);
        _dataConnectionBindings = dataConnectionBindings;
        _identityResolver = identityResolver;
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

    internal async Task StartAsync(byte[] initialControlPayload)
    {
        await HandleControlAsync(FrameContract.ControlChannel, initialControlPayload);
        if (Volatile.Read(ref _disposed) == 0)
            Start();
    }

    private async ValueTask HandleControlAsync(ushort channelId, byte[] payload)
    {
        var msg = MessageCodec.Deserialize(payload);

        // Hello 是认证前唯一允许的控制消息。拒绝后关闭连接，避免未认证连接
        // 继续占用会话、心跳或隧道资源。
        if (msg is HelloMessage hello)
        {
            await HandleHelloAsync(hello);
            return;
        }

        if (Volatile.Read(ref _authenticated) == 0)
        {
            LogLine?.Invoke("server", $"认证前控制消息被拒：{msg?.GetType().Name ?? "null"}");
            await SendAsync(new ErrorMessage(401, "需要先完成 Hello 登录"));
            await DisposeAsync();
            return;
        }

        switch (msg)
        {
            case RegisterProxyMessage reg:
                await HandleRegisterAsync(reg);
                break;

            case UnregisterProxyMessage unreg:
                await HandleUnregisterAsync(unreg.ProxyId);
                break;

            case CommandAckMessage ack:
                LogLine?.Invoke("server", $"指令回执：{ack.Command} {ack.ProxyId} → {(ack.Ok ? "ok" : ack.Error ?? "?")}");
                break;

            case HeartbeatMessage hb:
                _lastHeartbeat = Environment.TickCount64;
                await SendAsync(new HeartbeatAckMessage(hb.Ts));
                break;

            case RequestDataConnectionMessage:
                await HandleDataConnectionRequestAsync();
                break;

            case PeerRequestMessage peerRequest when AuthenticatedPeerId is not null:
                if (PeerRequestReceived is not null)
                    await PeerRequestReceived(this, peerRequest);
                break;

            case PeerDescriptionMessage peerDescription when AuthenticatedPeerId is not null:
                if (PeerDescriptionReceived is not null)
                    await PeerDescriptionReceived(this, peerDescription);
                break;

            case PeerCandidateMessage peerCandidate when AuthenticatedPeerId is not null:
                if (PeerCandidateReceived is not null)
                    await PeerCandidateReceived(this, peerCandidate);
                break;
        }
    }

    private async Task HandleHelloAsync(HelloMessage hello)
    {
        if (Volatile.Read(ref _authenticated) != 0)
        {
            LogLine?.Invoke("server", $"重复登录被拒：{hello.ClientId}");
            await SendAsync(new HelloAckMessage(false, "会话已完成登录", ServerVersion));
            await DisposeAsync();
            return;
        }

        if (hello.Token != _serverToken)
        {
            LogLine?.Invoke("server", $"登录被拒：token 不匹配 ({hello.ClientId})");
            await SendAsync(new HelloAckMessage(false, "token 不匹配", ServerVersion));
            await DisposeAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(hello.ClientId))
        {
            LogLine?.Invoke("server", "登录被拒：clientId 为空");
            await SendAsync(new HelloAckMessage(false, "clientId 不能为空", ServerVersion));
            await DisposeAsync();
            return;
        }

        if (hello.Version != ProtocolContract.CurrentVersion)
        {
            await SendAsync(new HelloAckMessage(false, $"不支持的协议版本：{hello.Version}", ServerVersion));
            await DisposeAsync();
            return;
        }

        var resolvedClientId = _identityResolver is null
            ? hello.ClientId
            : await _identityResolver.ResolveAsync(hello, _cts.Token);
        if (string.IsNullOrWhiteSpace(resolvedClientId))
        {
            await SendAsync(new HelloAckMessage(false, "客户端身份未获授权", ServerVersion));
            await DisposeAsync();
            return;
        }

        ClientId = resolvedClientId;
        AuthenticatedPeerId = _identityResolver is null ? null : resolvedClientId;
        Hostname = hello.Hostname;
        NegotiatedCapabilities = (ProtocolCapabilities)hello.Capabilities & ProtocolContract.SupportedCapabilities;
        if (NegotiatedCapabilities.HasFlag(ProtocolCapabilities.ConnectionIsolation))
            _mux.EnableSlowChannelIsolation();
        Volatile.Write(ref _authenticated, 1);
        LoggedIn?.Invoke(this);
        LogLine?.Invoke("server", $"Agent 登录成功：{hello.ClientId} ({hello.Hostname})");
        await SendAsync(new HelloAckMessage(true, null, ServerVersion, (ulong)NegotiatedCapabilities));
        // 下发端口策略：allowPorts 白名单 + 每客户端上限（客户端添加隧道前做前置校验）
        await SendAsync(new PortPolicyMessage(_ports.GetAllowedPorts(), _maxPortsPerClient));
    }

    private async Task HandleDataConnectionRequestAsync()
    {
        if (!NegotiatedCapabilities.HasFlag(ProtocolCapabilities.ConnectionIsolation) ||
            _dataConnectionBindings is null || _maxDataConnections <= 0)
        {
            await SendAsync(new ErrorMessage(400, "未协商附加数据连接能力"));
            return;
        }
        if (DataConnectionCount >= _maxDataConnections)
        {
            await SendAsync(new ErrorMessage(429, $"附加数据连接数已达到上限（{_maxDataConnections}）"));
            return;
        }

        var binding = _dataConnectionBindings.Issue(this);
        await SendAsync(new DataConnectionTokenMessage(binding.Token, binding.ExpiresAt.ToUnixTimeMilliseconds()));
    }

    internal bool TryAttachDataConnection(ChannelMultiplexer multiplexer, out string error)
    {
        error = "";
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _authenticated) == 0)
        {
            error = "控制会话已关闭";
            return false;
        }
        if (!NegotiatedCapabilities.HasFlag(ProtocolCapabilities.ConnectionIsolation))
        {
            error = "控制会话未协商附加数据连接能力";
            return false;
        }

        lock (_dataConnectionsLock)
        {
            _dataMultiplexers.RemoveAll(mux => mux.IsClosed);
            if (_dataMultiplexers.Count >= _maxDataConnections)
            {
                error = $"附加数据连接数已达到上限（{_maxDataConnections}）";
                return false;
            }
            _dataMultiplexers.Add(multiplexer);
        }

        multiplexer.EnableSlowChannelIsolation();
        multiplexer.ControlHandler = async (_, _) =>
        {
            LogLine?.Invoke("server", "附加数据连接收到非法控制消息，连接已关闭");
            await multiplexer.DisposeAsync();
        };
        multiplexer.ConnectionClosed += () => RemoveDataConnection(multiplexer);
        multiplexer.Start();
        return true;
    }

    private void RemoveDataConnection(ChannelMultiplexer multiplexer)
    {
        lock (_dataConnectionsLock)
            _dataMultiplexers.Remove(multiplexer);
    }

    private ChannelMultiplexer SelectTunnelMultiplexer()
    {
        lock (_dataConnectionsLock)
        {
            _dataMultiplexers.RemoveAll(mux => mux.IsClosed);
            if (_dataMultiplexers.Count == 0)
                return _mux;
            var index = (int)((uint)Interlocked.Increment(ref _nextDataMultiplexer) %
                (uint)_dataMultiplexers.Count);
            return _dataMultiplexers[index];
        }
    }

    private async Task HandleRegisterAsync(RegisterProxyMessage reg)
    {
        Exception? failure = null;
        await _resourceGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ServerSession));
            if (string.IsNullOrWhiteSpace(reg.ProxyId))
                throw new InvalidOperationException("ProxyId 不能为空");

            lock (_resourceStateLock)
            {
                if (_proxyPorts.ContainsKey(reg.ProxyId) || _vhostHosts.ContainsKey(reg.ProxyId))
                    throw new InvalidOperationException($"隧道 {reg.ProxyId} 已注册");
            }

            if (reg.LinkType is LinkType.Http or LinkType.Https)
                await RegisterVhostAsync(reg);
            else if (reg.LinkType is LinkType.Tcp or LinkType.Udp)
                await RegisterPortProxyAsync(reg);
            else
                throw new InvalidOperationException($"不支持的隧道类型：{reg.LinkType}");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _resourceGate.Release();
        }

        if (failure is not null)
        {
            LogLine?.Invoke("server", $"隧道注册失败：{reg.ProxyId} ({failure.Message})");
            await SendAsync(new RegisterProxyAckMessage(reg.ProxyId, false, null, failure.Message));
        }
    }

    private async Task RegisterVhostAsync(RegisterProxyMessage reg)
    {
        var isHttps = reg.LinkType == LinkType.Https;
        var registry = isHttps ? _vhostHttps : _vhostHttp;
        var host = BuildVhostHost(reg)
            ?? throw new InvalidOperationException("HTTP/HTTPS 隧道需配置 domain 或 subdomain");
        if (registry is null)
            throw new InvalidOperationException(isHttps
                ? "服务端未启用 HTTPS vhost（vhostHTTPSPort 未配置）"
                : "服务端未启用 HTTP vhost（vhostHTTPPort 未配置）");
        if (!registry.TryRegister(host, _mux, SelectTunnelMultiplexer, reg.ProxyId))
            throw new InvalidOperationException($"vhost {host} 已被占用");

        try
        {
            lock (_resourceStateLock)
            {
                _vhostHosts.Add(reg.ProxyId, host);
                _vhostRegistries.Add(reg.ProxyId, registry);
                _proxyGroups[reg.ProxyId] = string.IsNullOrWhiteSpace(reg.Group) ? "default" : reg.Group!;
                _traffic[reg.ProxyId] = new Traffic.TrafficCounter();
            }

            var scheme = isHttps ? "https" : "http";
            LogLine?.Invoke("server", $"隧道注册：{reg.ProxyId} ({reg.LinkType}) → {scheme}://{host}");
            await SendAsync(new RegisterProxyAckMessage(reg.ProxyId, true, $"{scheme}://{host}", null));
        }
        catch
        {
            lock (_resourceStateLock)
            {
                _vhostHosts.Remove(reg.ProxyId);
                _vhostRegistries.Remove(reg.ProxyId);
                _proxyGroups.Remove(reg.ProxyId);
                _traffic.Remove(reg.ProxyId);
            }
            registry.Unregister(host, _mux, reg.ProxyId);
            throw;
        }
    }

    private async Task RegisterPortProxyAsync(RegisterProxyMessage reg)
    {
        lock (_resourceStateLock)
        {
            if (_maxPortsPerClient > 0 && _proxyPorts.Count >= _maxPortsPerClient)
                throw new InvalidOperationException($"隧道端口数超过客户端上限（{_maxPortsPerClient}）");
        }

        int? port = null;
        ProxyListener? tcpListener = null;
        UdpProxyListener? udpListener = null;
        try
        {
            port = _ports.Allocate(reg.RemotePort);
            var traffic = new Traffic.TrafficCounter();
            if (reg.LinkType == LinkType.Tcp)
            {
                tcpListener = new ProxyListener(SelectTunnelMultiplexer, reg.ProxyId, port.Value, traffic);
                tcpListener.Start();
            }
            else
            {
                udpListener = new UdpProxyListener(SelectTunnelMultiplexer, reg.ProxyId, port.Value, traffic,
                    NegotiatedCapabilities.HasFlag(ProtocolCapabilities.UdpSourceAssociation));
                udpListener.Start();
            }

            lock (_resourceStateLock)
            {
                _proxyPorts.Add(reg.ProxyId, port.Value);
                _proxyTypes.Add(reg.ProxyId, reg.LinkType);
                _proxyGroups[reg.ProxyId] = string.IsNullOrWhiteSpace(reg.Group) ? "default" : reg.Group!;
                _traffic.Add(reg.ProxyId, traffic);
                if (tcpListener is not null)
                    _listeners.Add(reg.ProxyId, tcpListener);
                if (udpListener is not null)
                    _udpListeners.Add(reg.ProxyId, udpListener);
            }

            LogLine?.Invoke("server", $"隧道注册：{reg.ProxyId} ({reg.LinkType}) → 0.0.0.0:{port}");
            await SendAsync(new RegisterProxyAckMessage(reg.ProxyId, true, $"0.0.0.0:{port}", null));
        }
        catch
        {
            lock (_resourceStateLock)
            {
                _proxyPorts.Remove(reg.ProxyId);
                _proxyTypes.Remove(reg.ProxyId);
                _proxyGroups.Remove(reg.ProxyId);
                _traffic.Remove(reg.ProxyId);
                _listeners.Remove(reg.ProxyId);
                _udpListeners.Remove(reg.ProxyId);
            }
            if (tcpListener is not null)
                await tcpListener.DisposeAsync();
            if (udpListener is not null)
                await udpListener.DisposeAsync();
            if (port is not null)
                _ports.Release(port.Value);
            throw;
        }
    }

    /// <summary>隧道快照（Dashboard/TUI 用）：ProxyId, Group, Type, RemoteAddr, UpBytes, DownBytes, Online</summary>
    public IReadOnlyList<(string ProxyId, string Group, string Type, string RemoteAddr, long UpBytes, long DownBytes, bool Online)> GetProxiesSnapshot()
    {
        lock (_resourceStateLock)
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
            {
                var group = _proxyGroups.TryGetValue(proxyId, out var g) ? g : "default";
                list.Add((proxyId, group, "vhost", $"host://{host}", 0, 0, true));
            }
            return list;
        }
    }

    private string? BuildVhostHost(RegisterProxyMessage reg)
    {
        if (!string.IsNullOrWhiteSpace(reg.Domain))
            return reg.Domain;
        if (!string.IsNullOrWhiteSpace(reg.Subdomain) && !string.IsNullOrWhiteSpace(_subDomainHost))
            return $"{reg.Subdomain}.{_subDomainHost}";
        return null;
    }

    private async Task HandleUnregisterAsync(string proxyId)
    {
        await _resourceGate.WaitAsync();
        try
        {
            await RemoveProxyResourcesAsync(proxyId);
        }
        finally
        {
            _resourceGate.Release();
        }
    }

    private async Task RemoveProxyResourcesAsync(string proxyId)
    {
        int? port;
        ProxyListener? listener;
        UdpProxyListener? udpListener;
        string? host;
        IVhostRegistry? registry;
        lock (_resourceStateLock)
        {
            port = _proxyPorts.Remove(proxyId, out var allocatedPort) ? allocatedPort : null;
            _proxyTypes.Remove(proxyId);
            _listeners.Remove(proxyId, out listener);
            _udpListeners.Remove(proxyId, out udpListener);
            _vhostHosts.Remove(proxyId, out host);
            _vhostRegistries.Remove(proxyId, out registry);
            _proxyGroups.Remove(proxyId);
            _traffic.Remove(proxyId);
        }

        if (listener is not null)
            await listener.DisposeAsync();
        if (udpListener is not null)
            await udpListener.DisposeAsync();
        if (port is not null)
        {
            _ports.Release(port.Value);
            LogLine?.Invoke("server", $"隧道注销：{proxyId}（释放端口 {port}）");
        }
        if (host is not null && registry is not null)
        {
            registry.Unregister(host, _mux, proxyId);
            LogLine?.Invoke("server", $"隧道注销：{proxyId}（vhost {host}）");
        }
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel(); // 停止心跳看护，避免断开后误报超时
        _dataConnectionBindings?.Revoke(this);

        await _resourceGate.WaitAsync();
        try
        {
            string[] proxyIds;
            lock (_resourceStateLock)
                proxyIds = _proxyPorts.Keys.Concat(_vhostHosts.Keys).Distinct().ToArray();
            foreach (var proxyId in proxyIds)
                await RemoveProxyResourcesAsync(proxyId);
        }
        finally
        {
            _resourceGate.Release();
        }
        ChannelMultiplexer[] dataMultiplexers;
        lock (_dataConnectionsLock)
        {
            dataMultiplexers = _dataMultiplexers.ToArray();
            _dataMultiplexers.Clear();
        }
        foreach (var multiplexer in dataMultiplexers)
            await multiplexer.DisposeAsync();
        Closed?.Invoke(this);
        await _mux.DisposeAsync();
    }
}
