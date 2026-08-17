namespace Aeterni.Tunnel.Launcher.Models;

/// <summary>
/// 游戏模板元数据（内嵌 Templates/*.toml）。
/// 模板声明：运行方式（jar/script/command）、Java 版本、默认 env、启动参数。
/// </summary>
public sealed class GameTemplate
{
    /// <summary>模板 id（如 paper-1.21）</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名（如 Minecraft Paper 1.21）</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>运行方式：jar（Java 模式）| script（run.bat）| command</summary>
    public string StartMode { get; set; } = "jar";

    /// <summary>声明所需 Java 版本（jar 模式必填，管理器解析 JDK）</summary>
    public string? JavaVersion { get; set; }

    /// <summary>jar 文件名（相对 run/ 目录）</summary>
    public string? Jar { get; set; }

    /// <summary>jar 下载地址（空=用户手动放置）</summary>
    public string? JarUrl { get; set; }

    /// <summary>脚本文件名（script 模式，相对 run/ 目录）</summary>
    public string? Script { get; set; }

    /// <summary>原始命令（command 模式，支持 {port} 变量）</summary>
    public string? Command { get; set; }

    /// <summary>内存下限 MB</summary>
    public int MemoryMinMb { get; set; } = 1024;

    /// <summary>内存上限 MB</summary>
    public int MemoryMaxMb { get; set; } = 8192;

    /// <summary>默认进程环境变量（实例可覆盖）</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>启动参数模板（jar 模式）：{java} {memory} {jar} 变量替换</summary>
    public string LaunchArgs { get; set; } = "-Xmx{memory}M -jar {jar} nogui";

    /// <summary>停止指令（控制台输入即优雅停止，空=直接 kill）</summary>
    public string StopCommand { get; set; } = "stop";

    /// <summary>首次启动要自动写入的文件（如 eula.txt=true）</summary>
    public Dictionary<string, string> FirstRunFiles { get; set; } = new();
}
