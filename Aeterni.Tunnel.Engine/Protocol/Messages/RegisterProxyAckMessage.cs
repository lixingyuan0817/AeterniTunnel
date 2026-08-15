namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>隧道注册应答：Server → Agent</summary>
public sealed record RegisterProxyAckMessage(string ProxyId, bool Ok, string? RemoteAddr, string? Error) : Message;
