using System.Net;

namespace Aeterni.Tunnel.Engine.Server;

internal sealed class UdpSourceRegistry
{
    private readonly Dictionary<uint, SourceEntry> _sources = new();
    private readonly object _gate = new();
    private readonly int _maxSources;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _timeProvider;
    private uint _nextSourceId;

    public UdpSourceRegistry(int maxSources, TimeSpan idleTimeout, TimeProvider? timeProvider = null)
    {
        _maxSources = Math.Max(1, maxSources);
        _idleTimeout = idleTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                PruneExpired(_timeProvider.GetUtcNow());
                return _sources.Count;
            }
        }
    }

    public uint GetOrCreate(IPEndPoint endpoint)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            PruneExpired(now);

            var existing = _sources.FirstOrDefault(pair => pair.Value.EndPoint.Equals(endpoint));
            if (existing.Value is not null)
            {
                _sources[existing.Key] = existing.Value with { LastSeenUtc = now };
                return existing.Key;
            }

            if (_sources.Count >= _maxSources)
            {
                var oldest = _sources.MinBy(pair => pair.Value.LastSeenUtc);
                _sources.Remove(oldest.Key);
            }

            var id = NextAvailableId();
            _sources[id] = new SourceEntry(Clone(endpoint), now);
            return id;
        }
    }

    public bool TryGet(uint sourceId, out IPEndPoint? endpoint)
    {
        lock (_gate)
        {
            PruneExpired(_timeProvider.GetUtcNow());
            if (_sources.TryGetValue(sourceId, out var entry))
            {
                endpoint = Clone(entry.EndPoint);
                return true;
            }

            endpoint = null;
            return false;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _sources.Clear();
    }

    private uint NextAvailableId()
    {
        do
        {
            _nextSourceId = unchecked(_nextSourceId + 1);
        }
        while (_nextSourceId == 0 || _sources.ContainsKey(_nextSourceId));

        return _nextSourceId;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var expiry = now - _idleTimeout;
        foreach (var sourceId in _sources
                     .Where(pair => pair.Value.LastSeenUtc <= expiry)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _sources.Remove(sourceId);
        }
    }

    private static IPEndPoint Clone(IPEndPoint endpoint) => new(endpoint.Address, endpoint.Port);

    private sealed record SourceEntry(IPEndPoint EndPoint, DateTimeOffset LastSeenUtc);
}
