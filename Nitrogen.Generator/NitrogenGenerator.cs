using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Nitrogen.Grammar;

namespace Nitrogen.Generator;

/// <summary>
/// Compiles every <c>*.ngr</c> AdditionalFile into C#. The namespace comes from the file's
/// <c>Namespace</c> metadata, else the project's <c>RootNamespace</c>. The consuming project needs:
/// <code>
/// &lt;CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="Namespace" /&gt;
/// &lt;CompilerVisibleProperty Include="RootNamespace" /&gt;
/// </code>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class NitrogenGenerator : IIncrementalGenerator
{
    sealed record Compiled(EquatableArray<GrammarInput> Inputs, GrammarCompileResult Result);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var compiled = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellation) => ToInput(pair.Left, pair.Right, cancellation))
            .Collect()
            .Select(static (inputs, _) => new Compiled(inputs.ToArray(), GrammarCompiler.Compile(inputs)));

        context.RegisterSourceOutput(compiled, static (output, result) => Emit(output, result));
    }

    static GrammarInput ToInput(AdditionalText file, AnalyzerConfigOptionsProvider options, CancellationToken cancellation)
    {
        string text = file.GetText(cancellation)?.ToString() ?? "";
        options.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles.Namespace", out string? ns);
        if (string.IsNullOrWhiteSpace(ns)) options.GlobalOptions.TryGetValue("build_property.RootNamespace", out ns);
        return new GrammarInput(file.Path, text, string.IsNullOrWhiteSpace(ns) ? "Nitrogen.Generated" : ns!.Trim());
    }

    static void Emit(SourceProductionContext output, Compiled compiled)
    {
        foreach (var diagnostic in compiled.Result.Diagnostics)
            output.ReportDiagnostic(ToRoslyn(diagnostic, compiled.Inputs));
        foreach (var source in compiled.Result.Sources)
            output.AddSource(source.HintName, SourceText.From(source.Code, Encoding.UTF8));
    }

    static Diagnostic ToRoslyn(CompiledDiagnostic compiled, EquatableArray<GrammarInput> inputs)
    {
        var grammar = compiled.Diagnostic;
        var location = Location.None;
        var input = inputs.FirstOrDefault(i => i.Path == compiled.Path);
        if (input is not null)
        {
            var text = SourceText.From(input.Text);
            int start = Math.Min(grammar.Span.Start, text.Length);
            int end = Math.Min(grammar.Span.End, text.Length);
            var span = TextSpan.FromBounds(start, end);
            location = Location.Create(compiled.Path, span, text.Lines.GetLinePositionSpan(span));
        }
        var severity = grammar.Severity == GrammarSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
        var descriptor = new DiagnosticDescriptor(grammar.Code, "Nitrogen grammar", "{0}", "Nitrogen", severity, isEnabledByDefault: true);
        return Diagnostic.Create(descriptor, location, grammar.Message);
    }
}
