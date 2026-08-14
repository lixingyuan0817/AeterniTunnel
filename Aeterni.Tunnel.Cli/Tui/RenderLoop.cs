using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// 无闪整页渲染循环（Codex/Claude CLI 风格）：
/// - 终端尺寸不变：光标归位 + 覆盖重绘 + 清除帧下方残留（不清屏 → 不频闪）；
/// - 终端尺寸变化：清屏一次 + 全量重绘（消除快速缩放残留）。
/// </summary>
public static class RenderLoop
{
    private static int _lastWidth;
    private static int _lastHeight;

    /// <summary>进入渲染循环（Ctrl+C / ct 取消退出，恢复光标）</summary>
    public static async Task RunAsync(Func<IRenderable> buildFrame, CancellationToken ct, int intervalMs = 500)
    {
        try { AnsiConsole.Cursor.Hide(); } catch (IOException) { /* 重定向/无控制台 */ }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Render(buildFrame());
                await Task.Delay(intervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        finally
        {
            try { AnsiConsole.Cursor.Show(); } catch (IOException) { }
            Console.WriteLine();
        }
    }

    /// <summary>渲染一帧：尺寸未变 → 归位覆盖（不闪）；尺寸变化 → 清屏重绘</summary>
    private static void Render(IRenderable frame)
    {
        var width = Format.TerminalWidth();
        var height = Format.TerminalHeight();

        if (width != _lastWidth || height != _lastHeight)
        {
            // 尺寸变化：清屏 + 全量重绘（缩放时避免残留）
            AnsiConsole.Write("\x1b[H\x1b[2J");
            _lastWidth = width;
            _lastHeight = height;
        }
        else
        {
            // 尺寸未变：光标归位 + 覆盖重绘（不清屏，避免频闪）
            AnsiConsole.Write("\x1b[H");
        }

        AnsiConsole.Write(frame);
        // 清除帧下方可能残留的旧内容（新帧比旧帧短时）
        AnsiConsole.Write("\x1b[J");
    }
}
