namespace Aeterni.Tunnel.Engine.Hosting;

/// <summary>代理定义（宿主层配置）</summary>
public sealed record ProxyDefinition(
    string ProxyId,
    Protocol.LinkType LinkType,
    string LocalIp,
    int LocalPort,
    int? RemotePort = null,
    string? Domain = null,
    string? Subdomain = null,
    string? Group = null);
