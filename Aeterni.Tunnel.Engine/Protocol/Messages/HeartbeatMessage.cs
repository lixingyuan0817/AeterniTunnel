namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>心跳请求：双向</summary>
public sealed record HeartbeatMessage(long Ts) : Message;
