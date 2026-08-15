namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>隧道注销：Agent → Server（幂等）</summary>
public sealed record UnregisterProxyMessage(string ProxyId) : Message;
