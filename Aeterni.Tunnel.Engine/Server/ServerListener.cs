using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Transport;
using Aeterni.Tunnel.Engine.Wire;
using System.Net;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 服务端监听器：接受 Agent 连接，每个连接创建一个 ServerSession。
/// 可选 vhost HTTP 监听（vhostHttpPort &gt; 0 时启用，按 Host 路由 HTTP 隧道）。
/// </summary>
public sealed class ServerListener : IAsyncDisposable
{
    private readonly ITransportFactory _transport;
    private readonly PortManager _ports;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ServerSession> _sessions = [];
    private readonly Dictionary<string, ServerSession> _sessionsByClient = [];
    private readonly object _sessionsLock = new();
    private readonly int _vhostHttpPort;
    private readonly int _vhostHttpsPort;
    private readonly int _dashboardPort;
    private readonly int _allowPortsCount;
    private readonly int _maxPortsPerClient;
    private readonly IClientIdentityResolver? _identityResolver;
    private readonly DataConnectionBindingRegistry _dataConnectionBindings;
    private readonly int _maxDataConnectionsPerClient;
    private readonly PeerSignalingRegistry _peerSignaling;
    private readonly HashSet<Task> _connectionInitializers = [];
    private readonly object _connectionInitializersLock = new();
    private readonly SemaphoreSlim _pendingConnectionSlots;
    private Task? _acceptLoopTask;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>新会话接入（用于测试/宿主收集）</summary>
    public event Action<ServerSession>? SessionAccepted;

    /// <summary>HTTP vhost 监听器（未启用时为 null）</summary>
    public VhostHttpListener? VhostHttp { get; }

    /// <summary>HTTPS vhost 监听器（未启用时为 null）</summary>
    public VhostHttpsListener? VhostHttps { get; }

    /// <summary>Dashboard（未启用时为 null）</summary>
    public DashboardListener? Dashboard { get; }

    /// <summary>主域名后缀（subdomain 拼接用）</summary>
    public string SubDomainHost { get; }

    public ServerListener(int bindPort, string token, PortManager? ports = null, int vhostHttpPort = 0, int vhostHttpsPort = 0, string subDomainHost = "", int dashboardPort = 0, System.Security.Cryptography.X509Certificates.X509Certificate2? tlsCertificate = null, string dashboardUser = "", string dashboardPassword = "", int maxPortsPerClient = 0, IClientIdentityResolver? identityResolver = null, ITransportFactory? transportFactory = null, int maxDataConnectionsPerClient = 2, TimeSpan? dataConnectionTokenLifetime = null, int maxPendingConnections = 64, ICommunicationAuthorizationProvider? communicationAuthorization = null)
    {
        _transport = transportFactory ?? TcpTlsTransport.Server(IPAddress.Any, bindPort, tlsCertificate);
        BindPort = bindPort;
        _ports = ports ?? new PortManager();
        _token = token;
        SubDomainHost = subDomainHost;
        _vhostHttpPort = vhostHttpPort;
        _vhostHttpsPort = vhostHttpsPort;
        _dashboardPort = dashboardPort;
        _allowPortsCount = _ports.GetAllowedCount();
        _maxPortsPerClient = maxPortsPerClient;
        _identityResolver = identityResolver;
        _maxDataConnectionsPerClient = Math.Max(0, maxDataConnectionsPerClient);
        _peerSignaling = new PeerSignalingRegistry(communicationAuthorization);
        _dataConnectionBindings = new DataConnectionBindingRegistry(dataConnectionTokenLifetime);
        if (maxPendingConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingConnections));
        _pendingConnectionSlots = new SemaphoreSlim(maxPendingConnections, maxPendingConnections);

        if (vhostHttpPort > 0)
        {
            VhostHttp = new VhostHttpListener(vhostHttpPort);
            VhostHttp.Start();
        }

        if (vhostHttpsPort > 0)
        {
            VhostHttps = new VhostHttpsListener(vhostHttpsPort);
            VhostHttps.Start();
        }

