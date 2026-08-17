using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AeterniLink.Controls;

/// <summary>
/// AeterniLink 窗口基类：统一「无边框 + 毛玻璃 + 透明背景 + 居中启动 + 默认尺寸」。
/// 子类 XAML 以 &lt;aeterni:AeterniWindow&gt; 为根元素，壳内自绘圆角 Border 承载内容。
/// 三平台（mac/win）统一窗口外观与行为。
/// </summary>
public class AeterniWindow : Window
{
    public AeterniWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent,
        ];
        Background = Brushes.Transparent;
        Width = 1000;
        Height = 660;
        MinWidth = 880;
        MinHeight = 580;
    }
}
