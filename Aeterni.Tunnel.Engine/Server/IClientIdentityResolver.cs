using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 把已认证 Hello 映射到稳定 Peer 身份。返回 null 表示拒绝。
/// 默认兼容模式使用 Hello.ClientId；生产宿主应注入基于设备凭证/授权目录的实现。
/// </summary>
public interface IClientIdentityResolver
{
    ValueTask<string?> ResolveAsync(HelloMessage hello, CancellationToken ct = default);
}
