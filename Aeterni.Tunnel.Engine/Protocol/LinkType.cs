namespace Aeterni.Tunnel.Engine.Protocol
{
    [Flags]
    public enum LinkType : ushort
    {
        None = 0,

        Control = 1 << 0,   // 1

        Tcp = 1 << 1,   // 2

        Udp = 1 << 2,   // 4

        Http = 1 << 3,   // 8

        Https = 1 << 4,   // 16

        Stcp = 1 << 5,   // 32

        Kcp = 1 << 6,   // 64

        Quic = 1 << 7,   // 128

        Xtcp = 1 << 8,   // 256
    }
}
