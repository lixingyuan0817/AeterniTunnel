namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>隧道注册请求：Agent → Server。RemotePort=0 表示由服务端随机分配。Group 为展示分组。</summary>
public sealed record RegisterProxyMessage(
    string ProxyId,
    LinkType LinkType,
    string LocalIp,
    int LocalPort,
    int? RemotePort,
    string? Domain,
    string? Subdomain,
    string? Group = null) : Message;
