namespace Aeterni.Tunnel.Engine.Config;

/// <summary>日志配置（server.toml [log] 段）</summary>
public sealed class LogConfig
{
    /// <summary>日志文件路径（空 = 不写文件）</summary>
    public string File { get; set; } = "";

    /// <summary>debug / info / warn / error</summary>
    public string Level { get; set; } = "info";

    /// <summary>单个日志文件大小上限（MB，超过滚动）</summary>
    public int MaxSizeMb { get; set; } = 10;
}
