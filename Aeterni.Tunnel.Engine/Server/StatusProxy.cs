namespace Aeterni.Tunnel.Engine.Server;

/// <summary>Dashboard /api/status 隧道条目（含流量统计）</summary>
public sealed class StatusProxy
{
    public string ProxyId { get; set; } = "";
    public string Type { get; set; } = "";
    public string RemoteAddr { get; set; } = "";

    /// <summary>上行字节（本地→远端）</summary>
    public long UpBytes { get; set; }

    /// <summary>下行字节（远端→本地）</summary>
    public long DownBytes { get; set; }

    /// <summary>在线状态（true=已注册监听；配合健康检查可标记离线）</summary>
    public bool Online { get; set; } = true;

    /// <summary>分组（default / 自定义，如 mc）</summary>
    public string Group { get; set; } = "default";
}
