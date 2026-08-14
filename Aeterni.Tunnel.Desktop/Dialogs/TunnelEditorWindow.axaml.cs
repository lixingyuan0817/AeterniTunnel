using System.Windows.Input;
using Aeterni.Tunnel.Desktop.ViewModels;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aeterni.Tunnel.Desktop.Dialogs;

/// <summary>
/// 隧道编辑弹窗（添加 / 修改共用）：确认后经 Result 返回 ProxyDefinition。
/// </summary>
public partial class TunnelEditorWindow : Window
{
    /// <summary>编辑结果（null = 取消）</summary>
    public ProxyDefinition? Result { get; private set; }

    /// <summary>是否为修改模式（true = 修改已有隧道；保存时调用方先删后加）</summary>
    public bool IsEdit { get; }

    public TunnelEditorWindow() : this(null) { }

    public TunnelEditorWindow(ProxyDefinition? existing)
    {
        InitializeComponent();
        IsEdit = existing is not null;

        if (existing is not null)
        {
            TitleText.Text = "修改隧道";
            Title = "修改隧道";
            NameBox.Text = existing.ProxyId;
            NameBox.IsReadOnly = true;   // 名称作为标识，修改时锁定
            TypeBox.SelectedItem = TypeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(c => (c.Content?.ToString() ?? "") == existing.LinkType.ToString().ToLowerInvariant())
                ?? TypeBox.Items[0];
            LocalIpBox.Text = existing.LocalIp;
            LocalPortBox.Text = existing.LocalPort.ToString();
            RemotePortBox.Text = existing.RemotePort?.ToString() ?? "";
            DomainBox.Text = existing.Domain ?? "";
            GroupBox.Text = existing.Group ?? "";
        }
        else
        {
            TypeBox.SelectedItem = TypeBox.Items[0];
            LocalIpBox.Text = "127.0.0.1";
        }

        OkBtn.Click += OnOk;
        CancelBtn.Click += (_, _) => Close();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
            return;
        if (!int.TryParse((LocalPortBox.Text ?? "").Trim(), out var lp) || lp is < 1 or > 65535)
            return;

        var typeText = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "tcp";
        var linkType = typeText.ToLowerInvariant() switch
        {
            "udp" => LinkType.Udp,
            "http" => LinkType.Http,
            "https" => LinkType.Https,
            _ => LinkType.Tcp,
        };
        int? rp = int.TryParse((RemotePortBox.Text ?? "").Trim(), out var r) && r is >= 1 and <= 65535 ? r : null;
        var domain = string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim();
        var group = string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim();

        Result = new ProxyDefinition(name, linkType, (LocalIpBox.Text ?? "").Trim(), lp, rp, domain, null, group);
        Close();
    }
}
