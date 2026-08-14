namespace Aeterni.Tunnel.Engine.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warn,
    Error,
}

public class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Message { get; init; } = "";
}
