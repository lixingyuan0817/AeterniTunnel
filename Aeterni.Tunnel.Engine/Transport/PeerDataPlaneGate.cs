namespace Aeterni.Tunnel.Engine.Transport;

public enum PeerDataPlaneState
{
    AwaitingAuthentication = 0,
    Authenticated = 1,
    Closed = 2,
}

/// <summary>
/// Delivery gate shared by a native peer transport adapter and the Engine host.
/// The adapter must perform its standard encrypted handshake first, then present
/// the control-plane lease. No business payload is delivered before that succeeds.
/// This type intentionally does not implement cryptography or parse WebRTC frames.
/// </summary>
public sealed class PeerDataPlaneGate
{
    private readonly object _gate = new();
    private readonly PeerAuthorizationLease _lease;
    private PeerDataPlaneState _state = PeerDataPlaneState.AwaitingAuthentication;

    public PeerDataPlaneGate(PeerAuthorizationLease lease)
        => _lease = lease ?? throw new ArgumentNullException(nameof(lease));

    public PeerDataPlaneState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>
    /// Accepts exactly one successful lease presentation for this data-plane
    /// connection. A repeated presentation is rejected instead of extending a
    /// lease or reopening a closed connection.
    /// </summary>
    public bool Authenticate(
        string localPeerId,
        string remotePeerId,
        string serviceId,
        string token,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state != PeerDataPlaneState.AwaitingAuthentication ||
                !_lease.Validate(localPeerId, remotePeerId, serviceId, token, now))
            {
                return false;
            }

            _state = PeerDataPlaneState.Authenticated;
            return true;
        }
    }

    /// <summary>
    /// Delivers an already decrypted business payload only after authentication.
    /// The callback is invoked synchronously to make ownership explicit: the gate
    /// never retains the caller's buffer.
    /// </summary>
    public bool TryDeliver(ReadOnlyMemory<byte> payload, Action<ReadOnlyMemory<byte>> deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);

        lock (_gate)
        {
            if (_state != PeerDataPlaneState.Authenticated)
                return false;

            deliver(payload);
            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
            _state = PeerDataPlaneState.Closed;
    }
}
