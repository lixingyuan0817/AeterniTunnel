using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>FIFO 异步字节预算；取消等待不会消耗额度。</summary>
internal sealed class AsyncByteBudget
{
    private readonly long _limit;
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiters = [];
    private long _used;
    private Exception? _closedError;

    public AsyncByteBudget(long limit) => _limit = limit;

    public bool TryAcquire(int bytes)
    {
        ValidateSize(bytes);
        lock (_gate)
        {
            RemoveCancelledWaiters();
            if (_closedError is not null || _waiters.Count != 0 || _used + bytes > _limit)
                return false;
            _used += bytes;
            return true;
        }
    }

    public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        ValidateSize(bytes);
        Waiter? waiter = null;
        lock (_gate)
        {
            if (_closedError is not null)
                throw _closedError;
            RemoveCancelledWaiters();
            if (_waiters.Count == 0 && _used + bytes <= _limit)
            {
                _used += bytes;
                return;
            }

            waiter = new Waiter(bytes);
            _waiters.Enqueue(waiter);
        }

        using var registration = ct.Register(static state =>
        {
            var (budget, pending, token) = ((AsyncByteBudget, Waiter, CancellationToken))state!;
            budget.Cancel(pending, token);
        }, (this, waiter, ct));
        await waiter.Completion.Task;
    }

    public void Release(int bytes)
    {
        lock (_gate)
        {
            _used -= bytes;
            if (_used < 0)
                throw new InvalidOperationException("字节预算释放超过已占用额度");
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
        while (_waiters.TryPeek(out var waiter) && _used + waiter.Bytes <= _limit)
        {
            _waiters.Dequeue();
            if (waiter.State != WaiterState.Pending)
                continue;
            _used += waiter.Bytes;
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

    private void ValidateSize(int bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes > _limit)
            throw new CommunicationException(CommunicationErrorCode.BackpressureLimit,
                $"单帧 {bytes} 字节超过队列字节预算 {_limit}");
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
