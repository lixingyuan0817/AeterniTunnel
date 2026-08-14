namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>
/// 指令消息基类（服务端 → 客户端，S→C 方向的管理指令）。
/// 设计要点：
/// - TargetClientId：目标客户端（单机穿透场景为 null 即当前连接；未来组网按 clientId 寻址复用同一信封）
/// - Seq：指令序号，用于请求-响应关联与幂等（客户端回 CommandAckMessage 时原样带回）
/// 注意：JSON 多态仍平铺注册在 Message 基类（见 Message 的 JsonDerivedType），此类为纯 C# 基类。
/// </summary>
public abstract record CommandMessage : Message
{
    /// <summary>目标客户端（null = 当前连接；预留组网寻址）</summary>
    public string? TargetClientId { get; init; }

    /// <summary>指令序号（请求-响应关联/幂等）</summary>
    public long Seq { get; init; }
}
