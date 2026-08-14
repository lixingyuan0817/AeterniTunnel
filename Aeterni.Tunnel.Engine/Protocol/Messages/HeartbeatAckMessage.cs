namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>心跳应答：双向</summary>
public sealed record HeartbeatAckMessage(long Ts) : Message;
