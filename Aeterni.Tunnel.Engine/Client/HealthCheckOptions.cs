namespace Aeterni.Tunnel.Engine.Client;

/// <summary>健康检查配置（frp healthCheck 语义）</summary>
public sealed record HealthCheckOptions(
    string Type,                // "tcp" / "http"
    string? Path,               // http 探测路径
    int IntervalSeconds = 10,
    int TimeoutSeconds = 3,
    int MaxFailed = 3);
