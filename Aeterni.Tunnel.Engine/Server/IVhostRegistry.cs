using Aeterni.Tunnel.Engine.Channels;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// vhost 路由注册表（HTTP Host / HTTPS SNI 共用）。
/// </summary>
public interface IVhostRegistry
{
    /// <summary>注册 域名 → (连接, 隧道)，Session 创建监听时调用</summary>
    void Register(string host, ChannelMultiplexer mux, string proxyId);

    /// <summary>注销域名路由</summary>
    void Unregister(string host);
}
