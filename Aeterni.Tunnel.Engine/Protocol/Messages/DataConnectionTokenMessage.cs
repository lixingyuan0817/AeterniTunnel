namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>短期一次性附加数据连接凭证；仅在协商 ConnectionIsolation 后发送。</summary>
public sealed record DataConnectionTokenMessage(string Token, long ExpiresUnixMilliseconds) : Message;
