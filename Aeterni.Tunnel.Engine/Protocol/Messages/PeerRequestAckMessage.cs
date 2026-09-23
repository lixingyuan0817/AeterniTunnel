namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>Peer 请求创建结果；成功时带服务端裁剪后的租约到期时间。</summary>
public sealed record PeerRequestAckMessage(
    string RequestId,
    bool Ok,
    string? Error,
    long? ExpiresUnixMilliseconds = null,
    string? LeaseToken = null) : Message;
