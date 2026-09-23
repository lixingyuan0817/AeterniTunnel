namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>SDP/候选转发结果。</summary>
public sealed record PeerSignalAckMessage(
    string RequestId,
    bool Ok,
    string? Error,
    string SignalType) : Message;
