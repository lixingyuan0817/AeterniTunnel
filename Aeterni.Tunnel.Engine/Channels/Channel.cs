using System.Threading.Channels;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>
/// 一条逻辑通道（单连接上的多路复用单元，AD-002）。
/// 本端 OpenChannel 分配 ChannelId；对端经 OpenTunnel 控制消息用 AcceptChannel 建立同 id 通道。
/// ReadAsync 在通道关闭后返回 null。
/// </summary>
public sealed class Channel : IAsyncDisposable
{
    private readonly System.Threading.Channels.Channel<byte[]> _queue;
    private readonly ChannelMultiplexer _owner;
    private int _completed;

    /// <summary>通道号（0 为控制通道，数据通道从 1 起）</summary>
    public ushort ChannelId { get; }

    internal Channel(ChannelMultiplexer owner, ushort channelId)
    {
        _owner = owner;
        ChannelId = channelId;
        _queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(64);
    }

    /// <summary>读取一帧数据；通道关闭时返回 null</summary>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            return await _queue.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>向对端发送一帧数据</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => _owner.SendDataAsync(ChannelId, data, ct);

    /// <summary>关闭本通道（发送 Close 帧并释放本地队列）</summary>
    public ValueTask CloseAsync(CancellationToken ct = default)
        => _owner.CloseChannelAsync(ChannelId, ct);

    internal ValueTask EnqueueAsync(byte[] data) => _queue.Writer.WriteAsync(data);

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _queue.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
