using Avalonia;
using Avalonia.Controls;

namespace Aeterni.Tunnel.Desktop.Controls;

/// <summary>
/// 卡片 + 标题栏封装：Title 为标题文字，Content 为卡片内容。
/// 继承 ContentControl（模板内渲染），复用了 App.axaml 中 card / titleBar / cardTitle 样式。
/// </summary>
public partial class SectionCard : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SectionCard, string>(nameof(Title));

    /// <summary>卡片标题（显示为「竖条 + 文字」）</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public SectionCard() => InitializeComponent();
}
