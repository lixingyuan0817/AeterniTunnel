using System.Threading.Channels;
using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>
/// 一条逻辑通道（单连接上的多路复用单元，AD-002）。
/// 本端 OpenChannel 分配 ChannelId；对端经 OpenTunnel 控制消息用 AcceptChannel 建立同 id 通道。
/// ReadAsync 在通道关闭后返回 null。
/// </summary>
public sealed class Channel : IAsyncDisposable
{
    private readonly System.Threading.Channels.Channel<byte[]> _queue;
    private readonly AsyncByteBudget _queuedBytes;
    private readonly OutboundFlowGate _sendGate;
    private readonly RemoteFlowWindow _remoteFlowWindow;
    private readonly int _creditPacketThreshold;
    private readonly long _creditByteThreshold;
    private readonly ChannelMultiplexer _owner;
    private int _readsCompleted;
    private int _writesCompleted;
    private CommunicationException? _failure;
    private int _flowControlEnabled;
    private int _consumedPackets;
    private long _consumedBytes;

    /// <summary>通道号（0 为控制通道，数据通道从 1 起）</summary>
    public ushort ChannelId { get; }

    internal Channel(ChannelMultiplexer owner, ushort channelId, ChannelQueueOptions options)
    {
        _owner = owner;
        ChannelId = channelId;
        _queuedBytes = new AsyncByteBudget(options.MaxQueuedBytes);
        _sendGate = new OutboundFlowGate(options);
        _remoteFlowWindow = new RemoteFlowWindow(options);
        _creditPacketThreshold = Math.Min(32, Math.Max(1, options.MaxQueuedPackets / 2));
        _creditByteThreshold = Math.Min(4 * 1024 * 1024, Math.Max(1, options.MaxQueuedBytes / 2));
        _queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(options.MaxQueuedPackets)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    /// <summary>读取一帧数据；通道关闭时返回 null</summary>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _queue.Reader.ReadAsync(ct);
            _queuedBytes.Release(data.Length);
            if (Volatile.Read(ref _flowControlEnabled) != 0)
                ReportConsumed(data.Length);
            return data;
        }
        catch (ChannelClosedException ex) when (ex.InnerException is CommunicationException communicationError)
        {
            throw communicationError;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>向对端发送一帧数据</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _writesCompleted) != 0)
        {
            if (_failure is not null)
                throw _failure;
            throw new InvalidOperationException("通道写方向已完成");
        }
        var flowCreditAcquired = false;
        if (Volatile.Read(ref _flowControlEnabled) != 0)
        {
            await _remoteFlowWindow.AcquireAsync(data.Length, ct);
            flowCreditAcquired = true;
        }
        try
        {
            using var lease = await _sendGate.EnterAsync(data.Length, ct, _owner.ConnectionToken);
            if (Volatile.Read(ref _writesCompleted) != 0)
            {
                if (_failure is not null)
                    throw _failure;
                throw new InvalidOperationException("通道写方向已完成");
            }
            await _owner.SendDataAsync(ChannelId, data, ct);
        }
        catch
        {
            if (flowCreditAcquired)
                _remoteFlowWindow.Release(1, data.Length);
            throw;
        }
    }

    /// <summary>完成本端写方向；仍可读取对端在其 Close 之前发送的数据。</summary>
    public async ValueTask CloseAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _writesCompleted) != 0)
            return;
        OutboundFlowGate.Lease lease;
        try
        {
            lease = await _sendGate.EnterAsync(0, ct, _owner.ConnectionToken);
        }
        catch (CommunicationException) when (Volatile.Read(ref _writesCompleted) != 0)
        {
            return;
        }
        using (lease)
        {
            if (!CompleteWrites())
                return;
            await _owner.SendCloseChannelAsync(ChannelId, ct);
        }
    }

    internal async ValueTask EnqueueAsync(byte[] data, CancellationToken ct = default)
    {
        await _queuedBytes.AcquireAsync(data.Length, ct);
        try
        {
            await _queue.Writer.WriteAsync(data, ct);
        }
        catch
        {
            _queuedBytes.Release(data.Length);
            throw;
        }
    }

    internal bool TryEnqueue(byte[] data)
    {
        if (!_queuedBytes.TryAcquire(data.Length))
            return false;
        if (_queue.Writer.TryWrite(data))
            return true;
        _queuedBytes.Release(data.Length);
        return false;
    }

    internal bool CompleteWrites() => Interlocked.Exchange(ref _writesCompleted, 1) == 0;

    internal void EnableFlowControl() => Volatile.Write(ref _flowControlEnabled, 1);

    internal void AddRemoteCredit(int packets, long bytes)
        => _remoteFlowWindow.Release(packets, bytes);

    internal void CompleteReads()
    {
        if (Interlocked.Exchange(ref _readsCompleted, 1) == 0)
        {
            _queue.Writer.TryComplete();
            _queuedBytes.Close(new CommunicationException(CommunicationErrorCode.Closed, "通道读取方向已关闭"));
        }
    }

    internal void Fail(CommunicationException error)
    {
        _failure = error;
        _sendGate.Close(error);
        _remoteFlowWindow.Close(error);
        Interlocked.Exchange(ref _writesCompleted, 1);
        if (Interlocked.Exchange(ref _readsCompleted, 1) == 0)
        {
            _queue.Writer.TryComplete(error);
            _queuedBytes.Close(error);
        }
    }

    internal bool IsFullyClosed =>
        Volatile.Read(ref _readsCompleted) != 0 && Volatile.Read(ref _writesCompleted) != 0;

    internal void Complete()
    {
        CompleteWrites();
        CompleteReads();
        _sendGate.Close(new CommunicationException(CommunicationErrorCode.Closed, "通道已关闭"));
        _remoteFlowWindow.Close(new CommunicationException(CommunicationErrorCode.Closed, "通道已关闭"));
    }

    private void ReportConsumed(int bytes)
    {
        _consumedPackets++;
        _consumedBytes += bytes;
        if (_consumedPackets < _creditPacketThreshold && _consumedBytes < _creditByteThreshold)
            return;
        var packets = _consumedPackets;
        var consumedBytes = _consumedBytes;
        _consumedPackets = 0;
        _consumedBytes = 0;
        _owner.QueueWindowUpdate(ChannelId, packets, consumedBytes);
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
