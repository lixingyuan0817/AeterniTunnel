using Avalonia;
using Avalonia.Controls;

namespace AeterniLink.Controls;

/// <summary>按钮风格变体：Default=标准 / Primary=主题绿主操作 / Ghost=面板次操作 / Danger=危险操作</summary>
public enum AeterniButtonVariant
{
    Default,
    Primary,
    Ghost,
    Danger,
}

/// <summary>
/// AeterniLink 按钮组件：通过 Variant 应用对应样式类（primary/ghost/danger），
/// 复用 App.axaml 全局样式（悬浮/按下同色系深浅 + 按下 0.96 缩放过渡）。
/// 用法：&lt;controls:AeterniButton Variant="Primary" Content="保存" /&gt;
/// </summary>
public class AeterniButton : Button
{
    public static readonly StyledProperty<AeterniButtonVariant> VariantProperty =
        AvaloniaProperty.Register<AeterniButton, AeterniButtonVariant>(nameof(Variant));

    /// <summary>按钮风格（Default / Primary / Ghost / Danger）</summary>
    public AeterniButtonVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    static AeterniButton()
    {
        VariantProperty.Changed.AddClassHandler<AeterniButton>((btn, _) => btn.UpdateClasses());
    }

    public AeterniButton()
    {
        UpdateClasses();
    }

    private void UpdateClasses()
    {
        Classes.Set("primary", Variant == AeterniButtonVariant.Primary);
        Classes.Set("ghost", Variant == AeterniButtonVariant.Ghost);
        Classes.Set("danger", Variant == AeterniButtonVariant.Danger);
    }
}
