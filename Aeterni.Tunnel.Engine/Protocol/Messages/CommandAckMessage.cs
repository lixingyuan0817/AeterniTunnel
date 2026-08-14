namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>
/// 指令执行结果（客户端 → 服务端，响应 RemoveProxyCommandMessage 等指令）。
/// Command：指令类型标识（如 "removeProxy"）；ProxyId：指令作用的目标隧道。
/// Seq：回显指令序号，供服务端做请求-响应关联。
/// 协议向后兼容：客户端收到未知指令时回 Command = 原指令名、Ok = false、Error = "unsupported"。
/// </summary>
public sealed record CommandAckMessage(
    string Command,
    string ProxyId,
    bool Ok,
    string? Error = null,
    long Seq = 0) : Message;
