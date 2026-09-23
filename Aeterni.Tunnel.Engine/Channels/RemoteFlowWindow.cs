using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>对端已公告的接收窗口；同时按帧数和负载字节实施可靠背压。</summary>
internal sealed class RemoteFlowWindow
{
    private readonly int _maxPackets;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = [];
    private int _availablePackets;
    private long _availableBytes;
    private Exception? _closedError;

    public RemoteFlowWindow(ChannelQueueOptions options)
    {
        _maxPackets = options.MaxQueuedPackets;
        _maxBytes = options.MaxQueuedBytes;
        _availablePackets = _maxPackets;
        _availableBytes = _maxBytes;
    }

    public ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        if (bytes < 0 || bytes > _maxBytes)
            throw new CommunicationException(CommunicationErrorCode.BackpressureLimit,
                $"单帧 {bytes} 字节超过对端接收窗口 {_maxBytes}");

        lock (_gate)
        {
            if (_closedError is not null)
                throw _closedError;
            RemoveCancelledWaiters();
            if (_waiters.Count == 0 && _availablePackets > 0 && _availableBytes >= bytes)
            {
                _availablePackets--;
                _availableBytes -= bytes;
                return ValueTask.CompletedTask;
            }

            var waiter = new Waiter(bytes);
            _waiters.Enqueue(waiter);
            return new ValueTask(WaitAsync(waiter, ct));
        }
    }

    public void Release(int packets, long bytes)
    {
        if (packets <= 0 || bytes < 0)
            return;
        lock (_gate)
        {
            _availablePackets = Math.Min(_maxPackets, _availablePackets + packets);
            _availableBytes = Math.Min(_maxBytes, _availableBytes + bytes);
            DrainWaiters();
        }
    }

    public void Close(Exception error)
    {
        lock (_gate)
        {
            if (_closedError is not null)
                return;
            _closedError = error;
            while (_waiters.TryDequeue(out var waiter))
            {
                if (waiter.State == WaiterState.Pending)
                {
                    waiter.State = WaiterState.Cancelled;
                    waiter.Completion.TrySetException(error);
                }
            }
        }
    }

    private async Task WaitAsync(Waiter waiter, CancellationToken ct)
    {
        using var registration = ct.Register(static state =>
        {
            var (window, pending, token) = ((RemoteFlowWindow, Waiter, CancellationToken))state!;
            window.Cancel(pending, token);
        }, (this, waiter, ct));
        await waiter.Completion.Task;
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (_gate)
        {
            if (waiter.State != WaiterState.Pending)
                return;
            waiter.State = WaiterState.Cancelled;
            waiter.Completion.TrySetCanceled(token);
            DrainWaiters();
        }
    }

    private void DrainWaiters()
    {
        RemoveCancelledWaiters();
        while (_waiters.TryPeek(out var waiter) &&
               _availablePackets > 0 && _availableBytes >= waiter.Bytes)
        {
            _waiters.Dequeue();
            if (waiter.State != WaiterState.Pending)
                continue;
            _availablePackets--;
            _availableBytes -= waiter.Bytes;
            waiter.State = WaiterState.Granted;
            waiter.Completion.TrySetResult();
            RemoveCancelledWaiters();
        }
    }

    private void RemoveCancelledWaiters()
    {
        while (_waiters.TryPeek(out var waiter) && waiter.State == WaiterState.Cancelled)
            _waiters.Dequeue();
    }

    private sealed class Waiter(int bytes)
    {
        public int Bytes { get; } = bytes;
        public WaiterState State { get; set; }
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum WaiterState
    {
        Pending,
        Granted,
        Cancelled,
    }
}
