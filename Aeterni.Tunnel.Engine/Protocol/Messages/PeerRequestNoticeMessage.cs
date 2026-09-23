namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>服务端转发给目标 Peer 的已授权请求通知。</summary>
public sealed record PeerRequestNoticeMessage(
    string RequestId,
    string RequesterPeerId,
    string ServiceId,
    long ExpiresUnixMilliseconds,
    string? LeaseToken = null) : Message;
