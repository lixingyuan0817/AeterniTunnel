using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Hosting;

/// <summary>
/// Server 宿主：封装 ServerListener 生命周期，供 CLI/GUI 使用。
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private ServerListener? _listener;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public event Action<string>? LogLine;

    /// <summary>启动时刻（uptime 展示）</summary>
    public DateTime StartedAt => _startedAt;

    /// <summary>底层监听器（Dashboard/vhost 等公开状态可经此访问）</summary>
    public ServerListener? Listener => _listener;

    public void Start(ServerHostOptions options)
    {
        _listener = new ServerListener(
            options.BindPort,
            options.Token,
            ports: options.AllowPorts is null ? null : new PortManager(allowed: options.AllowPorts),
            vhostHttpPort: options.VhostHttpPort,
            vhostHttpsPort: options.VhostHttpsPort,
            subDomainHost: options.SubDomainHost,
            dashboardPort: options.DashboardPort,
            tlsCertificate: options.TlsCertificate,
            dashboardUser: options.DashboardUser,
            dashboardPassword: options.DashboardPassword,
            maxPortsPerClient: options.MaxPortsPerClient);

        _listener.SessionAccepted += s => s.LogLine += (_, line) => LogLine?.Invoke(line);
        _listener.Start();
    }

    public ValueTask DisposeAsync() => _listener?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>
    /// 以新配置重启 ATS（设置页修改端口等场景）：先释放旧监听器（含全部会话/端口），再按新配置启动。
    /// LogLine 事件挂在 ServerHost 上，重启后继续生效。
    /// </summary>
    public async ValueTask RestartAsync(ServerHostOptions options)
    {
        await (_listener?.DisposeAsync() ?? ValueTask.CompletedTask);
        _listener = null;
        Start(options);
    }
}
