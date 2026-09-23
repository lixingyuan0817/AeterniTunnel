using System.Collections.Concurrent;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Transport;
using Aeterni.Tunnel.Engine.Wire;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>
/// 单连接多路复用器（AD-002）：一条 TCP（或任意 ITunnelConnection）上按 ChannelId 分发帧。
/// - ChannelId=0 为控制通道（Payload 为 JSON 消息，交 ControlHandler）；
/// - 数据通道从 1 起，Data 帧投递到对应 Channel 队列；
/// - 自动应答 Ping（Pong）；Close 帧关闭对应通道。
/// </summary>
public sealed class ChannelMultiplexer : IAsyncDisposable
{
    private const int MaxPendingChannelFrames = 64;
    private readonly ITunnelConnection _connection;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ushort, Channel> _channels = new();
    private readonly Dictionary<ushort, List<byte[]>> _pendingChannelData = new();
    private readonly HashSet<ushort> _pendingChannelCloses = [];
    private readonly object _channelGate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly System.Threading.Channels.Channel<(ushort ChannelId, byte[] Payload)> _controlQueue =
        System.Threading.Channels.Channel.CreateBounded<(ushort, byte[])>(new System.Threading.Channels.BoundedChannelOptions(64)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private int _nextChannelId;
    private int _pendingChannelFrameCount;
    private bool _readLoopStarted;
    private int _disposed;

    /// <summary>控制帧回调（ChannelId=0，Payload 为 JSON）</summary>
    public Func<ushort, byte[], ValueTask>? ControlHandler { get; set; }

    /// <summary>Ping 回调（可选；默认自动回 Pong）</summary>
    public Func<byte[], ValueTask>? PingHandler { get; set; }

    /// <summary>连接关闭（读循环退出；可用于触发重连）</summary>
    public event Action? ConnectionClosed;

    public ChannelMultiplexer(ITunnelConnection connection)
    {
        _connection = connection;
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
        var id = (ushort)Interlocked.Increment(ref _nextChannelId);
        var ch = new Channel(this, id);
        lock (_channelGate)
            _channels[id] = ch;
        return ch;
    }

    /// <summary>按对端告知的通道号注册通道（OpenTunnel 控制消息处理时调用）</summary>
    public Channel AcceptChannel(ushort channelId)
    {
        lock (_channelGate)
        {
            if (_channels.TryGetValue(channelId, out var existing))
                return existing;

            var ch = new Channel(this, channelId);
            if (_pendingChannelData.Remove(channelId, out var pending))
            {
                foreach (var payload in pending)
                    ch.TryEnqueue(payload);
                _pendingChannelFrameCount -= pending.Count;
            }
            if (_pendingChannelCloses.Remove(channelId))
                ch.CompleteReads();

            _channels[channelId] = ch;
            return ch;
        }
    }

    /// <summary>关闭通道：发 Close 帧 + 释放本地队列</summary>
    public async ValueTask CloseChannelAsync(ushort channelId, CancellationToken ct = default)
    {
        Channel? ch;
        lock (_channelGate)
            _channels.TryGetValue(channelId, out ch);
        if (ch is not null && ch.CompleteWrites())
        {
            await WriteFrameAsync(new Frame(FrameType.Close, channelId, []), ct);
            if (ch.IsFullyClosed)
            {
                lock (_channelGate)
                    _channels.TryRemove(channelId, out _);
            }
        }
    }

    internal async ValueTask SendDataAsync(ushort channelId, ReadOnlyMemory<byte> data, CancellationToken ct)
        => await WriteFrameAsync(Frame.Data(channelId, data.ToArray()), ct);

    /// <summary>发送控制帧（ChannelId=0，Payload 为 JSON 消息）</summary>
    public async ValueTask SendControlAsync(byte[] payload, CancellationToken ct = default)
        => await WriteFrameAsync(new Frame(FrameType.Control, FrameContract.ControlChannel, payload), ct);

    private async ValueTask WriteFrameAsync(Frame frame, CancellationToken ct)
    {
        // 已释放后忽略写入，避免后台任务访问已释放的写锁
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            await _writeLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await FrameCodec.WriteAsync(_connection.Stream, frame, ct);
        }
        finally
        {
            _writeLock.Release();
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
                        lock (_channelGate)
                        {
                            if (!_channels.TryGetValue(frame.ChannelId, out ch))
                            {
                                if (!_pendingChannelCloses.Contains(frame.ChannelId) &&
                                    _pendingChannelFrameCount < MaxPendingChannelFrames)
                                {
                                    if (!_pendingChannelData.TryGetValue(frame.ChannelId, out var pending))
                                    {
                                        pending = [];
                                        _pendingChannelData[frame.ChannelId] = pending;
                                    }
                                    pending.Add(frame.Payload);
                                    _pendingChannelFrameCount++;
                                }
                            }
                        }
                        if (ch is not null)
                            await ch.EnqueueAsync(frame.Payload);
                        break;

                    case FrameType.Ping:
                        await WriteFrameAsync(new Frame(FrameType.Pong, frame.ChannelId, frame.Payload), default);
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
            _pendingChannelFrameCount = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        _controlQueue.Writer.TryComplete();
        CloseAllChannels();
        _writeLock.Dispose();
        await _connection.DisposeAsync();
    }
}
