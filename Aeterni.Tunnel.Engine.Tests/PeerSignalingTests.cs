using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Server;
using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class PeerSignalingTests
{
    [Fact]
    public async Task AuthorizedRequest_RoutesNoticeAndDescriptionOnlyToPeer()
    {
        var registry = new PeerSignalingRegistry(new AllowPeerAuthorization());
        var a = new FakePeer("peer-a");
        var b = new FakePeer("peer-b");
        registry.Register(a);
        registry.Register(b);

        await registry.HandleRequestAsync(a, new PeerRequestMessage("r1", "peer-b", "svc",
            DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()));

        var ack = Assert.IsType<PeerRequestAckMessage>(Assert.Single(a.Messages));
        Assert.True(ack.Ok);
        Assert.False(string.IsNullOrWhiteSpace(ack.LeaseToken));
        var notice = Assert.IsType<PeerRequestNoticeMessage>(Assert.Single(b.Messages));
        Assert.Equal("peer-a", notice.RequesterPeerId);
        Assert.Equal(ack.LeaseToken, notice.LeaseToken);

        await registry.HandleDescriptionAsync(a, new PeerDescriptionMessage("r1", true, "v=0"));
        Assert.Contains(b.Messages, message => message is PeerDescriptionMessage);
        Assert.Contains(a.Messages, message => message is PeerSignalAckMessage { Ok: true });

        await registry.HandleDescriptionAsync(b, new PeerDescriptionMessage("r1", true, "spoof"));
        Assert.Contains(b.Messages, message => message is PeerSignalAckMessage { Ok: false });
    }

    [Fact]
    public async Task DeniedOrOfflineRequest_DoesNotLeakTargetPresence()
    {
        var registry = new PeerSignalingRegistry();
        var a = new FakePeer("peer-a");
        registry.Register(a);

        await registry.HandleRequestAsync(a, new PeerRequestMessage("r1", "missing", "svc",
            DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()));

        var ack = Assert.IsType<PeerRequestAckMessage>(Assert.Single(a.Messages));
        Assert.False(ack.Ok);
        Assert.Contains("不在线", ack.Error);
    }

    [Fact]
    public async Task ExpiredLeaseAndCandidateFlood_AreRejected()
    {
        var registry = new PeerSignalingRegistry(new AllowPeerAuthorization());
        var a = new FakePeer("peer-a");
        var b = new FakePeer("peer-b");
        registry.Register(a);
        registry.Register(b);
        await registry.HandleRequestAsync(a, new PeerRequestMessage("r1", "peer-b", "svc",
            DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()));

        for (var i = 0; i < 128; i++)
            await registry.HandleCandidateAsync(a, new PeerCandidateMessage("r1", $"candidate:{i}"));
        await registry.HandleCandidateAsync(a, new PeerCandidateMessage("r1", "candidate:overflow"));

        Assert.Contains(a.Messages, message => message is PeerSignalAckMessage { Ok: false });
        await registry.HandleRequestAsync(a, new PeerRequestMessage("expired", "peer-b", "svc",
            DateTimeOffset.UtcNow.AddMilliseconds(-1).ToUnixTimeMilliseconds()));
        Assert.Contains(a.Messages, message => message is PeerRequestAckMessage { Ok: false });
    }

    private sealed class FakePeer(string id) : IPeerSignalEndpoint
    {
        public string PeerId { get; } = id;
        public List<Message> Messages { get; } = [];
        public ValueTask SendPeerSignalAsync(Message message)
        {
            lock (Messages)
                Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AllowPeerAuthorization : ICommunicationAuthorizationProvider
    {
        public ValueTask<bool> AuthorizeAsync(string localPeerId, string remotePeerId, string serviceId,
            CancellationToken ct = default) => ValueTask.FromResult(serviceId == "svc");
    }
}
