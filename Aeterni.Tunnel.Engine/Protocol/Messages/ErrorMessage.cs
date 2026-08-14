namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>通用错误：双向</summary>
public sealed record ErrorMessage(int Code, string Message) : Message;
