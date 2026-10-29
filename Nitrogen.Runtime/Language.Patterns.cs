namespace Nitrogen;

public sealed unsafe partial class Language
{
    readonly object _namedPatternLock = new();
    NamedPatternTable? _namedPatternTable;

    /// <summary>This language's named patterns, compiled on first use and kept for the language's lifetime.</summary>
    internal NamedPatternTable NamedPatternTable
    {
        get
        {
            if (Volatile.Read(ref _namedPatternTable) is { } table) return table;
            lock (_namedPatternLock) return _namedPatternTable ??= NamedPatternTable.Build(this);
        }
    }

    /// <summary>Compiles a read-only syntax pattern. The result owns its parsed pattern storage.</summary>
    public PatternCompilation CompilePattern(string category, string text, IReadOnlyDictionary<string, QuoteHole> captures)
        => CompilePatternCore(category, text, captures, NamedPatternTable);

    internal PatternCompilation CompilePatternCore(string category, string text, IReadOnlyDictionary<string, QuoteHole> captures,
        NamedPatternTable namedPatterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(captures);
        var copied = captures.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var diagnostics = new List<QuoteDiagnostic>();
        var references = new Dictionary<string, NamedPatternDefinition>(StringComparer.Ordinal);
        if (copied.ContainsKey("_")) diagnostics.Add(new("NQP0002", default, "The pattern capture name '_' is reserved for discards.", "_"));
        foreach (var (name, hole) in copied.ToArray())
        {
            if (hole.PatternName is not { } patternName) continue;
            if (namedPatterns.Find(patternName) is not { } definition)
            {
                diagnostics.Add(new("NQP0007", default, $"'{patternName}' is not a named pattern of this language.", name));
                continue;
            }
            if (hole.Descendant && hole.IsList)
                diagnostics.Add(new("NQP0009", default, "A descendant search captures one node, not a list.", name));
            copied[name] = hole with { Category = definition.Category };
            references[name] = definition;
        }
        if (diagnostics.Count != 0) return new(null, category, copied, diagnostics.ToArray());
        foreach (var name in copied.Values.Select(hole => hole.Category).Append(category).Distinct(StringComparer.Ordinal))
            if (!Modules.Any(module => module.QuoteCategoryNames.Contains(name, StringComparer.Ordinal)))
                diagnostics.Add(new("NQP0001", default, $"'{name}' has no generated quotation category metadata."));
        if (diagnostics.Count != 0) return new(null, category, copied, diagnostics.ToArray());
        ParseResult parsed;
        try { parsed = ParsePattern(text, category, copied); }
        catch (ArgumentException error) { return new(null, category, copied, [new("NQP0001", default, error.Message)]); }
        if (!parsed.Success || parsed.HasErrors)
        {
            foreach (var diagnostic in parsed.Diagnostics)
                diagnostics.Add(new("NQP0001", diagnostic.Span, parsed.FormatMessage(diagnostic)));
            if (diagnostics.Count == 0) diagnostics.Add(new("NQP0001", default, "The pattern did not parse."));
        }
        else
        {
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            var invalidLists = new HashSet<int>();
            for (int node = 0; node < parsed.Tree.NodeCount; node++)
            {
                int kind = parsed.Tree.Kind(node);
                if (kind is not (SyntaxKinds.Hole or SyntaxKinds.ListHole)) continue;
                string name = parsed.Tree.GetText(node)[(kind == SyntaxKinds.Hole ? 1 : 3)..].ToString();
                bool discard = parsed.Tree.PatternHoleSites.TryGetValue(node, out var site);
                if (!discard) used[name] = used.GetValueOrDefault(name) + 1;
                if (kind == SyntaxKinds.ListHole)
                {
                    int parent = parsed.Tree.TemplateList(node);
                    if (!parsed.Tree.TemplateLists.TryGetValue(parent, out var shape) ||
                        shape.Category != (discard ? site!.Category : copied[name].Category))
                        if (invalidLists.Add(parent))
                            diagnostics.Add(new("NQP0003", parsed.Tree.Span(node), "A list segment requires a supported item site and matching category.", name));
                }
            }
            foreach (string name in copied.Keys)
                if (!used.ContainsKey(name)) diagnostics.Add(new("NQP0002", default, "A declared capture must occur in the parsed pattern.", name));
                else if (copied[name].Descendant && used[name] > 1)
                    diagnostics.Add(new("NQP0009", default, "A descendant capture may occur only once in a pattern.", name));
        }
        if (diagnostics.Count == 0) return new(parsed, category, copied, [], references);
        parsed.Dispose();
        return new(null, category, copied, diagnostics.ToArray());
    }
}
