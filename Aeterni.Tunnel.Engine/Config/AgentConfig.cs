namespace Aeterni.Tunnel.Engine.Config;

/// <summary>
/// Agent（ATC）配置文件模型：对应 agent.toml。
/// </summary>
public sealed class AgentConfig
{
    /// <summary>ATS 服务端地址</summary>
    public string ServerAddr { get; set; } = "127.0.0.1";

    /// <summary>ATS 服务端端口</summary>
    public int ServerPort { get; set; } = 7000;

    /// <summary>认证 token（与服务端一致）</summary>
    public string Token { get; set; } = "";

    /// <summary>客户端标识（空 = 自动生成 agent-主机名-随机后缀，避免重复）</summary>
    public string ClientId { get; set; } = "";

    /// <summary>TLS 加密传输</summary>
    public bool UseTls { get; set; }

    /// <summary>断线重连间隔（秒）</summary>
    public int ReconnectIntervalSec { get; set; } = 5;

    /// <summary>健康检查间隔（秒，0=关闭）</summary>
    public int HealthIntervalSec { get; set; } = 10;

    public string LogFile { get; set; } = "";
    public string LogLevel { get; set; } = "info";

    /// <summary>隧道列表（[[tunnels]]）</summary>
    public List<AgentTunnelItem> Tunnels { get; set; } = new();
}

/// <summary>Agent 隧道条目（[[tunnels]]）</summary>
public sealed class AgentTunnelItem
{
    public string Name { get; set; } = "";

    /// <summary>tcp / udp / http / https</summary>
    public string Type { get; set; } = "tcp";

    public string LocalIp { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; }
    public int RemotePort { get; set; }

    /// <summary>http/https 自定义域名（如 web.example.com）</summary>
    public string Domain { get; set; } = "";

    /// <summary>http/https 子域名（如 web，配 subDomainHost 用）</summary>
    public string Subdomain { get; set; } = "";

    /// <summary>分组（如 "mc"；空 = 默认分组）</summary>
    public string Group { get; set; } = "";
}
