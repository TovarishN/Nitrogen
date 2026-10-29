namespace Nitrogen;

/// <summary>A grammar-declared named pattern: ordered alternatives over one syntax category.</summary>
/// <param name="Name">The qualified name, <c>Module.Name</c>.</param>
public sealed record NamedPatternSpec(string Name, string Category, IReadOnlyList<NamedPatternAlternative> Alternatives,
    QuoteHelperSource Source);

/// <summary>One alternative of a named pattern. Its captures are private to the alternative.</summary>
public sealed record NamedPatternAlternative(string Text, IReadOnlyDictionary<string, QuoteHole> Captures, QuoteHelperSource Source);

/// <summary>A language-owned named pattern. Alternatives are assigned after every shell exists, so references may recurse.</summary>
internal sealed class NamedPatternDefinition(string name, string category)
{
    internal string Name { get; } = name;
    internal string Category { get; } = category;
    internal QuotePattern[] Alternatives { get; set; } = [];
    internal bool Valid { get; set; }
}

internal sealed record NamedPatternError(string Pattern, QuoteHelperSource Source, QuoteDiagnostic Diagnostic);

/// <summary>Every named pattern of a language, compiled once and kept for the language's lifetime.</summary>
internal sealed class NamedPatternTable
{
    readonly Dictionary<string, NamedPatternDefinition> _definitions = new(StringComparer.Ordinal);
    readonly List<PatternCompilation> _storage = [];
    readonly List<NamedPatternError> _errors = [];

    internal IReadOnlyList<NamedPatternError> Errors => _errors;

    internal NamedPatternDefinition? Find(string qualifiedName) => _definitions.GetValueOrDefault(qualifiedName);

    internal static NamedPatternTable Build(Language language)
    {
        var table = new NamedPatternTable();
        var specs = new List<NamedPatternSpec>();
        foreach (var spec in language.Modules.SelectMany(module => module.NamedPatterns))
        {
            if (table._definitions.TryAdd(spec.Name, new(spec.Name, spec.Category))) specs.Add(spec);
            else table._errors.Add(new(spec.Name, spec.Source, new("NQP0001", default, $"Duplicate named pattern '{spec.Name}'.")));
        }
        var edges = new Dictionary<string, List<(string Target, QuoteHelperSource Source)>>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var definition = table._definitions[spec.Name];
            var alternatives = new List<QuotePattern>();
            var targets = edges[spec.Name] = [];
            foreach (var alternative in spec.Alternatives)
            {
                var compiled = language.CompilePatternCore(spec.Category, alternative.Text, alternative.Captures, table);
                table._storage.Add(compiled);
                foreach (var diagnostic in compiled.Diagnostics) table._errors.Add(new(spec.Name, alternative.Source, diagnostic));
                if (compiled.Pattern is not { } pattern) continue;
                alternatives.Add(pattern);
                if (pattern.RootReference is { } target) targets.Add((target.Name, alternative.Source));
            }
            definition.Alternatives = alternatives.ToArray();
            definition.Valid = alternatives.Count == spec.Alternatives.Count && spec.Alternatives.Count > 0;
        }
        table.ReportCycles(specs, edges);
        return table;
    }

    // A non-consuming reference keeps matching the same node; a cycle of them would never descend.
    void ReportCycles(List<NamedPatternSpec> specs, Dictionary<string, List<(string Target, QuoteHelperSource Source)>> edges)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the path, 2 = finished
        var path = new List<string>();
        foreach (var spec in specs) Visit(spec.Name);

        void Visit(string name)
        {
            if (state.GetValueOrDefault(name) == 2) return;
            state[name] = 1;
            path.Add(name);
            foreach (var (target, source) in edges.GetValueOrDefault(name) ?? [])
            {
                if (state.GetValueOrDefault(target) == 1)
                {
                    var cycle = path.Skip(path.IndexOf(target)).Append(target);
                    _errors.Add(new(name, source, new("NQP0008", default,
                        $"Named patterns refer to each other without consuming syntax: {string.Join(" -> ", cycle)}.")));
                }
                else if (edges.ContainsKey(target)) Visit(target);
            }
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
        }
    }
}
