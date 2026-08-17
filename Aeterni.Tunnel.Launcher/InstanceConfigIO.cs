using Aeterni.Tunnel.Common;
using Aeterni.Tunnel.Launcher.Models;

namespace Aeterni.Tunnel.Launcher;

/// <summary>实例配置 server.toml 读写（读用 MinimalToml.Parse，写用轻量序列化）</summary>
public static class InstanceConfigIO
{
    public static InstanceConfig Load(string path)
    {
        var kv = MinimalToml.Parse(File.ReadAllText(path));
        return new InstanceConfig
        {
            Name = GetStr(kv, "name") ?? Path.GetFileName(Path.GetDirectoryName(path)) ?? "",
            TemplateId = GetStr(kv, "templateId") ?? "",
            Port = GetInt(kv, "port", 25565),
            MemoryMb = GetIntOrNull(kv, "memoryMb"),
            JavaVersion = GetStrOrNull(kv, "javaVersion"),
            JavaPath = GetStrOrNull(kv, "javaPath"),
            Env = GetSection(kv, "env"),
            ExtraArgs = GetStrOrNull(kv, "extraArgs"),
            AutoRestart = GetBool(kv, "autoRestart", true),
            TunnelRemotePort = GetInt(kv, "tunnelRemotePort", 0),
            LaunchCount = GetInt(kv, "launchCount", 0),
        };
    }

    public static void Save(string path, InstanceConfig cfg)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"name = \"{cfg.Name}\"");
        sb.AppendLine($"templateId = \"{cfg.TemplateId}\"");
        sb.AppendLine($"port = {cfg.Port}");
        if (cfg.MemoryMb is { } mem) sb.AppendLine($"memoryMb = {mem}");
        if (cfg.JavaVersion is { } jv) sb.AppendLine($"javaVersion = \"{jv}\"");
        if (cfg.JavaPath is { } jp) sb.AppendLine($"javaPath = \"{jp}\"");
        if (cfg.ExtraArgs is { } ea) sb.AppendLine($"extraArgs = \"{ea}\"");
        sb.AppendLine($"autoRestart = {(cfg.AutoRestart ? "true" : "false")}");
        sb.AppendLine($"tunnelRemotePort = {cfg.TunnelRemotePort}");
        sb.AppendLine($"launchCount = {cfg.LaunchCount}");
        if (cfg.Env.Count > 0)
        {
            sb.AppendLine("[env]");
            foreach (var (k, v) in cfg.Env)
                sb.AppendLine($"{k} = \"{v}\"");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
    }

    private static string? GetStr(Dictionary<string, object?> kv, string key) =>
        kv.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static string? GetStrOrNull(Dictionary<string, object?> kv, string key) => GetStr(kv, key);

    private static int GetInt(Dictionary<string, object?> kv, string key, int def) =>
        kv.TryGetValue(key, out var v) && v is int n ? n : def;

    private static int? GetIntOrNull(Dictionary<string, object?> kv, string key) =>
        kv.TryGetValue(key, out var v) && v is int n ? n : null;

    private static bool GetBool(Dictionary<string, object?> kv, string key, bool def) =>
        kv.TryGetValue(key, out var v) && v is bool b ? b : def;

    private static Dictionary<string, string> GetSection(Dictionary<string, object?> kv, string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in kv)
        {
            if (k.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase) && v is not null)
                result[k[(section.Length + 1)..]] = v.ToString() ?? "";
        }
        return result;
    }
}
