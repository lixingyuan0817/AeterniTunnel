namespace Aeterni.Tunnel.Engine.Transport;

public readonly record struct PeerQualitySnapshot(
    DateTimeOffset At,
    TimeSpan? RoundTripTime,
    long SentBytes,
    long ReceivedBytes,
    long LostPackets);

/// <summary>
/// Host-facing Peer lifecycle implemented independently from a concrete native
/// transport. The adapter drives state transitions after it has evidence from the
/// transport; this class never claims that a connection exists by itself.
/// </summary>
public sealed class PeerSessionLifecycle : IPeerSession
{
    private readonly object _gate = new();
    private readonly PeerAuthorizationLease? _lease;
    private CommunicationSessionState _state = CommunicationSessionState.Created;
    private PeerQualitySnapshot _quality;
    private int _disposed;

    public PeerSessionLifecycle(
        string localPeerId,
        string remotePeerId,
        TransportCapabilities capabilities = TransportCapabilities.PeerSession,
        PeerAuthorizationLease? lease = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePeerId);
        LocalPeerId = localPeerId;
        RemotePeerId = remotePeerId;
        Capabilities = capabilities;
        _lease = lease;
    }

    public string LocalPeerId { get; }

    public string RemotePeerId { get; }

    public CommunicationSessionState State
    {
        get { lock (_gate) return _state; }
    }

    public TransportCapabilities Capabilities { get; }

    public PeerQualitySnapshot Quality
    {
        get { lock (_gate) return _quality; }
    }

    public event Action<CommunicationSessionState>? StateChanged;

    public event Action<PeerQualitySnapshot>? QualityChanged;

    /// <summary>
    /// Checks the control-plane lease at an adapter-defined cadence. Expiration or
    /// revocation is terminal; the caller must not silently reconnect with the old
    /// credential.
    /// </summary>
    public bool EnforceLease(DateTimeOffset now)
    {
        if (_lease is null)
            return State is not (CommunicationSessionState.Closing or CommunicationSessionState.Closed);

        if (_lease.IsRevoked || now >= _lease.ExpiresAt)
        {
            _ = CloseAsync(_lease.IsRevoked ? "peer lease revoked" : "peer lease expired");
            return false;
        }

        return State is not (CommunicationSessionState.Closing or CommunicationSessionState.Closed);
    }

    public void BeginConnect() => Transition(CommunicationSessionState.Connecting);

    public void MarkConnected() => Transition(CommunicationSessionState.Connected);

    public void MarkDegraded() => Transition(CommunicationSessionState.Degraded);

    public void MarkFailed() => Transition(CommunicationSessionState.Failed);

    public void UpdateQuality(PeerQualitySnapshot quality)
    {
        lock (_gate)
        {
            if (_state is CommunicationSessionState.Closed or CommunicationSessionState.Closing)
                return;
            _quality = quality;
        }

        QualityChanged?.Invoke(quality);
    }

    public ValueTask CloseAsync(string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ct.ThrowIfCancellationRequested();
        Transition(CommunicationSessionState.Closing);
        Transition(CommunicationSessionState.Closed);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            CloseAsync("disposed").AsTask().GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    private void Transition(CommunicationSessionState next)
    {
        Action<CommunicationSessionState>? changed = null;
        lock (_gate)
        {
            if (_state == next || _state == CommunicationSessionState.Closed)
                return;
            if (!IsAllowed(_state, next))
                throw new InvalidOperationException($"Peer session cannot transition from {_state} to {next}.");
            _state = next;
            changed = StateChanged;
        }

        changed?.Invoke(next);
    }

    private static bool IsAllowed(CommunicationSessionState from, CommunicationSessionState to)
        => (from, to) switch
        {
            (CommunicationSessionState.Created, CommunicationSessionState.Connecting) => true,
            (CommunicationSessionState.Created, CommunicationSessionState.Closing) => true,
            (CommunicationSessionState.Connecting, CommunicationSessionState.Connected) => true,
            (CommunicationSessionState.Connecting, CommunicationSessionState.Degraded) => true,
            (CommunicationSessionState.Connecting, CommunicationSessionState.Failed) => true,
            (CommunicationSessionState.Connecting, CommunicationSessionState.Closing) => true,
            (CommunicationSessionState.Connected, CommunicationSessionState.Degraded) => true,
            (CommunicationSessionState.Connected, CommunicationSessionState.Closing) => true,
            (CommunicationSessionState.Degraded, CommunicationSessionState.Connected) => true,
            (CommunicationSessionState.Degraded, CommunicationSessionState.Failed) => true,
            (CommunicationSessionState.Degraded, CommunicationSessionState.Closing) => true,
            (CommunicationSessionState.Failed, CommunicationSessionState.Closing) => true,
            (CommunicationSessionState.Closing, CommunicationSessionState.Closed) => true,
            _ => false,
        };
}
