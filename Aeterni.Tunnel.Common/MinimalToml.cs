using System.Text;

namespace Aeterni.Tunnel.Common;

/// <summary>
/// 轻量 TOML 解析器（配置文件用）：
/// 支持 key = value、[section]、[[array]] 表数组、# 注释、字符串/数字/布尔/数字数组。
/// 不做完整 TOML 规范，满足 server.toml / agent.toml 需要即可。
/// </summary>
public static class MinimalToml
{
    /// <summary>解析 TOML 文本 → 扁平键（section.key / array.0.key）→ 值</summary>
    public static Dictionary<string, object?> Parse(string text)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        var arrayIndex = -1;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("[[") && line.EndsWith("]]"))
            {
                var name = line.Trim('[', ']').Trim();
                arrayIndex++;
                section = $"{name}.{arrayIndex}";
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                arrayIndex = -1;
                section = line.Trim('[', ']').Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var fullKey = section.Length > 0 ? $"{section}.{key}" : key;
            var value = ParseValue(line[(eq + 1)..].Trim());
            result[fullKey] = value;
        }

        return result;
    }

    private static object? ParseValue(string s)
    {
        s = s.Trim();

        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2)
            return s[1..^1];

        if (s.StartsWith('[') && s.EndsWith(']'))
        {
            // 数组项：整数存 int，字符串（如端口区间 "7071-7171"）存 string
            var items = s[1..^1]
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            var list = new List<object>();
            foreach (var item in items)
            {
                var raw = item;
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = raw[1..^1];
                list.Add(int.TryParse(raw, out var n) ? n : raw);
            }
            return list;
        }

        if (bool.TryParse(s, out var b))
            return b;
        if (int.TryParse(s, out var i))
            return i;
        return s;
    }

}
