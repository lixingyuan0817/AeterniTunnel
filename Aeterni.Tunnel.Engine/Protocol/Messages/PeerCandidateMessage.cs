namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>Peer ICE 候选转发；每个租约的候选数量和长度由服务端限制。</summary>
public sealed record PeerCandidateMessage(
    string RequestId,
    string Candidate,
    string? SdpMid = null,
    bool EndOfCandidates = false) : Message;
