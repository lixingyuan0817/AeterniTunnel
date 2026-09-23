using System.Buffers.Binary;

namespace Aeterni.Tunnel.Engine.Protocol;

/// <summary>现有 UDP 隧道的来源关联封装；只用于 ATS/ATC 隧道数据面，不等同于 P2P 数据报。</summary>
public static class UdpSourceEnvelope
{
    public const int HeaderLength = sizeof(uint);

    public static byte[] Encode(uint sourceId, ReadOnlySpan<byte> payload)
    {
        if (sourceId == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));

        var result = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, HeaderLength), sourceId);
        payload.CopyTo(result.AsSpan(HeaderLength));
        return result;
    }

    public static bool TryDecode(ReadOnlyMemory<byte> data, out uint sourceId, out ReadOnlyMemory<byte> payload)
    {
        if (data.Length < HeaderLength)
        {
            sourceId = 0;
            payload = default;
            return false;
        }

        sourceId = BinaryPrimitives.ReadUInt32BigEndian(data.Span[..HeaderLength]);
        payload = data[HeaderLength..];
        return sourceId != 0;
    }
}
