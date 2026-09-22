namespace Aeterni.Tunnel.Engine.Client;

/// <summary>
/// Agent 连接配置。
/// </summary>
public sealed record AgentOptions(
    string ServerAddr,
    int ServerPort,
    string Token,
    string ClientId,
    bool UseTls = true,
    bool ValidateCertificate = true,
    TimeSpan HeartbeatInterval = default,
    string? TlsServerName = null,
    string? TlsCaCertificatePath = null);
