namespace Aeterni.Tunnel.Launcher.Models;

/// <summary>
/// 实例配置（server.toml，位于 servers/&lt;实例名&gt;/server.toml）。
/// 序列化格式与模板同构，实例字段覆盖模板默认。
/// </summary>
public sealed class InstanceConfig
{
    /// <summary>实例名（= 目录名，唯一）</summary>
    public string Name { get; set; } = "";

    /// <summary>模板 id（GameTemplateStore 解析）</summary>
    public string TemplateId { get; set; } = "";

    /// <summary>游戏端口（本地监听，隧道 localPort 联动）</summary>
    public int Port { get; set; } = 25565;

    /// <summary>分配内存 MB（覆盖模板默认）</summary>
    public int? MemoryMb { get; set; }

    /// <summary>Java 版本覆盖（模板声明了则不必填）</summary>
    public string? JavaVersion { get; set; }

    /// <summary>Java 可执行文件绝对路径覆盖（优先于版本解析）</summary>
    public string? JavaPath { get; set; }

    /// <summary>实例级进程环境变量（合并进模板默认，进程级注入）</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>启动参数追加（追加到模板命令后）</summary>
    public string? ExtraArgs { get; set; }

    /// <summary>崩溃自动重启（true=启用，防循环熔断在服务内）</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>关联隧道公网端口（0=未关联）</summary>
    public int TunnelRemotePort { get; set; }

    /// <summary>启动次数（崩溃熔断用，重置时清零）</summary>
    public int LaunchCount { get; set; }
}
