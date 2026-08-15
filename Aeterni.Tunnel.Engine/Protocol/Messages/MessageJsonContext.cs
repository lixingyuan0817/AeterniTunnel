using System.Text.Json.Serialization;
using Aeterni.Tunnel.Engine.Protocol;

namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>
/// 控制消息 JSON 源生成上下文（NativeAOT 兼容：无反射序列化）。
/// 多态判别由 JsonPolymorphic/JsonDerivedType（Message 基类）驱动。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(HelloAckMessage))]
[JsonSerializable(typeof(RegisterProxyMessage))]
[JsonSerializable(typeof(RegisterProxyAckMessage))]
[JsonSerializable(typeof(OpenTunnelMessage))]
[JsonSerializable(typeof(UnregisterProxyMessage))]
[JsonSerializable(typeof(HeartbeatMessage))]
[JsonSerializable(typeof(HeartbeatAckMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(RemoveProxyCommandMessage))]
[JsonSerializable(typeof(CommandAckMessage))]
[JsonSerializable(typeof(PortPolicyMessage))]
[JsonSerializable(typeof(LinkType))]
public partial class MessageJsonContext : JsonSerializerContext;
