using System.Collections.Specialized;
using Avalonia;
using Aeterni.Tunnel.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aeterni.Tunnel.Desktop.Views;

/// <summary>首页 View：首启提示 + 统计卡 + 日志（日志追加后自动滚动到底部）</summary>
public partial class HomeView : UserControl
{
    private MainWindowViewModel? _vm;

    public HomeView()
    {
        InitializeComponent();
        // 日志追加后自动滚动到底部（DataContext 由 MainWindow 继承，变更时换绑避免重复订阅）
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.Logs.CollectionChanged -= OnLogsChanged;
            _vm = DataContext as MainWindowViewModel;
            if (_vm is not null)
                _vm.Logs.CollectionChanged += OnLogsChanged;
        };
    }

    /// <summary>从可视树卸载时退订，避免随控件生命周期泄漏订阅</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null)
        {
            _vm.Logs.CollectionChanged -= OnLogsChanged;
            _vm = null;
        }
    }

    /// <summary>滚动调度到 UI 线程（日志可能由后台线程追加）</summary>
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(LogScroll.ScrollToEnd);
}
