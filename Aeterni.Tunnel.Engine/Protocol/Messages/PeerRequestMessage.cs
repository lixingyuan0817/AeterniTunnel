namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>已认证 Peer 发起的访问请求；目标与 serviceId 由服务端授权提供者校验。</summary>
public sealed record PeerRequestMessage(
    string RequestId,
    string TargetPeerId,
    string ServiceId,
    long ExpiresUnixMilliseconds) : Message;
