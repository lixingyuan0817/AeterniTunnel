using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>顶部 Banner（自绘，Apple 绿标题 + 灰副标题）</summary>
public sealed class BannerView : View
{
    public BannerView()
    {
        Width = Dim.Fill();
        Height = 3;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var r = base.OnDrawingContent(context);
        var w = Frame.Width;

        // 标题（Apple 绿）
        var title = "AETERNI TUNNEL";
        var x = Math.Max(0, (w - title.Length) / 2);
        SetAttribute(AppleColors.Success);
        AddStr(x, 0, title);

        // 副标题（灰）
        var sub = "ATC 客户端 · 内网穿透 · 安全 REPL";
        var sx = Math.Max(0, (w - sub.Length) / 2);
        SetAttribute(AppleColors.Dim);
        AddStr(sx, 1, sub);

        // 分隔线（灰）
        SetAttribute(AppleColors.Dim);
        for (var i = 0; i < w; i++)
            AddStr(i, 2, "─");
        return r;
    }
}
