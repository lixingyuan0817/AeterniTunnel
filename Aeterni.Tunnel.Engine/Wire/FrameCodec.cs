using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;
using System.IO;
using Aeterni.Tunnel.Engine.Protocol;

namespace Aeterni.Tunnel.Engine.Wire;

/// <summary>
/// 帧编解码：长度前缀读写，处理半包/粘包；Magic 校验；超长拒绝。
/// 基于 Stream.ReadExactly（内部循环，天然处理半包）；粘包由调用方循环 ReadAsync 消费。
/// </summary>
public static class FrameCodec
{
    /// <summary>把帧头写入 span（不校验长度上限，调用方保证）</summary>
    public static void EncodeHeader(Span<byte> dst, FrameType type, ushort channelId, int length)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..2], FrameContract.Magic);
        dst[2] = FrameContract.Version;
        dst[3] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..6], channelId);
        BinaryPrimitives.WriteInt32BigEndian(dst[6..10], length);
    }

    public static byte[] Encode(Frame frame)
    {
        var data = new byte[FrameContract.HeaderLength + frame.Payload.Length];
        EncodeHeader(data, frame.Type, frame.ChannelId, frame.Payload.Length);
        frame.Payload.CopyTo(data.AsSpan(FrameContract.HeaderLength));
        return data;
    }

    /// <summary>写一帧（头 + 负载）</summary>
    public static async ValueTask WriteAsync(Stream stream, Frame frame, CancellationToken ct = default)
        => await WriteAsync(stream, frame.Type, frame.ChannelId, frame.Payload, ct);

    /// <summary>写一帧并在调用返回前消费 payload；使用清零的池化缓冲减少热路径分配。</summary>
    public static async ValueTask WriteAsync(Stream stream, FrameType type, ushort channelId,
        ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > FrameContract.MaxPayloadLength)
            throw new ProtocolException($"帧长度非法：{payload.Length}");
        var length = FrameContract.HeaderLength + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            EncodeHeader(buffer.AsSpan(0, FrameContract.HeaderLength), type, channelId, payload.Length);
            payload.Span.CopyTo(buffer.AsSpan(FrameContract.HeaderLength, payload.Length));
            await stream.WriteAsync(buffer.AsMemory(0, length), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 读一帧。半包由 ReadExactly 处理；调用方循环调用以消费粘包。
    /// 抛 <see cref="ProtocolException"/>：Magic 不符 / 版本不符 / 长度非法。
    /// </summary>
    public static async ValueTask<Frame> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = ArrayPool<byte>.Shared.Rent(FrameContract.HeaderLength);
        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(0, FrameContract.HeaderLength), ct);

            var magic = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            if (magic != FrameContract.Magic)
                throw new ProtocolException($"非法 Magic：0x{magic:X4}");

            var version = header[2];
            if (version != FrameContract.Version)
                throw new ProtocolException($"不支持的协议版本：{version}");

            var type = (FrameType)header[3];
            var channelId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(6, 4));

            if (length < 0 || length > FrameContract.MaxPayloadLength)
                throw new ProtocolException($"帧长度非法：{length}");

            var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
            if (length > 0)
                await stream.ReadExactlyAsync(payload, ct);

            return new Frame(type, channelId, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header.AsSpan(0, FrameContract.HeaderLength));
            ArrayPool<byte>.Shared.Return(header);
        }
    }
}
