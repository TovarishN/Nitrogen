using Nitrogen.Grammar;

namespace Nitrogen.Workspace.Admission;

/// <summary>Rejects all grammar-model fields that admit author-supplied C#.</summary>
public static class DeclarativeGrammarPolicy
{
    public static IReadOnlyList<AdmissionDiagnostic> Validate(ModulePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var diagnostics = new List<AdmissionDiagnostic>();
        foreach (var (path, source) in package.Grammars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var parsed = GrammarParser.Parse(source);
            foreach (var error in parsed.Diagnostics)
            {
                var (line, column) = Location(source, error.Span.Start);
                diagnostics.Add(new AdmissionDiagnostic(error.Code, "grammar", path, line, column, error.Message));
            }
            if (parsed.File is null) continue;

            void Reject(GrammarSpan span)
            {
                var (line, column) = Location(source, span.Start);
                diagnostics.Add(new AdmissionDiagnostic("NA0002", "policy", path, line, column,
                    "authored C# is not allowed in a declarative module"));
            }

            foreach (var module in parsed.File.Modules)
            {
                foreach (var property in module.SymbolProperties) Reject(property.Span);
                foreach (var rule in module.Rules)
                {
                    if (rule is SyntaxRule { Semantics: { } block }) Reject(block.Span);
                    if (rule is not ExtensibleRule extensible) continue;
                    foreach (var property in extensible.Properties) Reject(property.Span);
                    foreach (var alternative in extensible.Alternatives)
                        if (alternative.Semantics is { } body) Reject(body.Span);
                }
                foreach (var extension in module.Extends)
                    foreach (var alternative in extension.Alternatives)
                        if (alternative.Semantics is { } body) Reject(body.Span);
            }
        }
        return diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line)
            .ThenBy(d => d.Column).ThenBy(d => d.Code, StringComparer.Ordinal).ToArray();
    }

    static (int Line, int Column) Location(string source, int offset)
    {
        int line = 1, column = 1;
        for (int i = 0; i < offset && i < source.Length; i++)
        {
            if (source[i] == '\n') { line++; column = 1; }
            else column++;
        }
        return (line, column);
    }
}
