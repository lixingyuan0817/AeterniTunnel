using Aeterni.Tunnel.Desktop.ViewModels;
using Avalonia.Controls;

namespace Aeterni.Tunnel.Desktop.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        // 日志追加后自动滚动到底部
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.Logs.CollectionChanged += (_, _) => LogScroll.ScrollToEnd();
        };
    }
}