        if (dashboardPort > 0)
        {
            Dashboard = new DashboardListener(dashboardPort, BuildStatusJson, BuildConfigJson, BuildHealthJson,
                dashboardUser, dashboardPassword);
            Dashboard.Start();
        }
    }

    /// <summary>当前状态快照（Dashboard/TUI 用）</summary>
    public StatusResponse GetStatusSnapshot()
    {
        var response = new StatusResponse();
        lock (_sessionsLock)
        {
            foreach (var s in _sessions)
            {
                var client = new StatusClient
                {
                    ClientId = s.ClientId ?? "?",
                    Hostname = s.Hostname ?? "",
                    // 在线 = 该 clientId 的活跃会话就是当前会话（否则为断线残留条目）
                    Online = s.ClientId is not null &&
                             _sessionsByClient.TryGetValue(s.ClientId, out var cur) &&
                             ReferenceEquals(cur, s),
                };
                foreach (var p in s.GetProxiesSnapshot())
                {
                    client.Proxies.Add(new StatusProxy
                    {
                        ProxyId = p.ProxyId,
                        Group = p.Group,
                        Type = p.Type,
                        RemoteAddr = p.RemoteAddr,
                        UpBytes = p.UpBytes,
                        DownBytes = p.DownBytes,
                        Online = p.Online,
                    });
                }
                response.Clients.Add(client);
            }
        }
        return response;
    }

    /// <summary>Dashboard /api/status JSON</summary>
    private string BuildStatusJson()
        => System.Text.Json.JsonSerializer.Serialize(GetStatusSnapshot(), StatusJsonContext.Default.StatusResponse);

    /// <summary>Dashboard /api/config JSON（不含 token）</summary>
    private string BuildConfigJson()
        => System.Text.Json.JsonSerializer.Serialize(BuildServerInfo(), StatusJsonContext.Default.ServerInfo);

    /// <summary>Dashboard /api/health JSON</summary>
    private string BuildHealthJson()
        => System.Text.Json.JsonSerializer.Serialize(BuildServerInfo(), StatusJsonContext.Default.ServerInfo);

    private ServerInfo BuildServerInfo()
    {
        var (clientCount, proxyCount) = (0, 0);
        lock (_sessionsLock)
        {
            clientCount = _sessions.Count;
            proxyCount = _sessions.Sum(s => s.GetProxiesSnapshot().Count);
        }

        var uptime = DateTime.UtcNow - _startedAt;
        var uptimeText = uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
            : $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";

        return new ServerInfo
        {
            BindPort = BindPort,
            VhostHttpPort = _vhostHttpPort,
            VhostHttpsPort = _vhostHttpsPort,
            SubDomainHost = SubDomainHost,
            DashboardPort = _dashboardPort,
            AllowPortsCount = _allowPortsCount,
            Uptime = uptimeText,
            ClientCount = clientCount,
            ProxyCount = proxyCount,
        };
    }

    /// <summary>监听端口</summary>
    public int BindPort { get; }

    public void Start()
    {
        _acceptLoopTask ??= AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var conn = await _transport.AcceptAsync(_cts.Token);
                if (!_pendingConnectionSlots.Wait(0))
                {
                    await conn.DisposeAsync();
                    continue;
                }
                var initializer = InitializeConnectionAsync(conn);
                lock (_connectionInitializersLock)
                    _connectionInitializers.Add(initializer);
                _ = initializer.ContinueWith(completed =>
                {
                    lock (_connectionInitializersLock)
                        _connectionInitializers.Remove(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_cts.IsCancellationRequested) { }
    }

    private async Task InitializeConnectionAsync(ITunnelConnection connection)
    {
        var ownershipTransferred = false;
        ServerSession? controlSession = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var frame = await FrameCodec.ReadAsync(connection.Stream, timeout.Token);
            if (frame.Type != FrameType.Control || frame.ChannelId != FrameContract.ControlChannel)
                throw new ProtocolException("连接首帧必须是控制消息");

            var message = MessageCodec.Deserialize(frame.Payload);
            if (message is BindDataConnectionMessage bind)
            {
                var mux = new ChannelMultiplexer(connection);
                if (!_dataConnectionBindings.TryConsume(bind.Token, out var session, out var error) ||
                    session is null || !session.TryAttachDataConnection(mux, out error))
                {
                    await mux.SendControlAsync(MessageCodec.Serialize(
                        new BindDataConnectionAckMessage(false, error)));
                    await mux.DisposeAsync();
                    return;
                }

                ownershipTransferred = true;
                await mux.SendControlAsync(MessageCodec.Serialize(
                    new BindDataConnectionAckMessage(true, null)));
                return;
            }

            var controlMux = new ChannelMultiplexer(connection);
            controlSession = new ServerSession(controlMux, _ports, _token,
                VhostHttp, VhostHttps, SubDomainHost, _maxPortsPerClient, _identityResolver,
                _dataConnectionBindings, _maxDataConnectionsPerClient);
            ownershipTransferred = true;
            lock (_sessionsLock)
                _sessions.Add(controlSession);
            controlSession.LoggedIn += OnSessionLoggedIn;
            controlSession.Closed += OnSessionClosed;
            controlSession.PeerRequestReceived += (sender, request) => _peerSignaling.HandleRequestAsync(sender, request);
            controlSession.PeerDescriptionReceived += (sender, description) => _peerSignaling.HandleDescriptionAsync(sender, description);
            controlSession.PeerCandidateReceived += (sender, candidate) => _peerSignaling.HandleCandidateAsync(sender, candidate);
            SessionAccepted?.Invoke(controlSession);
            await controlSession.StartAsync(frame.Payload);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch
        {
            // 握手/首帧错误只关闭当前连接，不影响统一接入监听。
            if (controlSession is not null)
                await controlSession.DisposeAsync();
        }
        finally
        {
            if (!ownershipTransferred)
                await connection.DisposeAsync();
            _pendingConnectionSlots.Release();
        }
    }

    /// <summary>登录成功：同 clientId 的旧会话立即替换（客户端快速重启不再等心跳过期）</summary>
    private void OnSessionLoggedIn(ServerSession session)
    {
        if (session.AuthenticatedPeerId is not null)
            _peerSignaling.Register(session);
        lock (_sessionsLock)
        {
            if (session.ClientId is not null &&
                _sessionsByClient.TryGetValue(session.ClientId, out var old) &&
                !ReferenceEquals(old, session))
            {
                _ = old.DisposeAsync(); // 关闭旧会话 → 释放其端口
            }
            if (session.ClientId is not null)
                _sessionsByClient[session.ClientId] = session;
        }
    }

    /// <summary>按 clientId 获取在线会话（管理端用；不存在返回 null）</summary>
    public ServerSession? GetSession(string clientId)
    {
        lock (_sessionsLock)
            return _sessionsByClient.TryGetValue(clientId, out var s) ? s : null;
    }

    /// <summary>
    /// 服务端主动删除指定客户端的隧道（管理端 API）：向该客户端下发删除指令并本地释放。
    /// 返回 false 表示客户端不在线（无法下发，需调用方决定后续处理）。
    /// </summary>
    public async Task<bool> RemoveProxyAsync(string clientId, string proxyId, CancellationToken ct = default)
    {
        var session = GetSession(clientId);
        if (session is null)
            return false;
        await session.RemoveProxyAsync(proxyId, ct);
        return true;
    }

    /// <summary>会话关闭：从会话列表移除 + 清理 clientId 映射</summary>
    private void OnSessionClosed(ServerSession session)
    {
        if (session.AuthenticatedPeerId is not null)
            _peerSignaling.Unregister(session);
        lock (_sessionsLock)
        {
            // 必须从 _sessions 移除：否则断开/被替换的旧会话残留在列表，
            // 状态快照会一直显示"离线"条目，客户端重连后出现一旧一新两个客户端
            _sessions.Remove(session);
            if (session.ClientId is not null &&
                _sessionsByClient.TryGetValue(session.ClientId, out var cur) &&
                ReferenceEquals(cur, session))
            {
                _sessionsByClient.Remove(session.ClientId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_transport is IAsyncDisposable disposableTransport)
            await disposableTransport.DisposeAsync();
        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask; }
            catch (OperationCanceledException) { }
        }
        Task[] initializers;
        lock (_connectionInitializersLock)
            initializers = _connectionInitializers.ToArray();
        await Task.WhenAll(initializers);
        ServerSession[] sessions;
        lock (_sessionsLock)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
            _sessionsByClient.Clear();
        }
        // 会话释放会关闭监听并回收端口/vhost；必须等待完成后再关闭共享注册表。
        await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));
        if (VhostHttp is not null)
            await VhostHttp.DisposeAsync();
        if (VhostHttps is not null)
            await VhostHttps.DisposeAsync();
        if (Dashboard is not null)
            await Dashboard.DisposeAsync();
        _pendingConnectionSlots.Dispose();
    }
}
