using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// REPL IntelliSense 引擎（CSharpRepl 同款方案，自研实现）：
/// AdhocWorkspace + CompletionService + SignatureHelpService，
/// 对"历史代码 + 当前输入"实时求补全候选与方法签名。
/// </summary>
public sealed class ReplIntelliSense : IDisposable
{
    private readonly AdhocWorkspace _workspace = new();
    private readonly DocumentId _documentId;
    private Document _document;
    private string _lastCode = "";

    // 用户代码包装（脚本编译模型）：atc 作为静态字段（AtcContext 类型，与脚本 globals 一致），补全在 __Main 方法体内进行
    private const string WrapPrefix =
        "using System;\nusing System.Linq;\nusing System.Collections.Generic;\n" +
        "using Aeterni.Tunnel.Cli.Tui;\n" +
        "public static class __AtcProgram\n{\n    public static AtcContext atc;\n    public static void __Main()\n    {\n";
    private const string WrapSuffix = "\n    }\n}\n";

    public ReplIntelliSense()
    {
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(),
                "repl", "repl", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(LoadReferences());
        _workspace.AddProject(projectInfo);
        _documentId = DocumentId.CreateNewId(projectId);
        _document = _workspace.AddDocument(projectId, "repl.cs", SourceText.From(""));
    }

    /// <summary>加载引用：运行时已加载程序集（开发/自解压单文件 Location 均有效，比 TPA 可靠）</summary>
    private static IEnumerable<MetadataReference> LoadReferences()
    {
        var refs = new List<MetadataReference>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location) || !File.Exists(asm.Location))
                continue;
            try { refs.Add(MetadataReference.CreateFromFile(asm.Location)); }
            catch { /* 跳过无法读取的引用 */ }
        }
        return refs;
    }

    /// <summary>获取光标处的补全候选（用户代码，内部包装为类方法）</summary>
    public async Task<CompletionList?> GetCompletionsAsync(string code, int caretOffset, CancellationToken ct)
    {
        var document = GetDocument(Wrap(code));
        var service = CompletionService.GetService(document);
        if (service is null)
            return null;
        return await service.GetCompletionsAsync(document, WrapPrefix.Length + caretOffset, cancellationToken: ct);
    }

    /// <summary>获取补全项的签名描述（VS 补全下方签名行，等价于 CSharpRepl 的签名提示）</summary>
    public async Task<string?> GetDescriptionAsync(string code, CompletionItem item, CancellationToken ct)
    {
        var document = GetDocument(Wrap(code));
        var service = CompletionService.GetService(document);
        if (service is null)
            return null;
        var description = await service.GetDescriptionAsync(document, item, cancellationToken: ct);
        if (description is null || description.Text.Length == 0)
            return null;
        return string.Join(" ", description.Text);
    }

    private static string Wrap(string code) => WrapPrefix + code + WrapSuffix;

    private Document GetDocument(string code)
    {
        if (code != _lastCode)
        {
            _document = _document.WithText(SourceText.From(code));
            _lastCode = code;
        }
        return _document;
    }

    public void Dispose() => _workspace.Dispose();
}
