using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class PeerRecoveryPolicyTests
{
    [Fact]
    public void NetworkFailureUsesBoundedExponentialRetry()
    {
        var policy = new PeerRecoveryPolicy(maxAttempts: 3,
            initialDelay: TimeSpan.FromMilliseconds(100), maximumDelay: TimeSpan.FromMilliseconds(250));

        Assert.Equal(new PeerRecoveryDecision(PeerRecoveryAction.RetryPeer,
            TimeSpan.FromMilliseconds(100), true),
            policy.Decide(PeerRecoveryReason.NetworkChanged, 0));
        Assert.Equal(new PeerRecoveryDecision(PeerRecoveryAction.RetryPeer,
            TimeSpan.FromMilliseconds(200), true),
            policy.Decide(PeerRecoveryReason.NetworkChanged, 1));
        Assert.Equal(new PeerRecoveryDecision(PeerRecoveryAction.RetryPeer,
            TimeSpan.FromMilliseconds(250), true),
            policy.Decide(PeerRecoveryReason.NetworkChanged, 2));
        Assert.Equal(PeerRecoveryAction.ClosePeer,
            policy.Decide(PeerRecoveryReason.NetworkChanged, 3).Action);
    }

    [Fact]
    public void AtsControlLossKeepsPeerSeparateAndRequiresReauthorization()
    {
        var decision = new PeerRecoveryPolicy().Decide(PeerRecoveryReason.AtsControlDisconnected, 99);

        Assert.Equal(PeerRecoveryAction.AwaitControl, decision.Action);
        Assert.True(decision.RequiresControlReauthorization);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
    }

    [Theory]
    [InlineData(PeerRecoveryReason.LeaseExpired)]
    [InlineData(PeerRecoveryReason.LeaseRevoked)]
    [InlineData(PeerRecoveryReason.ExplicitClose)]
    public void TerminalReasonsNeverRetry(PeerRecoveryReason reason)
    {
        var decision = new PeerRecoveryPolicy().Decide(reason, 0);

        Assert.Equal(PeerRecoveryAction.ClosePeer, decision.Action);
        Assert.False(decision.RequiresControlReauthorization);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
    }
}
