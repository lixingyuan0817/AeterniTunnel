namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>建立数据隧道：Server → Agent。ChannelId 由 Server 分配，Agent 用 AcceptChannel 建立同 id 通道。</summary>
public sealed record OpenTunnelMessage(string ProxyId, ushort ChannelId) : Message;
