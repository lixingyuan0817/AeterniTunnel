using System.Security.Cryptography;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>同入口附加数据连接的一次性短期绑定凭证注册表。</summary>
internal sealed class DataConnectionBindingRegistry
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<ServerSession, string> _outstandingBySession = new();

    public DataConnectionBindingRegistry(TimeSpan? lifetime = null, TimeProvider? timeProvider = null)
    {
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DataConnectionBinding Issue(ServerSession session)
    {
        lock (_gate)
        {
            if (_outstandingBySession.Remove(session, out var previous))
                _entries.Remove(previous);

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expiresAt = _timeProvider.GetUtcNow().Add(_lifetime);
            _entries[token] = new Entry(session, expiresAt);
            _outstandingBySession[session] = token;
            return new DataConnectionBinding(token, expiresAt);
        }
    }

    public bool TryConsume(string token, out ServerSession? session, out string error)
    {
        session = null;
        error = "附加数据连接凭证无效或已使用";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        lock (_gate)
        {
            if (!_entries.Remove(token, out var entry))
                return false;
            _outstandingBySession.Remove(entry.Session);
            if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                error = "附加数据连接凭证已过期";
                return false;
            }
            session = entry.Session;
            error = "";
            return true;
        }
    }

    public void Revoke(ServerSession session)
    {
        lock (_gate)
        {
            if (_outstandingBySession.Remove(session, out var token))
                _entries.Remove(token);
        }
    }

    private sealed record Entry(ServerSession Session, DateTimeOffset ExpiresAt);
}

internal readonly record struct DataConnectionBinding(string Token, DateTimeOffset ExpiresAt);
