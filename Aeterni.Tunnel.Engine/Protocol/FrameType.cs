namespace Aeterni.Tunnel.Engine.Protocol;

/// <summary>
/// 帧类型。
/// </summary>
public enum FrameType : byte
{
    /// <summary>控制消息（Payload = JSON 消息契约）</summary>
    Control = 1,

    /// <summary>数据负载（Payload = 原始字节，隧道透传）</summary>
    Data = 2,

    /// <summary>心跳请求</summary>
    Ping = 3,

    /// <summary>心跳应答</summary>
    Pong = 4,

    /// <summary>通道关闭（带 FIN 语义）</summary>
    Close = 5,
}
