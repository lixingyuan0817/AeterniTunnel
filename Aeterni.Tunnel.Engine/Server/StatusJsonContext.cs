using System.Text.Json.Serialization;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>Dashboard 状态 JSON 源生成上下文（NativeAOT 兼容）</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(StatusClient))]
[JsonSerializable(typeof(StatusProxy))]
[JsonSerializable(typeof(ServerInfo))]
public partial class StatusJsonContext : JsonSerializerContext;
