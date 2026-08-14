using Avalonia.Controls;

namespace Aeterni.Tunnel.Desktop.Dialogs;

/// <summary>确认弹窗：确定 → Confirmed=true 关闭</summary>
public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog() : this("确认", "确定执行该操作？") { }

    public ConfirmDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkBtn.Click += (_, _) => { Confirmed = true; Close(); };
        CancelBtn.Click += (_, _) => Close();
    }
}
