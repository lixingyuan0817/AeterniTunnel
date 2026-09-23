using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class CommunicationContractTests
{
    [Fact]
    public async Task DefaultAuthorizationProvider_DeniesRequest()
    {
        var provider = new DenyAllCommunicationAuthorizationProvider();

        Assert.False(await provider.AuthorizeAsync("local", "remote", "service"));
    }

    [Fact]
    public async Task TcpTlsTransport_AdvertisesOnlyReliableSemantics()
    {
        await using var transport = TcpTlsTransport.Client("127.0.0.1", 7000, useTls: true);

        Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.ReliableStream));
        Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.ReliableMessage));
        Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.RealtimeDatagram));
        Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.PeerSession));
    }
}
