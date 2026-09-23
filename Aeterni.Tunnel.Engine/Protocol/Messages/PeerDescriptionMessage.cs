namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>Peer SDP 描述转发；offer 只能由请求方发送，answer 只能由目标发送。</summary>
public sealed record PeerDescriptionMessage(
    string RequestId,
    bool IsOffer,
    string Sdp) : Message;
