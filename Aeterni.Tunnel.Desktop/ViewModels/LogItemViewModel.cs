using Avalonia.Media;

namespace Aeterni.Tunnel.Desktop.ViewModels;

/// <summary>日志级别（分级着色 + 级别徽章）</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error,
    Success,
    Trace,
}

/// <summary>日志行（分级着色，与 CLI LogView 同款分类、Web 同款调色板）</summary>
public sealed class LogItemViewModel
{
    // Web 调色板（wwwroot/css/app.css 深色主题语义色）
    private static readonly IBrush Error = SolidColorBrush.Parse("#F87171");
    private static readonly IBrush Warning = SolidColorBrush.Parse("#FBBF24");
    private static readonly IBrush Success = SolidColorBrush.Parse("#4CD964");
    private static readonly IBrush Info = SolidColorBrush.Parse("#60A5FA");
    private static readonly IBrush Muted = SolidColorBrush.Parse("#94A3B8");

    public string Text { get; }

    public IBrush Brush { get; }

    public LogLevel Level { get; }

    /// <summary>级别徽章文本（ERR/WRN/OK/INF/·）</summary>
    public string LevelText => Level switch
    {
        LogLevel.Error => "ERR",
        LogLevel.Warn => "WRN",
        LogLevel.Success => "OK",
        LogLevel.Trace => "·",
        _ => "INF",
    };

    public LogItemViewModel(string text, IBrush brush, LogLevel level)
    {
        Text = text;
        Brush = brush;
        Level = level;
    }

    public static LogItemViewModel Create(string text)
    {
        var (brush, level) = Classify(text);
        return new LogItemViewModel(text, brush, level);
    }

    /// <summary>按内容关键词分类：失败/异常→错误，警告/超时→警告，成功/登录→成功，连接/重连→信息，其余→杂项</summary>
    public static (IBrush Brush, LogLevel Level) Classify(string text) =>
        text.Contains("失败") || text.Contains("异常") || text.Contains("✗") || text.Contains("拒绝") ? (Error, LogLevel.Error) :
        text.Contains("警告") || text.Contains("⚠") || text.Contains("超时") ? (Warning, LogLevel.Warn) :
        text.Contains("成功") || text.Contains("登录") || text.Contains("注册") || text.Contains("在线") ? (Success, LogLevel.Success) :
        text.Contains("连接") || text.Contains("心跳") || text.Contains("重连") ? (Info, LogLevel.Info) :
        (Muted, LogLevel.Trace);
}
