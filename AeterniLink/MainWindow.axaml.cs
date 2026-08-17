using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using AeterniLink.Controls;

namespace AeterniLink;

public partial class MainWindow : AeterniWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>顶栏主题切换：明亮 / 黑暗 / 跟随系统 → RequestedThemeVariant（所有 DynamicResource 即时联动）</summary>
    private void OnThemeChecked(object? sender, RoutedEventArgs e)
    {
        // IsCheckedChanged 在取消勾选时也会触发，仅在“被选中”那一刻执行切换
        if (sender is not RadioButton rb || rb.IsChecked != true || Application.Current is not { } app)
            return;
        app.RequestedThemeVariant = rb.Tag?.ToString() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,   // system / 未知
        };
    }
}
