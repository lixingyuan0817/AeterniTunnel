namespace Aeterni.Tunnel.Engine.Server;

/// <summary>Dashboard /api/status 响应体：{ clients: [...] }</summary>
public sealed class StatusResponse
{
    public List<StatusClient> Clients { get; set; } = new();
}
