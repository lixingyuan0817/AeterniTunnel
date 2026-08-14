namespace Aeterni.Tunnel.Engine.Server;

/// <summary>Dashboard /api/status 客户端条目</summary>
public sealed class StatusClient
{
    public string ClientId { get; set; } = "";

    /// <summary>客户端主机名（Hello 握手提供）</summary>
    public string Hostname { get; set; } = "";

    /// <summary>当前是否在线（会话在 _sessionsByClient 中 = 活跃连接；否则为断线残留条目）</summary>
    public bool Online { get; set; }

    public List<StatusProxy> Proxies { get; set; } = new();
}
