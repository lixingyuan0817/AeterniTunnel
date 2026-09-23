namespace Aeterni.Tunnel.Engine.Protocol;

/// <summary>控制会话协商的能力；协议版本与产品版本分开。</summary>
[Flags]
public enum ProtocolCapabilities : ulong
{
    None = 0,
    ReliableStream = 1UL << 0,
    ReliableMessage = 1UL << 1,
    RealtimeDatagram = 1UL << 2,
    PeerSession = 1UL << 3,
    UdpSourceAssociation = 1UL << 4,
    /// <summary>同一接入端口的短期凭证数据连接绑定、通道公平调度与显式重置。</summary>
    ConnectionIsolation = 1UL << 5,
}

public static class ProtocolContract
{
    public const int CurrentVersion = 1;
    public const ProtocolCapabilities SupportedCapabilities =
        ProtocolCapabilities.ReliableStream |
        ProtocolCapabilities.ReliableMessage |
        ProtocolCapabilities.UdpSourceAssociation |
        ProtocolCapabilities.ConnectionIsolation;
}
