using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class PeerPathPolicyTests
{
    [Fact]
    public void PreferDirectFallsBackToAuthorizedRelay()
    {
        var decision = PeerPathSelector.Select(PeerPathPolicy.PreferDirect, directAvailable: false, relayAuthorized: true);

        Assert.True(decision.IsAllowed);
        Assert.Equal(PeerPathKind.Relay, decision.Path);
    }

    [Fact]
    public void DirectOnlyNeverFallsBackToRelay()
    {
        var decision = PeerPathSelector.Select(PeerPathPolicy.DirectOnly, directAvailable: false, relayAuthorized: true);

        Assert.False(decision.IsAllowed);
        Assert.Equal(PeerPathFailure.DirectUnavailable, decision.Failure);
    }

    [Fact]
    public void RelayOnlyRequiresRelayAuthorization()
    {
        var decision = PeerPathSelector.Select(PeerPathPolicy.RelayOnly, directAvailable: true, relayAuthorized: false);

        Assert.False(decision.IsAllowed);
        Assert.Equal(PeerPathFailure.RelayUnauthorized, decision.Failure);
    }

    [Fact]
    public void LeaseBindsIdentityServiceAndExpiry()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "service-1", issued, TimeSpan.FromSeconds(10));

        Assert.True(lease.Validate("peer-a", "peer-b", "service-1", lease.Token, issued.AddSeconds(1)));
        Assert.False(lease.Validate("peer-x", "peer-b", "service-1", lease.Token, issued.AddSeconds(1)));
        Assert.False(lease.Validate("peer-a", "peer-b", "service-2", lease.Token, issued.AddSeconds(1)));
        Assert.False(lease.Validate("peer-a", "peer-b", "service-1", lease.Token, issued.AddSeconds(10)));

        lease.Revoke();
        Assert.False(lease.Validate("peer-a", "peer-b", "service-1", lease.Token, issued.AddSeconds(2)));
    }

    [Fact]
    public void LeaseLifetimeIsBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PeerAuthorizationLease.Create("peer-a", "peer-b", "service-1", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(31)));
    }
}
