using System.Text.Json.Serialization;

namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>
/// 控制消息基类（JSON 契约，带 "type" 判别字段，见 MessageCodec）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(HelloAckMessage), "helloAck")]
[JsonDerivedType(typeof(RegisterProxyMessage), "registerProxy")]
[JsonDerivedType(typeof(RegisterProxyAckMessage), "registerProxyAck")]
[JsonDerivedType(typeof(OpenTunnelMessage), "openTunnel")]
[JsonDerivedType(typeof(UnregisterProxyMessage), "unregisterProxy")]
[JsonDerivedType(typeof(HeartbeatMessage), "heartbeat")]
[JsonDerivedType(typeof(HeartbeatAckMessage), "heartbeatAck")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(RemoveProxyCommandMessage), "removeProxyCommand")]
[JsonDerivedType(typeof(CommandAckMessage), "commandAck")]
[JsonDerivedType(typeof(PortPolicyMessage), "portPolicy")]
[JsonDerivedType(typeof(RequestDataConnectionMessage), "requestDataConnection")]
[JsonDerivedType(typeof(DataConnectionTokenMessage), "dataConnectionToken")]
[JsonDerivedType(typeof(BindDataConnectionMessage), "bindDataConnection")]
[JsonDerivedType(typeof(BindDataConnectionAckMessage), "bindDataConnectionAck")]
[JsonDerivedType(typeof(PeerRequestMessage), "peerRequest")]
[JsonDerivedType(typeof(PeerRequestNoticeMessage), "peerRequestNotice")]
[JsonDerivedType(typeof(PeerRequestAckMessage), "peerRequestAck")]
[JsonDerivedType(typeof(PeerDescriptionMessage), "peerDescription")]
[JsonDerivedType(typeof(PeerCandidateMessage), "peerCandidate")]
[JsonDerivedType(typeof(PeerSignalAckMessage), "peerSignalAck")]
public abstract record Message;
