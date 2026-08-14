using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Config;

/// <summary>服务端配置（server.toml 模型）</summary>
public sealed class ServerConfig
{
    public int BindPort { get; set; } = 7000;
    public string Token { get; set; } = "";
    public int VhostHttpPort { get; set; }
    public int VhostHttpsPort { get; set; }
    public string SubDomainHost { get; set; } = "";
    public int DashboardPort { get; set; }

    /// <summary>Dashboard 鉴权用户（空 = 不鉴权）</summary>
    public string DashboardUser { get; set; } = "";

    /// <summary>Dashboard 鉴权密码</summary>
    public string DashboardPassword { get; set; } = "";

    /// <summary>每客户端最大代理端口数（0 = 不限；vhost 域名代理不计）</summary>
    public int MaxPortsPerClient { get; set; }

    /// <summary>网页管理器访问 token（sha256+salt 哈希，hex；仅初始化/重置时打印明文）</summary>
    public string WebToken { get; set; } = "";

    /// <summary>webToken 加盐哈希的盐（hex，16 字节）</summary>
    public string WebTokenSalt { get; set; } = "";

    /// <summary>REST API 开关（默认关闭；页面走 SignalR 不依赖）</summary>
    public bool ApiEnabled { get; set; }

    /// <summary>网页管理器绑定地址（默认仅本机）</summary>
    public string WebBind { get; set; } = "127.0.0.1:7500";

    public List<PortRange>? AllowPorts { get; set; }
    public LogConfig Log { get; set; } = new();
}
