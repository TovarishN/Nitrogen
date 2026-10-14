using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

/// <summary>Validates composed quote bodies using generated syntax only, never configured C# helpers.</summary>
public static class QuoteHelperValidation
{
    public static IReadOnlyList<WorkspaceDiagnostic> Validate(IEnumerable<GrammarInput> inputs)
    {
        var files = inputs.ToArray();
        if (!files.Any(input => GrammarParser.Parse(input.Text).File?.Modules.Any(m => m.Helpers.Count > 0) == true))
            return [];
        var emitted = GrammarCompiler.CompileQuotationSyntax(files);
        if (emitted.HasErrors)
            return emitted.Diagnostics.Select(d => new WorkspaceDiagnostic(d.Path, 1, 1, d.Diagnostic.Code, d.Diagnostic.Message, true)).ToArray();
        var references = GrammarWorkspace.QuoteReferences;
        var compilation = CSharpCompilation.Create("NitrogenQuoteValidation" + Guid.NewGuid().ToString("N"),
            emitted.Sources.Select(source => CSharpSyntaxTree.ParseText(source.Code, new CSharpParseOptions(LanguageVersion.Latest))),
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        if (!result.Success)
            return result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => new WorkspaceDiagnostic("", 1, 1, d.Id, d.GetMessage(), true)).ToArray();
        image.Position = 0;
        var context = new WorkspaceLoadContext();
        try
        {
            var assembly = context.LoadFromStream(image);
            var builder = new LanguageBuilder();
            foreach (var type in assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SyntaxModule)) && !t.IsAbstract))
                builder.Add((SyntaxModule)type.GetField("Instance")!.GetValue(null)!);
            builder.TryBuild(out _, out var diagnostics);
            return diagnostics.Select(diagnostic =>
            {
                string path = diagnostic.Location?.Path ?? "";
                int start = diagnostic.Location?.Span.Start ?? 0;
                string text = files.FirstOrDefault(f => f.Path == path)?.Text ?? "";
                int line = 1, column = 1;
                for (int i = 0; i < Math.Min(start, text.Length); i++)
                    if (text[i] == '\n') { line++; column = 1; }
                    else column++;
                return new WorkspaceDiagnostic(path, line, column, diagnostic.Code, diagnostic.Message, true);
            }).ToArray();
        }
        finally { context.Unload(); }
    }
}
