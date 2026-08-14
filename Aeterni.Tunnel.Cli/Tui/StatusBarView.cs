using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>底部状态栏（自绘：连接状态 / 隧道数 / 流量 / 键位提示，Apple 语义色）</summary>
public sealed class StatusBarView : View
{
    private bool _connected;
    private int _tunnelCount;
    private long _up;
    private long _down;

    public StatusBarView()
    {
        Width = Dim.Fill();
        Height = 1;
    }

    public void SetStatus(bool connected, int tunnelCount, long up, long down)
    {
        _connected = connected;
        _tunnelCount = tunnelCount;
        _up = up;
        _down = down;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var r = base.OnDrawingContent(context);
        var w = Frame.Width;

        var x = 1;
        var stateText = _connected ? "● 已连接" : "○ 重连中";
        SetAttribute(_connected ? AppleColors.Success : AppleColors.Warning);
        AddStr(x, 0, stateText);
        x += stateText.Length + 2;

        var countText = $"隧道 {_tunnelCount}";
        SetAttribute(AppleColors.White);
        AddStr(x, 0, countText);
        x += countText.Length + 2;

        var flowText = $"↑{Format.Bytes(_up)} ↓{Format.Bytes(_down)}";
        SetAttribute(AppleColors.Dim);
        AddStr(x, 0, flowText);

        // 右侧键位提示
        var hint = "Tab 补全/切换 · ↑↓ 选择 · Enter 执行 · Ctrl+C 退出";
        var hx = Math.Max(x + 2, w - hint.Length - 1);
        SetAttribute(AppleColors.Dim);
        AddStr(hx, 0, hint);
        return r;
    }
}
