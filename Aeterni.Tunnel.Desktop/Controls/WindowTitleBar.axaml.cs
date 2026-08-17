using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Aeterni.Tunnel.Desktop.Controls;

/// <summary>
/// 三平台统一的自定义标题栏（配合 Window.SystemDecorations="None"）：
/// 左侧标题自动跟随 Window.Title，右侧最小化/最大化还原/关闭按钮；
/// 标题栏空白区拖拽移动窗口、双击最大化/还原；非可缩放窗口不显示最大化按钮。
/// </summary>
public partial class WindowTitleBar : UserControl
{
    private Window? _window;

    public WindowTitleBar()
    {
        InitializeComponent();

        MinBtn.Click += (_, _) => { if (_window is not null) _window.WindowState = WindowState.Minimized; };
        MaxBtn.Click += (_, _) => ToggleMaximize();
        CloseBtn.Click += (_, _) => _window?.Close();

        AddHandler(PointerPressedEvent, OnTitleBarPressed, RoutingStrategies.Bubble);
        AddHandler(DoubleTappedEvent, OnTitleBarDoubleTapped, RoutingStrategies.Bubble);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null)
            return;
        _window.PropertyChanged += OnWindowPropertyChanged;
        MaxBtn.IsVisible = _window.CanResize;
        TitleText.Text = _window.Title;
        UpdateMaxIcon(_window.WindowState);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
            _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.TitleProperty)
            TitleText.Text = e.NewValue as string;
        else if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState state)
            UpdateMaxIcon(state);
    }

    private void UpdateMaxIcon(WindowState state)
    {
        // 红绿灯风格：绿点点击在最大化/还原间切换，仅更新提示
        ToolTip.SetTip(MaxBtn, state == WindowState.Maximized ? "还原" : "最大化");
    }

    private void ToggleMaximize()
    {
        if (_window is null || !_window.CanResize)
            return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>拖拽移动：仅标题栏空白处（按钮区不触发）；双击时直接切换最大化，避免进入系统 move-loop 竞争</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_window is null || e.Source is not Visual src)
            return;
        if (src.FindAncestorOfType<Button>() is not null)
            return;   // 按钮区域交给按钮处理
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        _window.BeginMoveDrag(e);
    }

    /// <summary>双击最大化/还原（仅可缩放窗口）</summary>
    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_window is null || !_window.CanResize)
            return;
        if (e.Source is Visual src && src.FindAncestorOfType<Button>() is not null)
            return;
        ToggleMaximize();
    }
}
