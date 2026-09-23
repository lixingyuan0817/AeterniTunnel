using System.Collections.Concurrent;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Transport;
using Aeterni.Tunnel.Engine.Wire;
using System.Text;
using System.Buffers.Binary;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>
/// 单连接多路复用器（AD-002）：一条 TCP（或任意 ITunnelConnection）上按 ChannelId 分发帧。
/// - ChannelId=0 为控制通道（Payload 为 JSON 消息，交 ControlHandler）；
/// - 数据通道从 1 起，Data 帧投递到对应 Channel 队列；
/// - 自动应答 Ping（Pong）；Close 帧关闭对应通道。
/// </summary>
public sealed class ChannelMultiplexer : IAsyncDisposable
{
    private readonly ITunnelConnection _connection;
    private readonly ChannelQueueOptions _queueOptions;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ushort, Channel> _channels = new();
    private readonly Dictionary<ushort, List<byte[]>> _pendingChannelData = new();
    private readonly HashSet<ushort> _pendingChannelCloses = [];
    private readonly HashSet<ushort> _rejectedChannels = [];
    private readonly object _channelGate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly OutboundFlowGate _controlWriteGate;
    private readonly System.Threading.Channels.Channel<(ushort ChannelId, byte[] Payload)> _controlQueue =
        System.Threading.Channels.Channel.CreateBounded<(ushort, byte[])>(new System.Threading.Channels.BoundedChannelOptions(64)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private int _nextChannelId;
    private int _pendingChannelFrameCount;
    private long _pendingChannelBytes;
    private bool _readLoopStarted;
    private int _isolateSlowChannels;
    private int _disposed;

    /// <summary>控制帧回调（ChannelId=0，Payload 为 JSON）</summary>
    public Func<ushort, byte[], ValueTask>? ControlHandler { get; set; }

    /// <summary>Ping 回调（可选；默认自动回 Pong）</summary>
    public Func<byte[], ValueTask>? PingHandler { get; set; }

    /// <summary>连接关闭（读循环退出；可用于触发重连）</summary>
    public event Action? ConnectionClosed;

    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _cts.IsCancellationRequested;

    internal CancellationToken ConnectionToken => _cts.Token;

    public ChannelMultiplexer(ITunnelConnection connection, ChannelQueueOptions? queueOptions = null)
    {
        _connection = connection;
        _queueOptions = queueOptions ?? new ChannelQueueOptions();
        _queueOptions.Validate();
        _controlWriteGate = new OutboundFlowGate(_queueOptions);
    }

    /// <summary>
    /// 启用协商后的慢通道隔离：队列超过包数或字节预算时只重置该通道，
    /// 不再让底层连接读循环等待该消费者。
    /// </summary>
    public void EnableSlowChannelIsolation()
    {
        Volatile.Write(ref _isolateSlowChannels, 1);
        lock (_channelGate)
        {
            foreach (var channel in _channels.Values)
                channel.EnableFlowControl();
        }
    }

    /// <summary>启动读循环（幂等）</summary>
    public void Start()
    {
        if (!_readLoopStarted)
        {
            _readLoopStarted = true;
            _ = ControlLoopAsync();
            _ = ReadLoopAsync();
        }
    }

    /// <summary>本端分配一个新通道号并注册</summary>
    public Channel OpenChannel()
    {
        Channel ch;
        lock (_channelGate)
        {
            ushort id;
            do
            {
                id = (ushort)Interlocked.Increment(ref _nextChannelId);
            } while (id == FrameContract.ControlChannel || _channels.ContainsKey(id));
            ch = new Channel(this, id, _queueOptions);
            if (Volatile.Read(ref _isolateSlowChannels) != 0)
                ch.EnableFlowControl();
            _channels[id] = ch;
            _rejectedChannels.Remove(id);
        }
        return ch;
    }

    /// <summary>按对端告知的通道号注册通道（OpenTunnel 控制消息处理时调用）</summary>
    public Channel AcceptChannel(ushort channelId)
    {
        lock (_channelGate)
        {
            if (_channels.TryGetValue(channelId, out var existing))
                return existing;

            var ch = new Channel(this, channelId, _queueOptions);
            if (Volatile.Read(ref _isolateSlowChannels) != 0)
                ch.EnableFlowControl();
            if (_pendingChannelData.Remove(channelId, out var pending))
            {
                _pendingChannelFrameCount -= pending.Count;
                _pendingChannelBytes -= pending.Sum(payload => payload.Length);
                foreach (var payload in pending)
                {
                    if (!ch.TryEnqueue(payload))
                    {
                        var error = QueueLimitError(channelId);
                        ch.Fail(error);
                        _rejectedChannels.Add(channelId);
                        _ = SendResetAsync(channelId, error.Message);
                        break;
                    }
                }
            }
            if (_pendingChannelCloses.Remove(channelId))
                ch.CompleteReads();

            _channels[channelId] = ch;
            return ch;
        }
    }

    /// <summary>发送通道 FIN；调用方负责通过通道发送门保持与先前数据的顺序。</summary>
    internal async ValueTask SendCloseChannelAsync(ushort channelId, CancellationToken ct = default)
    {
        Channel? ch;
        lock (_channelGate)
            _channels.TryGetValue(channelId, out ch);
        if (ch is not null)
        {
            await WriteFrameCoreAsync(new Frame(FrameType.Close, channelId, []), ct);
            if (ch.IsFullyClosed)
            {
                lock (_channelGate)
                    _channels.TryRemove(channelId, out _);
            }
        }
    }

    internal async ValueTask SendDataAsync(ushort channelId, ReadOnlyMemory<byte> data, CancellationToken ct)
        => await WriteFrameCoreAsync(FrameType.Data, channelId, data, ct);

    /// <summary>发送控制帧（ChannelId=0，Payload 为 JSON 消息）</summary>
    public async ValueTask SendControlAsync(byte[] payload, CancellationToken ct = default)
    {
        using var lease = await _controlWriteGate.EnterAsync(payload.Length, ct, _cts.Token);
        await WriteFrameCoreAsync(new Frame(FrameType.Control, FrameContract.ControlChannel, payload), ct);
    }

    private async ValueTask WriteFrameCoreAsync(Frame frame, CancellationToken ct)
        => await WriteFrameCoreAsync(frame.Type, frame.ChannelId, frame.Payload, ct);

    private async ValueTask WriteFrameCoreAsync(FrameType type, ushort channelId,
        ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new CommunicationException(CommunicationErrorCode.Closed, "连接已关闭");
        CancellationTokenSource? linked = null;
        var lockTaken = false;
        try
        {
            if (!_writeLock.Wait(0))
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                await _writeLock.WaitAsync(linked.Token);
            }
            lockTaken = true;
            if (linked is null && ct.CanBeCanceled)
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            await FrameCodec.WriteAsync(_connection.Stream, type, channelId, payload,
                linked?.Token ?? _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            _ = _connection.DisposeAsync();
            throw new CommunicationException(CommunicationErrorCode.TransportFailure,
                "帧写入失败", ex);
        }
        finally
        {
            if (lockTaken)
                _writeLock.Release();
            linked?.Dispose();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(_connection.Stream, _cts.Token);

                switch (frame.Type)
                {
                    case FrameType.Control when frame.ChannelId == FrameContract.ControlChannel:
                        await _controlQueue.Writer.WriteAsync((frame.ChannelId, frame.Payload), _cts.Token);
                        break;

                    case FrameType.Data:
                        Channel? ch;
                        var reject = false;
                        lock (_channelGate)
                        {
                            if (_rejectedChannels.Contains(frame.ChannelId))
                                break;
                            if (!_channels.TryGetValue(frame.ChannelId, out ch))
                            {
                                if (!_pendingChannelCloses.Contains(frame.ChannelId) &&
                                    _pendingChannelFrameCount < _queueOptions.MaxQueuedPackets &&
                                    _pendingChannelBytes + frame.Payload.Length <= _queueOptions.MaxQueuedBytes)
                                {
                                    if (!_pendingChannelData.TryGetValue(frame.ChannelId, out var pending))
                                    {
                                        pending = [];
                                        _pendingChannelData[frame.ChannelId] = pending;
                                    }
                                    pending.Add(frame.Payload);
                                    _pendingChannelFrameCount++;
                                    _pendingChannelBytes += frame.Payload.Length;
                                }
                                else if (Volatile.Read(ref _isolateSlowChannels) != 0)
                                    reject = _rejectedChannels.Add(frame.ChannelId);
                            }
                        }
                        if (ch is not null)
                        {
                            if (Volatile.Read(ref _isolateSlowChannels) == 0)
                                await ch.EnqueueAsync(frame.Payload, _cts.Token);
                            else if (!ch.TryEnqueue(frame.Payload))
                                reject = true;
                        }
                        if (reject)
                            await RejectChannelAsync(frame.ChannelId);
                        break;

                    case FrameType.Ping:
                        using (var lease = await _controlWriteGate.EnterAsync(
                                   frame.Payload.Length, _cts.Token, _cts.Token))
                            await WriteFrameCoreAsync(new Frame(FrameType.Pong, frame.ChannelId, frame.Payload), _cts.Token);
                        break;

                    case FrameType.Pong:
                        break;

                    case FrameType.Close:
                        Channel? closed;
                        lock (_channelGate)
                        {
                            _channels.TryGetValue(frame.ChannelId, out closed);
                            if (closed is null && _pendingChannelData.ContainsKey(frame.ChannelId))
                                _pendingChannelCloses.Add(frame.ChannelId);
                        }
                        if (closed is not null)
                        {
                            closed.CompleteReads();
                            if (closed.IsFullyClosed)
                            {
                                lock (_channelGate)
                                    _channels.TryRemove(frame.ChannelId, out _);
                            }
                        }
                        break;

                    case FrameType.Reset:
                        Channel? reset;
                        lock (_channelGate)
                        {
                            _channels.TryRemove(frame.ChannelId, out reset);
                            _rejectedChannels.Add(frame.ChannelId);
                            RemovePendingChannelData(frame.ChannelId);
                        }
                        reset?.Fail(new CommunicationException(CommunicationErrorCode.BackpressureLimit,
                            frame.Payload.Length == 0 ? "对端重置通道" : Encoding.UTF8.GetString(frame.Payload)));
                        break;

                    case FrameType.WindowUpdate:
                        if (frame.Payload.Length != 12)
                            throw new ProtocolException("通道窗口更新长度必须为 12 字节");
                        var packets = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4));
                        var bytes = BinaryPrimitives.ReadInt64BigEndian(frame.Payload.AsSpan(4, 8));
                        if (packets <= 0 || bytes < 0)
                            throw new ProtocolException("通道窗口更新数值非法");
                        Channel? credited;
                        lock (_channelGate)
                            _channels.TryGetValue(frame.ChannelId, out credited);
                        credited?.AddRemoteCredit(packets, bytes);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch
        {
            // 连接断开：关闭全部通道
        }
        finally
        {
            _controlQueue.Writer.TryComplete();
            CloseAllChannels();
            ConnectionClosed?.Invoke();
        }
    }

    private async Task ControlLoopAsync()
    {
        try
        {
            await foreach (var item in _controlQueue.Reader.ReadAllAsync(_cts.Token))
            {
                if (ControlHandler is not null)
                    await ControlHandler(item.ChannelId, item.Payload);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            _cts.Cancel();
        }
    }

    private void CloseAllChannels()
    {
        lock (_channelGate)
        {
            foreach (var ch in _channels.Values)
                ch.Complete();
            _channels.Clear();
            _pendingChannelData.Clear();
            _pendingChannelCloses.Clear();
            _rejectedChannels.Clear();
            _pendingChannelFrameCount = 0;
            _pendingChannelBytes = 0;
        }
    }

    private async ValueTask RejectChannelAsync(ushort channelId)
    {
        Channel? channel;
        var error = QueueLimitError(channelId);
        lock (_channelGate)
        {
            _channels.TryRemove(channelId, out channel);
            _rejectedChannels.Add(channelId);
            RemovePendingChannelData(channelId);
        }
        channel?.Fail(error);
        await SendResetAsync(channelId, error.Message);
    }

    private ValueTask SendResetAsync(ushort channelId, string reason)
        => SendPriorityFrameAsync(new Frame(FrameType.Reset, channelId, Encoding.UTF8.GetBytes(reason)));

    private async ValueTask SendPriorityFrameAsync(Frame frame)
    {
        using var lease = await _controlWriteGate.EnterAsync(frame.Payload.Length, _cts.Token, _cts.Token);
        await WriteFrameCoreAsync(frame, _cts.Token);
    }

    internal void QueueWindowUpdate(ushort channelId, int packets, long bytes)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), packets);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(4, 8), bytes);
        _ = SendWindowUpdateCoreAsync(new Frame(FrameType.WindowUpdate, channelId, payload));
    }

    private async Task SendWindowUpdateCoreAsync(Frame frame)
    {
        try
        {
            await SendPriorityFrameAsync(frame);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (CommunicationException) { }
    }

    private CommunicationException QueueLimitError(ushort channelId)
        => new(CommunicationErrorCode.BackpressureLimit,
            $"通道 {channelId} 的接收队列超过 {_queueOptions.MaxQueuedPackets} 包或 {_queueOptions.MaxQueuedBytes} 字节限制");

    private void RemovePendingChannelData(ushort channelId)
    {
        if (!_pendingChannelData.Remove(channelId, out var pending))
            return;
        _pendingChannelFrameCount -= pending.Count;
        _pendingChannelBytes -= pending.Sum(payload => payload.Length);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        _controlWriteGate.Close(new CommunicationException(CommunicationErrorCode.Closed, "连接已关闭"));
        _controlQueue.Writer.TryComplete();
        CloseAllChannels();
        await _connection.DisposeAsync();
    }
}
