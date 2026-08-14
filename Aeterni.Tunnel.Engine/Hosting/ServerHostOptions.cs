using System.Security.Cryptography.X509Certificates;

namespace Aeterni.Tunnel.Engine.Hosting;

/// <summary>Server 宿主配置</summary>
public sealed record ServerHostOptions(
    int BindPort,
    string Token,
    int VhostHttpPort = 0,
    int VhostHttpsPort = 0,
    string SubDomainHost = "",
    int DashboardPort = 0,
    X509Certificate2? TlsCertificate = null,
    IReadOnlyList<Server.PortRange>? AllowPorts = null,
    string DashboardUser = "",
    string DashboardPassword = "",
    int MaxPortsPerClient = 0,
    string WebBind = "127.0.0.1:7500",
    bool ApiEnabled = false);
