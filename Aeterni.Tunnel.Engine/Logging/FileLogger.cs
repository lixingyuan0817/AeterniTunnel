using System.Text;

namespace Aeterni.Tunnel.Engine.Logging;

/// <summary>
/// 文件日志：追加写入 + 大小滚动（超限重命名为 .1 并新建）。
/// 线程安全（写锁）；级别过滤。
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly string _path;
    private readonly LogLevel _minLevel;
    private readonly long _maxSize;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public FileLogger(string path, LogLevel minLevel = LogLevel.Info, long maxSizeBytes = 10 * 1024 * 1024)
    {
        _path = path;
        _minLevel = minLevel;
        _maxSize = maxSizeBytes;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public void Log(LogLevel level, string message)
    {
        if (level < _minLevel)
            return;

        var line = $"[{DateTime.Now:HH:mm:ss}] [{LevelName(level)}] {message}";

        lock (_lock)
        {
            EnsureWriter();
            _writer!.WriteLine(line);
            _writer.Flush();
            RollIfNeeded();
        }
    }

    public void Debug(string msg) => Log(LogLevel.Debug, msg);
    public void Info(string msg) => Log(LogLevel.Info, msg);
    public void Warn(string msg) => Log(LogLevel.Warn, msg);
    public void Error(string msg) => Log(LogLevel.Error, msg);

    /// <summary>把 LogLevel 解析为过滤级别（字符串 → 枚举；非法则 Info）</summary>
    public static LogLevel ParseLevel(string level) => level.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    private void EnsureWriter()
    {
        if (_writer is null)
        {
            var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }
    }

    private void RollIfNeeded()
    {
        if (_writer is null || _writer.BaseStream.Length < _maxSize)
            return;

        _writer.Dispose();
        _writer = null;

        // 滚动：当前 → .1（先删旧的 .1）
        var backup = _path + ".1";
        if (File.Exists(backup))
            File.Delete(backup);
        File.Move(_path, backup);
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "D",
        LogLevel.Success => "S",
        LogLevel.Warn => "W",
        LogLevel.Error => "E",
        _ => "I",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
