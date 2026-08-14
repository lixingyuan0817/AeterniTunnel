namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>字节数格式化 + 终端尺寸（实时检测，兼容缩放）</summary>
public static class Format
{
    public static string Bytes(long value) => value switch
    {
        >= 1024 * 1024 => $"{value / 1024.0 / 1024.0:F1}MB",
        >= 1024 => $"{value / 1024.0:F1}KB",
        _ => $"{value}B",
    };

    /// <summary>当前终端宽度（每次调用实时读取；无控制台时回退 80）</summary>
    public static int TerminalWidth()
    {
        try
        {
            return Math.Max(Console.WindowWidth, 20);
        }
        catch (IOException)
        {
            return 80; // 重定向/无控制台
        }
    }

    /// <summary>当前终端高度（每次调用实时读取；无控制台时回退 24）</summary>
    public static int TerminalHeight()
    {
        try
        {
            return Math.Max(Console.WindowHeight, 10);
        }
        catch (IOException)
        {
            return 24;
        }
    }

    /// <summary>速率格式化（bytes/s → B/s / KB/s / MB/s）</summary>
    public static string Rate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / 1024 / 1024:F1}MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:F1}KB/s";
        return $"{bytesPerSec:F0}B/s";
    }
}
