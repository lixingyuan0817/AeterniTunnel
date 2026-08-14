namespace Aeterni.Tunnel.Engine.Protocol;

/// <summary>
/// 一帧：固定头 + 负载。ChannelId=0 表示控制通道。
/// </summary>
public readonly record struct Frame(FrameType Type, ushort ChannelId, byte[] Payload)
{
    public static Frame Control(ushort channelId, byte[] payload) => new(FrameType.Control, channelId, payload);
    public static Frame Data(ushort channelId, byte[] payload) => new(FrameType.Data, channelId, payload);
}
