namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>附加连接在同一 ATS 接入端口发送的首条绑定消息。</summary>
public sealed record BindDataConnectionMessage(string Token) : Message;
