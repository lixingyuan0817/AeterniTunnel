namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>
/// 端口策略（Server → Agent，登录成功后下发）：服务端 allowPorts 白名单展开（空 = 不限制）
/// + 每客户端端口上限。客户端用于添加隧道时前置校验（端口不在白名单内即时提示，避免注册失败）。
/// </summary>
public sealed record PortPolicyMessage(int[] AllowPorts, int MaxPortsPerClient) : Message;
