using Terminal.Gui.Drawing;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>Apple（iOS/macOS HIG）语义配色——success/error/warning/info</summary>
public static class AppleColors
{
    /// <summary>成功绿 #34C759</summary>
    public static readonly Terminal.Gui.Drawing.Attribute Success = new(new Color(52, 199, 89), new Color(0, 0, 0));

    /// <summary>错误红 #FF3B30</summary>
    public static readonly Terminal.Gui.Drawing.Attribute Error = new(new Color(255, 59, 48), new Color(0, 0, 0));

    /// <summary>警告橙 #FF9500</summary>
    public static readonly Terminal.Gui.Drawing.Attribute Warning = new(new Color(255, 149, 0), new Color(0, 0, 0));

    /// <summary>信息蓝 #007AFF</summary>
    public static readonly Terminal.Gui.Drawing.Attribute Info = new(new Color(0, 122, 255), new Color(0, 0, 0));

    /// <summary>次要灰 #8E8E93</summary>
    public static readonly Terminal.Gui.Drawing.Attribute Dim = new(new Color(142, 142, 147), new Color(0, 0, 0));

    /// <summary>主文本白 #FFFFFF</summary>
    public static readonly Terminal.Gui.Drawing.Attribute White = new(new Color(255, 255, 255), new Color(0, 0, 0));
}
