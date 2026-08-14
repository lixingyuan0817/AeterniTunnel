using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Transport;
using System.Net;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 服务端监听器：接受 Agent 连接，每个连接创建一个 ServerSession。
/// 可选 vhost HTTP 监听（vhostHttpPort &gt; 0 时启用，按 Host 路由 HTTP 代理）。
/// </summary>
public sealed class ServerListener : IAsyncDisposable
{
    private readonly TcpTlsTransport _transport;
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

    public ServerListener(int bindPort, string token, PortManager? ports = null, int vhostHttpPort = 0, int vhostHttpsPort = 0, string subDomainHost = "", int dashboardPort = 0, System.Security.Cryptography.X509Certificates.X509Certificate2? tlsCertificate = null, string dashboardUser = "", string dashboardPassword = "", int maxPortsPerClient = 0)
    {
        _transport = TcpTlsTransport.Server(IPAddress.Any, bindPort, tlsCertificate);
        BindPort = bindPort;
        _ports = ports ?? new PortManager();
        _token = token;
        SubDomainHost = subDomainHost;
        _vhostHttpPort = vhostHttpPort;
        _vhostHttpsPort = vhostHttpsPort;
        _dashboardPort = dashboardPort;
        _allowPortsCount = _ports.GetAllowedCount();
        _maxPortsPerClient = maxPortsPerClient;

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
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var conn = await _transport.AcceptAsync(_cts.Token);
                var mux = new ChannelMultiplexer(conn);
                var session = new ServerSession(mux, _ports, _token, VhostHttp, VhostHttps, SubDomainHost, _maxPortsPerClient);
                lock (_sessionsLock)
                    _sessions.Add(session);
                session.LoggedIn += OnSessionLoggedIn;
                session.Closed += OnSessionClosed;
                SessionAccepted?.Invoke(session);
                session.Start();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>登录成功：同 clientId 的旧会话立即替换（客户端快速重启不再等心跳过期）</summary>
    private void OnSessionLoggedIn(ServerSession session)
    {
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

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        lock (_sessionsLock)
        {
            // 遍历副本：session.DisposeAsync 会触发 Closed → OnSessionClosed → _sessions.Remove，
            // 直接枚举原集合会抛 "Collection was modified"
            foreach (var session in _sessions.ToArray())
                _ = session.DisposeAsync();
            _sessions.Clear();
            _sessionsByClient.Clear();
        }
        if (VhostHttp is not null)
            _ = VhostHttp.DisposeAsync();
        if (VhostHttps is not null)
            _ = VhostHttps.DisposeAsync();
        if (Dashboard is not null)
            _ = Dashboard.DisposeAsync();
        return _transport.DisposeAsync();
    }
}
