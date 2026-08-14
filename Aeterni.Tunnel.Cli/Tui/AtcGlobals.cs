namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// Roslyn 脚本全局对象：脚本里通过 `atc` 访问 ATC 上下文（VS C# Interactive 风格）。
/// 例：atc.Tunnel.Add("mc", "tcp", "25565", "6071") / atc.TunnelCount / atc.Status / atc.Quit()
/// </summary>
public sealed class AtcGlobals
{
    /// <summary>ATC 上下文（脚本内可直接使用 atc 名称）</summary>
    public AtcContext atc { get; }

    public AtcGlobals(AtcContext context) => atc = context;
}
