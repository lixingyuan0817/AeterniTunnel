using System.Text;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Wire;

namespace Aeterni.Tunnel.Engine.Tests;

public class FrameCodecTests
{
    private static MemoryStream StreamWith(params byte[][] chunks)
    {
        var ms = new MemoryStream();
        foreach (var c in chunks) ms.Write(c);
        ms.Position = 0;
        return ms;
    }

    private static async Task<Frame> ReadOneAsync(Stream s) => await FrameCodec.ReadAsync(s);

    [Fact]
    public void Encode_HeaderMatchesContract()
    {
        var frame = Frame.Control(7, [0xAA, 0xBB]);
        var data = FrameCodec.Encode(frame);

        Assert.Equal(12, data.Length);                       // 10 头 + 2 负载
        Assert.Equal((byte)'A', data[0]);
        Assert.Equal((byte)'T', data[1]);
        Assert.Equal(FrameContract.Version, data[2]);
        Assert.Equal((byte)FrameType.Control, data[3]);
        Assert.Equal(0, data[4]);                            // channelId 大端
        Assert.Equal(7, data[5]);
        Assert.Equal(0, data[6]);                            // length 大端
        Assert.Equal(0, data[7]);
        Assert.Equal(0, data[8]);
        Assert.Equal(2, data[9]);
    }

    [Fact]
    public async Task ReadAsync_RoundTrips_ControlAndData()
    {
        var frames = new[]
        {
            Frame.Control(0, Encoding.UTF8.GetBytes("{\"msg\":\"hello\"}")),
            Frame.Data(1, [0x00, 0x01, 0x02, 0xFF]),
            new Frame(FrameType.Ping, 0, []),
            new Frame(FrameType.Close, 3, []),
        };

        using var ms = new MemoryStream();
        foreach (var f in frames)
            await FrameCodec.WriteAsync(ms, f);
        ms.Position = 0;

        foreach (var expected in frames)
        {
            var actual = await ReadOneAsync(ms);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.ChannelId, actual.ChannelId);
            Assert.Equal(expected.Payload, actual.Payload);
        }
    }

    [Fact]
    public async Task ReadAsync_HandlesFragmentedWrites()
    {
        var frame = Frame.Data(9, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        var data = FrameCodec.Encode(frame);

        // 按 1 字节/块分片写入，验证半包处理
        using var ms = StreamWith(data.Select(b => new[] { b }).ToArray());
        var actual = await ReadOneAsync(ms);
        Assert.Equal(frame.Payload, actual.Payload);
        Assert.Equal(9, actual.ChannelId);
    }

    [Fact]
    public async Task ReadAsync_HandlesMultipleFramesInOneWrite()
    {
        var f1 = Frame.Control(0, [1, 2, 3]);
        var f2 = Frame.Data(2, [4, 5]);
        using var ms = StreamWith(FrameCodec.Encode(f1), FrameCodec.Encode(f2));

        var a1 = await ReadOneAsync(ms);
        var a2 = await ReadOneAsync(ms);
        Assert.Equal(f1.Payload, a1.Payload);
        Assert.Equal(f2.Payload, a2.Payload);
        Assert.Equal(2, a2.ChannelId);
    }

    [Fact]
    public async Task ReadAsync_RejectsBadMagic()
    {
        using var ms = StreamWith([0x00, 0x11, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        await Assert.ThrowsAsync<ProtocolException>(() => ReadOneAsync(ms));
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedPayload()
    {
        // 伪造 length = MaxPayloadLength + 1（大端）
        var header = new byte[10];
        header[0] = (byte)'A'; header[1] = (byte)'T';
        header[2] = FrameContract.Version;
        header[3] = (byte)FrameType.Data;
        var len = FrameContract.MaxPayloadLength + 1;
        header[6] = (byte)(len >> 24);
        header[7] = (byte)(len >> 16);
        header[8] = (byte)(len >> 8);
        header[9] = (byte)len;

        using var ms = StreamWith(header);
        await Assert.ThrowsAsync<ProtocolException>(() => ReadOneAsync(ms));
    }

    [Fact]
    public async Task ReadAsync_EmptyPayload_Ok()
    {
        var frame = new Frame(FrameType.Close, 5, []);
        using var ms = StreamWith(FrameCodec.Encode(frame));
        var actual = await ReadOneAsync(ms);
        Assert.Equal(FrameType.Close, actual.Type);
        Assert.Equal(5, actual.ChannelId);
        Assert.Empty(actual.Payload);
    }
}
