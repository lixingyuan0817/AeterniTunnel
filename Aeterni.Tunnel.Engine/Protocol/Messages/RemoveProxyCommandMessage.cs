namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>
/// 服务端指令：删除（注销）指定隧道。
/// 客户端收到后：停止本地隧道、从配置目标列表移除（重连不再重注册）、回 CommandAckMessage。
/// 幂等：目标隧道不存在时也视为成功（ack ok=true）。
/// TargetClientId / Seq 继承自 CommandMessage，通过对象初始化器设置。
/// </summary>
public sealed record RemoveProxyCommandMessage(
    string ProxyId,
    string? Reason = null) : CommandMessage;
