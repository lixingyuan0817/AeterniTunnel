using Aeterni.Tunnel.Engine.Config;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Server;
using Microsoft.AspNetCore.Hosting;

namespace Aeterni.Tunnel.Web.Status;

/// <summary>
/// Aeterni 管理端数据服务：包装内嵌 ATS 的 ServerListener，提供真实状态快照与隧道速率差分。
/// 组件轮询调用 GetSnapshot（建议 2-3s）；速率 = 两次采样字节差 / 时间差。
/// 另提供 ATS 运行时重启（设置页改 bindPort 等场景，写回 server.toml 后重建监听器）。
/// </summary>
public sealed class AeterniServerStatusService
{
    private readonly ServerHost _host;
    private readonly string _serverToml;
    private readonly object _lock = new();
    private readonly List<string> _logs = new();
    private const int MaxLogs = 200;

    // 上次采样：clientId|proxyId → (up, down, timestamp)
    private readonly Dictionary<string, (long Up, long Down, DateTime Ts)> _last = new();

    public AeterniServerStatusService(ServerHost host, IWebHostEnvironment env)
    {
        _host = host;
        _serverToml = Path.Combine(env.ContentRootPath, "server.toml");
        // 缓冲 ATS 日志（控制台首页日志流用；同时 Program.cs 已转发到 ILogger）
        _host.LogLine += line =>
        {
            lock (_lock)
            {
                _logs.Add($"{DateTime.Now:HH:mm:ss} {line}");
                if (_logs.Count > MaxLogs) _logs.RemoveRange(0, _logs.Count - MaxLogs);
            }
        };
    }

    public ServerListener? Listener => _host.Listener;

    public bool IsRunning => _host.Listener is not null;

    /// <summary>当前 ATS 监听端口（server.toml bindPort）</summary>
    public int BindPort => ConfigLoader.Load(_serverToml)?.BindPort ?? 0;

    /// <summary>最近 ATS 日志（最新在前）</summary>
    public IReadOnlyList<string> RecentLogs
    {
        get { lock (_lock) return _logs.AsEnumerable().Reverse().ToList(); }
    }

    /// <summary>读取当前 server.toml 配置（设置页表单初始化用）</summary>
    public ServerConfig LoadConfig()
        => ConfigLoader.Load(_serverToml) ?? new ServerConfig();

    /// <summary>
    /// 保存完整服务端配置：写回 server.toml 并运行时重启 ATS（端口/token/vhost 等变更立即生效）。
    /// </summary>
    public async Task<bool> SaveConfig(ServerConfig cfg)
    {
        ConfigLoader.SaveServer(_serverToml, cfg);
        await _host.RestartAsync(ConfigLoader.ToHostOptions(cfg));
        return true;
    }

    /// <summary>
    /// 修改 ATS 监听端口：写回 server.toml 并运行时重启监听器（已连 ATC 会断开重连）。
    /// </summary>
    public async Task<bool> RestartAsync(int newBindPort)
    {
        if (newBindPort is < 1 or > 65535)
            return false;
        var cfg = ConfigLoader.Load(_serverToml) ?? new ServerConfig();
        cfg.BindPort = newBindPort;
        ConfigLoader.SaveServer(_serverToml, cfg);
        await _host.RestartAsync(ConfigLoader.ToHostOptions(cfg));
        return true;
    }

    /// <summary>当前快照（无监听器时返回空快照）</summary>
    public StatusResponse GetSnapshot()
        => _host.Listener?.GetStatusSnapshot() ?? new StatusResponse();

    /// <summary>
    /// 快照 + 速率（bytes/s，上行/下行，按采样间隔差分）。
    /// 首采样无历史 → 速率为 0。
    /// </summary>
    public (StatusResponse Snapshot, IReadOnlyDictionary<string, (float Up, float Down)> Rates) GetSnapshotWithRates()
    {
        var snapshot = GetSnapshot();
        var now = DateTime.UtcNow;
        var rates = new Dictionary<string, (float, float)>();

        lock (_lock)
        {
            foreach (var c in snapshot.Clients)
            {
                foreach (var p in c.Proxies)
                {
                    var key = $"{c.ClientId}|{p.ProxyId}";
                    if (_last.TryGetValue(key, out var prev))
                    {
                        var secs = (float)Math.Max(0.1, (now - prev.Ts).TotalSeconds);
                        rates[key] = ((p.UpBytes - prev.Up) / secs, (p.DownBytes - prev.Down) / secs);
                    }
                    _last[key] = (p.UpBytes, p.DownBytes, now);
                }
            }
            // 清理已消失隧道的缓存
            var live = new HashSet<string>(snapshot.Clients.SelectMany(c => c.Proxies.Select(p => $"{c.ClientId}|{p.ProxyId}")));
            foreach (var k in _last.Keys.Where(k => !live.Contains(k)).ToList())
                _last.Remove(k);
        }

        return (snapshot, rates);
    }
}
