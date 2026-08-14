namespace Aeterni.Tunnel.Engine.Server;

/// <summary>服务端信息（/api/config、/api/health 响应，token 不返回）</summary>
public sealed class ServerInfo
{
    public int BindPort { get; set; }
    public int VhostHttpPort { get; set; }
    public int VhostHttpsPort { get; set; }
    public string SubDomainHost { get; set; } = "";
    public int DashboardPort { get; set; }
    public int AllowPortsCount { get; set; }
    public string Uptime { get; set; } = "";
    public int ClientCount { get; set; }
    public int ProxyCount { get; set; }
}
