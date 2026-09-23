namespace Aeterni.Tunnel.Engine.Transport;

public enum PeerRecoveryReason
{
    NetworkChanged = 0,
    TransportFailure = 1,
    AtsControlDisconnected = 2,
    LeaseExpired = 3,
    LeaseRevoked = 4,
    ExplicitClose = 5,
}

public enum PeerRecoveryAction
{
    RetryPeer = 0,
    AwaitControl = 1,
    ClosePeer = 2,
}

public readonly record struct PeerRecoveryDecision(
    PeerRecoveryAction Action,
    TimeSpan Delay,
    bool RequiresControlReauthorization);

/// <summary>
/// Bounded, deterministic recovery policy for a Peer session. ATS control loss
/// is deliberately separate from Peer transport loss: the peer is not reported
/// as closed merely because signaling/control is temporarily unavailable.
/// </summary>
public sealed class PeerRecoveryPolicy
{
    public PeerRecoveryPolicy(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null)
    {
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(250);
        MaximumDelay = maximumDelay ?? TimeSpan.FromSeconds(5);
        if (InitialDelay <= TimeSpan.Zero || MaximumDelay < InitialDelay)
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public PeerRecoveryDecision Decide(PeerRecoveryReason reason, int attempt)
    {
        if (attempt < 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));

        return reason switch
        {
            PeerRecoveryReason.AtsControlDisconnected =>
                new(PeerRecoveryAction.AwaitControl, TimeSpan.Zero, true),
            PeerRecoveryReason.LeaseExpired or
            PeerRecoveryReason.LeaseRevoked or
            PeerRecoveryReason.ExplicitClose =>
                new(PeerRecoveryAction.ClosePeer, TimeSpan.Zero, false),
            PeerRecoveryReason.NetworkChanged or
            PeerRecoveryReason.TransportFailure when attempt < MaxAttempts =>
                new(PeerRecoveryAction.RetryPeer, CalculateDelay(attempt), true),
            PeerRecoveryReason.NetworkChanged or
            PeerRecoveryReason.TransportFailure =>
                new(PeerRecoveryAction.ClosePeer, TimeSpan.Zero, false),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown recovery reason."),
        };
    }

    private TimeSpan CalculateDelay(int attempt)
    {
        var delay = InitialDelay;
        for (var i = 0; i < attempt && delay < MaximumDelay; i++)
        {
            var nextTicks = delay.Ticks > MaximumDelay.Ticks / 2
                ? MaximumDelay.Ticks
                : delay.Ticks * 2;
            delay = TimeSpan.FromTicks(Math.Min(nextTicks, MaximumDelay.Ticks));
        }

        return delay;
    }
}
