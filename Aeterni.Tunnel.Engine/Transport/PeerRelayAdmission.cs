namespace Aeterni.Tunnel.Engine.Transport;

public sealed record PeerRelayLimits
{
    public int MaxPendingPackets { get; init; } = 128;

    public long MaxPendingBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxPacketBytes { get; init; } = 256 * 1024;

    internal void Validate()
    {
        if (MaxPendingPackets <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingPackets));
        if (MaxPendingBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingBytes));
        if (MaxPacketBytes <= 0 || MaxPacketBytes > MaxPendingBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxPacketBytes));
    }
}

/// <summary>
/// Admission and bounded-resource gate for a future end-to-end encrypted relay.
/// It authenticates the control-plane lease but never decrypts or interprets the
/// payload. Callers must pass ciphertext produced by the selected standard secure
/// peer transport; plaintext is intentionally not represented by this API.
/// </summary>
public sealed class PeerRelayAdmission
{
    private readonly object _gate = new();
    private readonly PeerAuthorizationLease _lease;
    private readonly PeerRelayLimits _limits;
    private bool _authenticated;
    private bool _closed;
    private int _pendingPackets;
    private long _pendingBytes;

    public PeerRelayAdmission(PeerAuthorizationLease lease, PeerRelayLimits? limits = null)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _limits = limits ?? new PeerRelayLimits();
        _limits.Validate();
    }

    public bool IsAuthenticated
    {
        get { lock (_gate) return _authenticated && !_closed; }
    }

    public (int Packets, long Bytes) Pending
    {
        get { lock (_gate) return (_pendingPackets, _pendingBytes); }
    }

    public bool Authenticate(
        string localPeerId,
        string remotePeerId,
        string serviceId,
        string token,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_closed || _authenticated ||
                !_lease.Validate(localPeerId, remotePeerId, serviceId, token, now))
            {
                return false;
            }

            _authenticated = true;
            return true;
        }
    }

    public bool TryReserve(int bytes, out PeerRelayReservation? reservation)
    {
        lock (_gate)
        {
            if (!_authenticated || _closed || bytes <= 0 || bytes > _limits.MaxPacketBytes ||
                _pendingPackets >= _limits.MaxPendingPackets ||
                _pendingBytes > _limits.MaxPendingBytes - bytes)
            {
                reservation = null;
                return false;
            }

            _pendingPackets++;
            _pendingBytes += bytes;
            reservation = new PeerRelayReservation(this, bytes);
            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
            _closed = true;
    }

    internal void Release(int bytes)
    {
        lock (_gate)
        {
            if (_pendingPackets > 0)
                _pendingPackets--;
            _pendingBytes = Math.Max(0, _pendingBytes - bytes);
        }
    }
}

public sealed class PeerRelayReservation : IDisposable
{
    private readonly PeerRelayAdmission _admission;
    private readonly int _bytes;
    private int _released;

    internal PeerRelayReservation(PeerRelayAdmission admission, int bytes)
    {
        _admission = admission;
        _bytes = bytes;
    }

    public int Bytes => _bytes;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _admission.Release(_bytes);
    }
}
