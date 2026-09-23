using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>
/// 单逻辑通道的发送预算和顺序门。只有一个该通道的写入会竞争连接级写锁，
/// 因而高并发写入不能为同一通道预占全部连接调度位置。
/// </summary>
internal sealed class OutboundFlowGate
{
    private readonly SemaphoreSlim _packetSlots;
    private readonly SemaphoreSlim _order = new(1, 1);
    private readonly AsyncByteBudget _bytes;
    private readonly CancellationTokenSource _closed = new();
    private Exception? _closedError;

    public OutboundFlowGate(ChannelQueueOptions options)
    {
        _packetSlots = new SemaphoreSlim(options.MaxQueuedPackets, options.MaxQueuedPackets);
        _bytes = new AsyncByteBudget(options.MaxQueuedBytes);
    }

    public ValueTask<Lease> EnterAsync(int bytes, CancellationToken callerToken,
        CancellationToken connectionToken)
    {
        ThrowIfClosed();
        if (_packetSlots.Wait(0))
        {
            if (_bytes.TryAcquire(bytes))
            {
                if (_order.Wait(0))
                {
                    try
                    {
                        ThrowIfClosed();
                        return ValueTask.FromResult(new Lease(this, bytes));
                    }
                    catch
                    {
                        _order.Release();
                        _bytes.Release(bytes);
                        _packetSlots.Release();
                        throw;
                    }
                }
                _bytes.Release(bytes);
            }
            _packetSlots.Release();
        }
        return EnterSlowAsync(bytes, callerToken, connectionToken);
    }

    public void Close(Exception error)
    {
        if (Interlocked.CompareExchange(ref _closedError, error, null) is not null)
            return;
        _bytes.Close(error);
        _closed.Cancel();
    }

    private async ValueTask<Lease> EnterSlowAsync(int bytes, CancellationToken callerToken,
        CancellationToken connectionToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken, connectionToken, _closed.Token);
        var packetAcquired = false;
        var bytesAcquired = false;
        var orderAcquired = false;
        try
        {
            await _packetSlots.WaitAsync(linked.Token);
            packetAcquired = true;
            await _bytes.AcquireAsync(bytes, linked.Token);
            bytesAcquired = true;
            await _order.WaitAsync(linked.Token);
            orderAcquired = true;
            ThrowIfClosed();
            return new Lease(this, bytes);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            if (orderAcquired)
                _order.Release();
            if (bytesAcquired)
                _bytes.Release(bytes);
            if (packetAcquired)
                _packetSlots.Release();
            throw _closedError ?? new CommunicationException(
                CommunicationErrorCode.Closed, "发送队列已关闭");
        }
        catch
        {
            if (orderAcquired)
                _order.Release();
            if (bytesAcquired)
                _bytes.Release(bytes);
            if (packetAcquired)
                _packetSlots.Release();
            throw;
        }
    }

    private void Exit(int bytes)
    {
        _order.Release();
        _bytes.Release(bytes);
        _packetSlots.Release();
    }

    private void ThrowIfClosed()
    {
        if (_closedError is not null)
            throw _closedError;
    }

    public readonly struct Lease : IDisposable
    {
        private readonly OutboundFlowGate? _owner;
        private readonly int _bytes;

        internal Lease(OutboundFlowGate owner, int bytes)
        {
            _owner = owner;
            _bytes = bytes;
        }

        public void Dispose() => _owner?.Exit(_bytes);
    }
}
