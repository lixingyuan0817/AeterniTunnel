using System.Text;
using System.Text.Json;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Tests;

public class MessageCodecTests
{
    [Fact]
    public void Serialize_Hello_ContainsTypeDiscriminator()
    {
        var msg = new HelloMessage("agent-1", 1, "token", "pc-01");
        var json = MessageCodec.SerializeToString(msg);

        Assert.Contains("\"type\":\"hello\"", json);
        Assert.Contains("\"clientId\":\"agent-1\"", json);
        Assert.Contains("\"token\":\"token\"", json);
    }

    [Fact]
    public void LegacyHelloWithoutCapabilities_RemainsReadable()
    {
        var restored = MessageCodec.Deserialize(Encoding.UTF8.GetBytes(
            "{\"type\":\"hello\",\"clientId\":\"legacy\",\"version\":1,\"token\":\"t\",\"hostname\":\"pc\"}"));

        var hello = Assert.IsType<HelloMessage>(restored);
        Assert.Equal(0UL, hello.Capabilities);
    }

    [Fact]
    public void HelloWithUnknownOptionalProperty_RemainsReadable()
    {
        var restored = MessageCodec.Deserialize(Encoding.UTF8.GetBytes(
            "{\"type\":\"hello\",\"clientId\":\"newer\",\"version\":1,\"token\":\"t\",\"hostname\":\"pc\",\"futureOptional\":true}"));

        Assert.IsType<HelloMessage>(restored);
    }

    [Fact]
    public void HelloCapabilities_RoundTripSeparatelyFromProtocolVersion()
    {
        var message = new HelloMessage("agent", ProtocolContract.CurrentVersion, "token", "pc",
            (ulong)(ProtocolCapabilities.ReliableMessage | ProtocolCapabilities.UdpSourceAssociation));

        var restored = Assert.IsType<HelloMessage>(MessageCodec.Deserialize(MessageCodec.Serialize(message)));

        Assert.Equal(ProtocolContract.CurrentVersion, restored.Version);
        Assert.Equal(message.Capabilities, restored.Capabilities);
    }

    [Fact]
    public void UdpSourceEnvelope_PreservesSourceAndPayload()
    {
        var encoded = UdpSourceEnvelope.Encode(42, new byte[] { 1, 2, 3 });

        Assert.True(UdpSourceEnvelope.TryDecode(encoded, out var sourceId, out var payload));
        Assert.Equal((uint)42, sourceId);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
        Assert.False(UdpSourceEnvelope.TryDecode(new byte[3], out _, out _));
    }

    [Theory]
    [InlineData(typeof(HelloMessage))]
    [InlineData(typeof(HelloAckMessage))]
    [InlineData(typeof(RegisterProxyMessage))]
    [InlineData(typeof(RegisterProxyAckMessage))]
    [InlineData(typeof(UnregisterProxyMessage))]
    [InlineData(typeof(HeartbeatMessage))]
    [InlineData(typeof(HeartbeatAckMessage))]
    [InlineData(typeof(ErrorMessage))]
    public void RoundTrip_AllMessageTypes(Type type)
    {
        Message msg = type.Name switch
        {
            nameof(HelloMessage) => new HelloMessage("a1", 1, "t", "h"),
            nameof(HelloAckMessage) => new HelloAckMessage(true, null, "0.1.0"),
            nameof(RegisterProxyMessage) => new RegisterProxyMessage("p1", LinkType.Tcp, "127.0.0.1", 25565, 7001, null, null),
            nameof(RegisterProxyAckMessage) => new RegisterProxyAckMessage("p1", true, "1.2.3.4:7001", null),
            nameof(UnregisterProxyMessage) => new UnregisterProxyMessage("p1"),
            nameof(HeartbeatMessage) => new HeartbeatMessage(1234567890),
            nameof(HeartbeatAckMessage) => new HeartbeatAckMessage(1234567890),
            nameof(ErrorMessage) => new ErrorMessage(400, "bad request"),
            _ => throw new ArgumentOutOfRangeException(),
        };

        var bytes = MessageCodec.Serialize(msg);
        var restored = MessageCodec.Deserialize(bytes);

        Assert.NotNull(restored);
        Assert.Equal(msg.GetType(), restored.GetType());
        Assert.Equal(msg, restored);
    }

    [Fact]
    public void RoundTrip_RegisterProxy_LinkTypeAsString()
    {
        var msg = new RegisterProxyMessage("p1", LinkType.Http | LinkType.Https, "127.0.0.1", 8080, null, "web.example.com", "web");
        var json = MessageCodec.SerializeToString(msg);

        Assert.Contains("\"linkType\":\"Http, Https\"", json);
        Assert.Contains("\"remotePort\":null", json);
        Assert.Contains("\"domain\":\"web.example.com\"", json);

        var restored = MessageCodec.Deserialize(MessageCodec.Serialize(msg)) as RegisterProxyMessage;
        Assert.Equal(LinkType.Http | LinkType.Https, restored!.LinkType);
        Assert.Equal("web", restored.Subdomain);
    }

    [Fact]
    public void Deserialize_UnknownType_Throws()
    {
        var json = """{"type":"unknownType","foo":1}""";
        Assert.ThrowsAny<JsonException>(() => MessageCodec.Deserialize(Encoding.UTF8.GetBytes(json)));
    }
}
