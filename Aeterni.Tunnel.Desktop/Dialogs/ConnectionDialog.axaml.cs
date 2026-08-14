using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aeterni.Tunnel.Desktop.Dialogs;

/// <summary>
/// 连接设置弹窗（首启无配置 / 配置不完整时引导填写）。
/// 校验通过后经 Confirmed + Address/Port/Token/UseTls 返回，由调用方写入 agent.toml 并重连。
/// </summary>
public partial class ConnectionDialog : Window
{
    /// <summary>是否确认保存（false = 取消）</summary>
    public bool Confirmed { get; private set; }

    public string Address { get; private set; } = "";

    public int Port { get; private set; }

    public string Token { get; private set; } = "";

    public bool UseTls { get; private set; }

    public ConnectionDialog(string address, string port, string token, bool useTls)
    {
        InitializeComponent();
        AddressBox.Text = address;
        PortBox.Text = port;
        TokenBox.Text = token;
        TlsBox.IsChecked = useTls;

        OkBtn.Click += OnOk;
        CancelBtn.Click += (_, _) => Close();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var addr = (AddressBox.Text ?? "").Trim();
        if (addr.Length == 0)
        {
            AddressBox.Focus();
            return;
        }
        if (!int.TryParse((PortBox.Text ?? "").Trim(), out var port) || port is < 1 or > 65535)
        {
            PortBox.Focus();
            return;
        }
        var token = (TokenBox.Text ?? "").Trim();
        if (token.Length == 0)
        {
            TokenBox.Focus();
            return;
        }

        Address = addr;
        Port = port;
        Token = token;
        UseTls = TlsBox.IsChecked == true;
        Confirmed = true;
        Close();
    }
}
