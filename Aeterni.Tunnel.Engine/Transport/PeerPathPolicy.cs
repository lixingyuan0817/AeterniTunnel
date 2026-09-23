namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// Selects which authenticated peer data path may be used.
/// </summary>
public enum PeerPathPolicy
{
    PreferDirect = 0,
    DirectOnly = 1,
    RelayOnly = 2,
}

public enum PeerPathKind
{
    Direct = 0,
    Relay = 1,
}

public enum PeerPathFailure
{
    None = 0,
    DirectUnavailable = 1,
    RelayUnauthorized = 2,
    NoAllowedPath = 3,
}

public readonly record struct PeerPathDecision(
    PeerPathKind? Path,
    PeerPathFailure Failure)
{
    public bool IsAllowed => Path is not null;

    public static PeerPathDecision Direct() => new(PeerPathKind.Direct, PeerPathFailure.None);

    public static PeerPathDecision Relay() => new(PeerPathKind.Relay, PeerPathFailure.None);

    public static PeerPathDecision Denied(PeerPathFailure failure) => new(null, failure);
}

/// <summary>
/// Pure path selection. It deliberately does not establish sockets or silently
/// downgrade authentication; those responsibilities belong to a transport adapter.
/// </summary>
public static class PeerPathSelector
{
    public static PeerPathDecision Select(
        PeerPathPolicy policy,
        bool directAvailable,
        bool relayAuthorized)
    {
        return policy switch
        {
            PeerPathPolicy.DirectOnly => directAvailable
                ? PeerPathDecision.Direct()
                : PeerPathDecision.Denied(PeerPathFailure.DirectUnavailable),
            PeerPathPolicy.RelayOnly => relayAuthorized
                ? PeerPathDecision.Relay()
                : PeerPathDecision.Denied(PeerPathFailure.RelayUnauthorized),
            PeerPathPolicy.PreferDirect when directAvailable => PeerPathDecision.Direct(),
            PeerPathPolicy.PreferDirect when relayAuthorized => PeerPathDecision.Relay(),
            PeerPathPolicy.PreferDirect => PeerPathDecision.Denied(PeerPathFailure.NoAllowedPath),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown peer path policy."),
        };
    }
}
