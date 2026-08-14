using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>Tab 标签头（自绘：隧道列表 / 日志，激活标签高亮）</summary>
public sealed class TabHeaderView : View
{
    private int _active;

    /// <summary>当前激活标签（0=隧道列表 1=日志）</summary>
    public int Active
    {
        get => _active;
        set { _active = value; SetNeedsDraw(); }
    }

    public TabHeaderView()
    {
        Width = Dim.Fill();
        Height = 1;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var r = base.OnDrawingContent(context);
        var tabs = new[] { "  ▸ 隧道列表  ", "  ▸ 日志  " };
        var x = 1;
        for (var i = 0; i < tabs.Length; i++)
        {
            var text = tabs[i];
            SetAttribute(i == _active ? AppleColors.Info : AppleColors.Dim);
            AddStr(x, 0, text);
            x += text.Length + 1;
        }
        return r;
    }
}
