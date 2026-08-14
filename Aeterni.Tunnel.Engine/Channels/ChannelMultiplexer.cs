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
    private readonly ITunnelConnection _connection;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ushort, Channel> _channels = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextChannelId;
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
            _ = ReadLoopAsync();
        }
    }

    /// <summary>本端分配一个新通道号并注册</summary>
    public Channel OpenChannel()
    {
        var id = (ushort)Interlocked.Increment(ref _nextChannelId);
        var ch = new Channel(this, id);
        _channels[id] = ch;
        return ch;
    }

    /// <summary>按对端告知的通道号注册通道（OpenTunnel 控制消息处理时调用）</summary>
    public Channel AcceptChannel(ushort channelId)
    {
        var ch = new Channel(this, channelId);
        _channels[channelId] = ch;
        return ch;
    }

    /// <summary>关闭通道：发 Close 帧 + 释放本地队列</summary>
    public async ValueTask CloseChannelAsync(ushort channelId, CancellationToken ct = default)
    {
        if (_channels.TryRemove(channelId, out var ch))
        {
            ch.Complete();
            await WriteFrameAsync(new Frame(FrameType.Close, channelId, []), ct);
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
                        if (ControlHandler is not null)
                            await ControlHandler(frame.ChannelId, frame.Payload);
                        break;

                    case FrameType.Data:
                        if (_channels.TryGetValue(frame.ChannelId, out var ch))
                            await ch.EnqueueAsync(frame.Payload);
                        break;

                    case FrameType.Ping:
                        await WriteFrameAsync(new Frame(FrameType.Pong, frame.ChannelId, frame.Payload), default);
                        break;

                    case FrameType.Pong:
                        break;

                    case FrameType.Close:
                        if (_channels.TryRemove(frame.ChannelId, out var closed))
                            closed.Complete();
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
            CloseAllChannels();
            ConnectionClosed?.Invoke();
        }
    }

    private void CloseAllChannels()
    {
        foreach (var ch in _channels.Values)
            ch.Complete();
        _channels.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        CloseAllChannels();
        _writeLock.Dispose();
        await _connection.DisposeAsync();
    }
}
