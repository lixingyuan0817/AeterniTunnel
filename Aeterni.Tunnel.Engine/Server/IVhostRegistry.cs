using Aeterni.Tunnel.Engine.Channels;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// vhost 路由注册表（HTTP Host / HTTPS SNI 共用）。
/// </summary>
public interface IVhostRegistry
{
    /// <summary>
    /// 原子注册 域名 → (连接, 隧道)。域名已被任意会话占用时返回 false，
    /// 不允许后注册会话覆盖现有路由。
    /// </summary>
    bool TryRegister(string host, ChannelMultiplexer owner,
        Func<ChannelMultiplexer> selectMultiplexer, string proxyId);

    /// <summary>
    /// 仅当域名仍属于指定连接和隧道时注销，避免旧会话清理误删新会话路由。
    /// </summary>
    bool Unregister(string host, ChannelMultiplexer owner, string proxyId);
}
