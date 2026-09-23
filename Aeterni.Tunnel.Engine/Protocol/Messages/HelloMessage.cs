namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>登录请求：Agent → Server。新增字段带默认值，旧 v1 JSON 可继续反序列化。</summary>
public sealed record HelloMessage(
    string ClientId,
    int Version,
    string Token,
    string Hostname,
    ulong Capabilities = 0) : Message;
