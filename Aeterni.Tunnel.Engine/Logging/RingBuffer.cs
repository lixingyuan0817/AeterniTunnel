using System.Collections.Concurrent;

namespace Aeterni.Tunnel.Engine.Logging;

/// <summary>
/// 线程安全环形缓冲：容量固定，超限丢最旧。
/// </summary>
public class RingBuffer<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly int _capacity;

    public RingBuffer(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _queue.Count;

    public void Add(T item)
    {
        _queue.Enqueue(item);
        while (_queue.Count > _capacity && _queue.TryDequeue(out _)) { }
    }

    public IReadOnlyList<T> Snapshot() => _queue.ToArray();
}
