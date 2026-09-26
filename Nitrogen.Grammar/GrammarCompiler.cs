namespace Nitrogen.Grammar;

/// <param name="Namespace">The C# namespace the file's modules are generated into.</param>
public sealed record GrammarInput(string Path, string Text, string Namespace);

public sealed record CompiledDiagnostic(string Path, GrammarDiagnostic Diagnostic);

public sealed record GeneratedSource(string HintName, string Code);

public sealed record GrammarCompileResult(EquatableArray<CompiledDiagnostic> Diagnostics, EquatableArray<GeneratedSource> Sources)
{
    public bool HasErrors => Diagnostics.Any(d => d.Diagnostic.Severity == GrammarSeverity.Error);
}

/// <summary>
/// The whole pipeline: parse every file, validate all modules together, run emit-time checks, then
/// emit one source per module. Any error stops before emission; warnings are returned with the sources.
/// </summary>
public static class GrammarCompiler
{
    public static GrammarCompileResult Compile(IEnumerable<GrammarInput> inputs)
    {
        var diagnostics = new List<CompiledDiagnostic>();
        var modules = new List<ModuleDecl>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var moduleInputs = new Dictionary<string, GrammarInput>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            var parsed = GrammarParser.Parse(input.Text);
            foreach (var diagnostic in parsed.Diagnostics) diagnostics.Add(new CompiledDiagnostic(input.Path, diagnostic));
            if (parsed.File is null) continue;
            foreach (var module in parsed.File.Modules)
            {
                modules.Add(module);
                if (paths.ContainsKey(module.Name)) continue;
                paths[module.Name] = input.Path;
                moduleInputs[module.Name] = input;
                namespaces[module.Name] = input.Namespace;
            }
        }

        string PathOf(GrammarDiagnostic diagnostic) =>
            diagnostic.Module is not null && paths.TryGetValue(diagnostic.Module, out string? path) ? path : "";

        foreach (var diagnostic in GrammarValidator.Validate(modules))
            diagnostics.Add(new CompiledDiagnostic(PathOf(diagnostic), diagnostic));
        if (HasErrors(diagnostics)) return new GrammarCompileResult(diagnostics.ToArray(), default);

        var model = new EmitModel(new GrammarAnalysis(modules), namespaces, moduleInputs);
        foreach (var diagnostic in model.Check())
            diagnostics.Add(new CompiledDiagnostic(PathOf(diagnostic), diagnostic));
        if (HasErrors(diagnostics)) return new GrammarCompileResult(diagnostics.ToArray(), default);

        var sources = model.Modules
            .Select(info => new GeneratedSource(info.Module.Name + ".g.cs", ModuleWriter.Write(model, info)))
            .ToArray();
        return new GrammarCompileResult(diagnostics.ToArray(), sources);
    }

    static bool HasErrors(List<CompiledDiagnostic> diagnostics) =>
        diagnostics.Any(d => d.Diagnostic.Severity == GrammarSeverity.Error);
}
