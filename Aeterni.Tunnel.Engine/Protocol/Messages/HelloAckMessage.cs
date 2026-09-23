namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>登录应答：Server → Agent。Capabilities 是双方交集。</summary>
public sealed record HelloAckMessage(
    bool Ok,
    string? Error,
    string ServerVersion,
    ulong Capabilities = 0) : Message;
