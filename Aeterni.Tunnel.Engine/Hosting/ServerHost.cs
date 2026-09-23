using Aeterni.Tunnel.Engine.Server;
using System.Security.Cryptography.X509Certificates;

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
        if (!options.UseTls && !options.AllowInsecureTransport)
            throw new InvalidOperationException("明文客户端接入已禁用；如需旧部署迁移，必须同时显式设置 AllowInsecureTransport=true。");

        X509Certificate2? certificate = options.TlsCertificate;
        if (options.UseTls && certificate is null)
        {
            if (string.IsNullOrWhiteSpace(options.TlsCertificatePath))
                throw new InvalidOperationException("TLS 客户端接入已启用，但未配置 TlsCertificatePath。");
            if (!File.Exists(options.TlsCertificatePath))
                throw new FileNotFoundException("TLS 接入证书不存在。", options.TlsCertificatePath);

            certificate = X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(options.TlsCertificatePath),
                string.IsNullOrEmpty(options.TlsCertificatePassword) ? null : options.TlsCertificatePassword);
        }

        _listener = new ServerListener(
            options.BindPort,
            options.Token,
            ports: options.AllowPorts is null ? null : new PortManager(allowed: options.AllowPorts),
            vhostHttpPort: options.VhostHttpPort,
            vhostHttpsPort: options.VhostHttpsPort,
            subDomainHost: options.SubDomainHost,
            dashboardPort: options.DashboardPort,
            tlsCertificate: certificate,
            dashboardUser: options.DashboardUser,
            dashboardPassword: options.DashboardPassword,
            maxPortsPerClient: options.MaxPortsPerClient,
            identityResolver: options.IdentityResolver,
            transportFactory: options.TransportFactory);

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
