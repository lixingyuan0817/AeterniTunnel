using System.Text.RegularExpressions;

namespace Aeterni.Tunnel.Engine.Logging;

/// <summary>
/// 解析 frpc 输出行 → LogEntry。
/// 级别前缀: [T] trace / [D] debug / [I] info / [W] warn / [E] error
/// </summary>
public static class LogLineParser
{
    private static readonly Regex LevelRegex = new(@"\[([TDIWE])\]", RegexOptions.Compiled);

    public static LogEntry Parse(string line)
    {
        var level = LogLevel.Info;
        var message = line;

        var m = LevelRegex.Match(line);
        if (m.Success)
        {
            level = m.Groups[1].Value switch
            {
                "T" or "D" => LogLevel.Debug,
                "W" => LogLevel.Warn,
                "E" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            message = line[(m.Index + 3)..].Trim();
        }

        if (level == LogLevel.Info &&
            (message.Contains("login to server success") || message.Contains("start proxy success")))
        {
            level = LogLevel.Success;
        }

        return new LogEntry { Level = level, Message = message };
    }
}
