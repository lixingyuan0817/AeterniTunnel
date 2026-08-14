using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// 自绘日志视图：按日志等级着色（失败红 / 警告黄 / 成功绿 / 连接信息 / 其余灰）。
/// v2.4 TextView 不支持逐行着色，故自绘（OnDrawingContent + SetAttribute + AddStr）。
/// </summary>
public sealed class LogView : View
{
    private readonly List<(string Text, Terminal.Gui.Drawing.Attribute Color)> _lines = [];

    /// <summary>追加一行日志（自动按内容分级着色，自动滚动到最新）</summary>
    public void Add(string text)
    {
        _lines.Add((text, Classify(text)));
        while (_lines.Count > 500)
            _lines.RemoveAt(0);
        SetNeedsDraw();
    }

    private static Terminal.Gui.Drawing.Attribute Classify(string text) =>
        text.Contains("失败") || text.Contains("异常") || text.Contains("✗") || text.Contains("拒绝") ? AppleColors.Error :
        text.Contains("警告") || text.Contains("⚠") || text.Contains("超时") ? AppleColors.Warning :
        text.Contains("成功") || text.Contains("登录") || text.Contains("注册") || text.Contains("在线") ? AppleColors.Success :
        text.Contains("连接") || text.Contains("心跳") || text.Contains("重连") ? AppleColors.Info :
        AppleColors.Dim;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var r = base.OnDrawingContent(context);
        var height = Frame.Height;
        var lines = _lines.TakeLast(Math.Max(1, height)).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            SetAttribute(lines[i].Color);
            AddStr(0, i, lines[i].Text);
        }
        return r;
    }
}
