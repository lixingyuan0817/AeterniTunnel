using Avalonia;
using Avalonia.Controls;

namespace AeterniLink.Controls;

/// <summary>
/// 表单字段封装：Label 为左侧标签文字，Content 为右侧输入控件。
/// 继承 ContentControl（模板内渲染），标签列宽 72，与 App.axaml 的 fieldLabel 样式配套。
/// </summary>
public partial class AeterniFormField : ContentControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<AeterniFormField, string>(nameof(Label));

    /// <summary>字段标签（左侧灰色小字）</summary>
    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public AeterniFormField() => InitializeComponent();
}
