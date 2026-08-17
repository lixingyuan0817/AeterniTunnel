using System.Reflection;
using Aeterni.Tunnel.Common;
using Aeterni.Tunnel.Launcher.Models;

namespace Aeterni.Tunnel.Launcher;

/// <summary>
/// 游戏模板库：加载内嵌 Templates/*.toml，按 id 检索。
/// 模板声明运行方式（jar/script/command）、Java 版本、默认 env、启动参数。
/// </summary>
public sealed class GameTemplateStore
{
    private readonly Dictionary<string, GameTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);

    private GameTemplateStore() { }

    /// <summary>加载内嵌模板资源</summary>
    public static GameTemplateStore LoadBuiltIn()
    {
        var store = new GameTemplateStore();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".toml")))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
                continue;
            using var reader = new StreamReader(stream);
            var template = ParseToml(reader.ReadToEnd());
            if (!string.IsNullOrEmpty(template.Id))
                store._templates[template.Id] = template;
        }
        return store;
    }

    public static GameTemplateStore FromTexts(IEnumerable<(string Id, string Toml)> sources)
    {
        var store = new GameTemplateStore();
        foreach (var (id, toml) in sources)
        {
            var template = ParseToml(toml);
            if (string.IsNullOrEmpty(template.Id))
                template.Id = id;
            store._templates[template.Id] = template;
        }
        return store;
    }

    public IEnumerable<GameTemplate> All() => _templates.Values;

    public GameTemplate? Find(string id) =>
        _templates.TryGetValue(id, out var t) ? t : null;

    public GameTemplate Get(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"模板「{id}」不存在");

    private static GameTemplate ParseToml(string toml)
    {
        var kv = MinimalToml.Parse(toml);
        var t = new GameTemplate
        {
            Id = GetStr(kv, "id") ?? "",
            DisplayName = GetStr(kv, "displayName") ?? "",
            StartMode = GetStr(kv, "startMode") ?? "jar",
            JavaVersion = GetStr(kv, "javaVersion"),
            Jar = GetStr(kv, "jar"),
            JarUrl = GetStr(kv, "jarUrl"),
            Script = GetStr(kv, "script"),
            Command = GetStr(kv, "command"),
            MemoryMinMb = GetInt(kv, "memoryMinMb", 1024),
            MemoryMaxMb = GetInt(kv, "memoryMaxMb", 8192),
            Env = GetSection(kv, "env"),
            LaunchArgs = GetStr(kv, "launchArgs") ?? "-Xmx{memory}M -jar {jar} nogui",
            StopCommand = GetStr(kv, "stopCommand") ?? "stop",
            FirstRunFiles = GetSection(kv, "firstRunFiles"),
        };
        return t;
    }

    private static string? GetStr(Dictionary<string, object?> kv, string key) =>
        kv.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int GetInt(Dictionary<string, object?> kv, string key, int def) =>
        kv.TryGetValue(key, out var v) && v is int n ? n : def;

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
