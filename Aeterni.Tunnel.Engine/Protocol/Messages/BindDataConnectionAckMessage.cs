namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>附加数据连接绑定结果。</summary>
public sealed record BindDataConnectionAckMessage(bool Ok, string? Error) : Message;
