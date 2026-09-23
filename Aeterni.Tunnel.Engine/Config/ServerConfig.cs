using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Config;

/// <summary>服务端配置（server.toml 模型）</summary>
public sealed class ServerConfig
{
    public int BindPort { get; set; } = 7000;
    public string Token { get; set; } = "";
    /// <summary>客户端接入是否启用 TLS；生产默认启用，明文迁移必须显式关闭并允许不安全传输。</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>ATS 接入证书（PFX/PKCS#12）路径；UseTls=true 且为空时拒绝启动。</summary>
    public string TlsCertificatePath { get; set; } = "";

    /// <summary>ATS 接入证书私钥密码；不写入日志。</summary>
    public string TlsCertificatePassword { get; set; } = "";

    /// <summary>仅用于明确的旧明文迁移；不允许 TLS 失败后自动降级。</summary>
    public bool AllowInsecureTransport { get; set; }
    public int VhostHttpPort { get; set; }
    public int VhostHttpsPort { get; set; }
    public string SubDomainHost { get; set; } = "";
    public int DashboardPort { get; set; }

    /// <summary>Dashboard 鉴权用户（空 = 不鉴权）</summary>
    public string DashboardUser { get; set; } = "";

    /// <summary>Dashboard 鉴权密码</summary>
    public string DashboardPassword { get; set; } = "";

    /// <summary>每客户端最大隧道端口数（0 = 不限；vhost 域名隧道不计）</summary>
    public int MaxPortsPerClient { get; set; }

    /// <summary>每客户端在同一 ATS 接入端口上的附加数据连接上限（0 = 禁用）。</summary>
    public int MaxDataConnectionsPerClient { get; set; } = 2;

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
