namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>登录请求：Agent → Server</summary>
public sealed record HelloMessage(string ClientId, int Version, string Token, string Hostname) : Message;
