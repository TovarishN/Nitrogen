using System.IO;
using Nitrogen.Grammar;

namespace Nitrogen.Generator;

/// <summary>Normalizes only supplied inputs. Resolving a dependency never reads the filesystem.</summary>
internal static class RegisteredGrammarInputs
{
    internal sealed record Input(string Path, string Text, string Namespace, string? ExplicitNamespace);
    internal sealed record Result(EquatableArray<GrammarInput> Inputs, GrammarCompileResult Compilation);

    internal static Result Compile(IEnumerable<Input> supplied)
    {
        var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = supplied.GroupBy(i => Path.GetFullPath(i.Path), comparer).ToDictionary(g => g.Key, g => g.First(), comparer);
        var selected = new Dictionary<string, GrammarInput>(comparer);
        var diagnostics = new List<CompiledDiagnostic>();
        foreach (var input in files.Values.OrderByDescending(i => DocumentSignatures.Read(i.Text).Signatures.Any(s => s.Mode == SignatureMode.Grammar)))
        {
            if (selected.ContainsKey(Path.GetFullPath(input.Path))) continue;
            var header = DocumentSignatures.Read(input.Text);
            if (input.Path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase) || header.Signatures.Any(s => s.Mode == SignatureMode.Grammar))
                Visit(input, null, new HashSet<string>(comparer));
        }
        var compilation = diagnostics.Any(d => d.Diagnostic.Severity == GrammarSeverity.Error)
            ? new GrammarCompileResult(default, default) : GrammarCompiler.Compile(selected.Values);
        return new(selected.Values.ToArray(), compilation with { Diagnostics = diagnostics.Concat(compilation.Diagnostics).ToArray() });

        void Visit(Input input, string? inheritedNamespace, HashSet<string> active)
        {
            string path = Path.GetFullPath(input.Path);
            if (active.Contains(path)) { Error(input, "cyclic grammar dependency"); return; }
            if (active.Count >= 128) { Error(input, "grammar dependency depth exceeds 128"); return; }
            var header = DocumentSignatures.Read(input.Text);
            diagnostics.AddRange(header.Diagnostics.Select(d => new CompiledDiagnostic(input.Path, d)));
            var parsed = GrammarParser.Parse(header.MaskedText);
            var declaration = parsed.File?.Languages.FirstOrDefault();
            string ns = declaration?.Namespace ?? (declaration is not null ? declaration.Name + ".Syntax" : inheritedNamespace ?? input.Namespace);
            if (!string.IsNullOrWhiteSpace(input.ExplicitNamespace) && input.ExplicitNamespace != ns)
                Error(input, $"Namespace metadata '{input.ExplicitNamespace}' conflicts with language namespace '{ns}'");
            if (selected.TryGetValue(path, out var previous))
            {
                if (previous.Namespace != ns) Error(input, "grammar dependency is selected with conflicting namespaces");
                return;
            }
            selected[path] = new(input.Path, header.MaskedText, ns);
            active.Add(path);
            foreach (string dependency in declaration?.Grammars ?? default)
            {
                try
                {
                    if (Uri.TryCreate(dependency, UriKind.Absolute, out var uri) && !uri.IsFile)
                    { Error(input, "grammar dependency must be a local registered input"); continue; }
                    string dependencyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, dependency));
                    if (files.TryGetValue(dependencyPath, out var found)) Visit(found, ns, active);
                    else Error(input, $"grammar dependency '{dependency}' is not a registered AdditionalFile");
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
                { Error(input, error.Message); }
            }
            active.Remove(path);
        }
        void Error(Input input, string message) => diagnostics.Add(new(input.Path, new("NGR0402", GrammarSeverity.Error, message, default)));
    }
}
