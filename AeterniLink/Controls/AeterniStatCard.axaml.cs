using Avalonia;
using Avalonia.Controls;

namespace AeterniLink.Controls;

/// <summary>
/// 统计卡封装：Label 为指标名（灰色小字），Content 为数值/内容区。
/// 继承 ContentControl（模板内渲染），用于首页「隧道总数 / 分组数 / 实时速率」等统计卡片。
/// </summary>
public partial class AeterniStatCard : ContentControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<AeterniStatCard, string>(nameof(Label));

    /// <summary>指标名（卡片上方灰色小字）</summary>
    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public AeterniStatCard() => InitializeComponent();
}
