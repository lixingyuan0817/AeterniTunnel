namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>登录应答：Server → Agent</summary>
public sealed record HelloAckMessage(bool Ok, string? Error, string ServerVersion) : Message;
