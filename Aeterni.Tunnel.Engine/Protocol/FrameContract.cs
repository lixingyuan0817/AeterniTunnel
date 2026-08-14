namespace Aeterni.Tunnel.Engine.Protocol;

/// <summary>
/// 帧协议常量（契约，先冻结再实现）。
/// 帧头：| Magic(2) | Version(1) | Type(1) | ChannelId(2) | Length(4) | = 10 字节，大端序。
/// </summary>
public static class FrameContract
{
    /// <summary>"AT"（'A'=0x41, 'T'=0x54，大端）</summary>
    public const ushort Magic = 0x4154;

    public const byte Version = 0x01;

    public const int HeaderLength = 10;

    /// <summary>单帧负载上限（防恶意大包）</summary>
    public const int MaxPayloadLength = 4 * 1024 * 1024;

    /// <summary>控制通道号</summary>
    public const ushort ControlChannel = 0;
}
