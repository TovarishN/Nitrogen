namespace Nitrogen;

public sealed unsafe partial class Language
{
    /// <summary>Compiles a read-only syntax pattern. The result owns its parsed pattern storage.</summary>
    public PatternCompilation CompilePattern(string category, string text, IReadOnlyDictionary<string, QuoteHole> captures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(captures);
        var copied = captures.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var diagnostics = new List<QuoteDiagnostic>();
        foreach (var name in copied.Values.Select(hole => hole.Category).Append(category).Distinct(StringComparer.Ordinal))
            if (!Modules.Any(module => module.QuoteCategoryNames.Contains(name, StringComparer.Ordinal)))
                diagnostics.Add(new("NQP0001", default, $"'{name}' has no generated quotation category metadata."));
        if (diagnostics.Count != 0) return new(null, category, copied, diagnostics.ToArray());
        ParseResult parsed;
        try { parsed = ParseTemplate(text, category, copied); }
        catch (ArgumentException error) { return new(null, category, copied, [new("NQP0001", default, error.Message)]); }
        if (!parsed.Success || parsed.HasErrors)
        {
            foreach (var diagnostic in parsed.Diagnostics)
                diagnostics.Add(new("NQP0001", diagnostic.Span, parsed.FormatMessage(diagnostic)));
            if (diagnostics.Count == 0) diagnostics.Add(new("NQP0001", default, "The pattern did not parse."));
        }
        else
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var invalidLists = new HashSet<int>();
            for (int node = 0; node < parsed.Tree.NodeCount; node++)
            {
                int kind = parsed.Tree.Kind(node);
                if (kind is not (SyntaxKinds.Hole or SyntaxKinds.ListHole)) continue;
                string name = parsed.Tree.GetText(node)[(kind == SyntaxKinds.Hole ? 1 : 3)..].ToString();
                used.Add(name);
                if (kind == SyntaxKinds.ListHole)
                {
                    int parent = parsed.Tree.TemplateList(node);
                    if (!parsed.Tree.TemplateLists.TryGetValue(parent, out var shape) ||
                        shape.Category != copied[name].Category ||
                        Enumerable.Range(0, ListSlot.Count(parsed.Tree, parent)).Count(index =>
                            parsed.Tree.Kind(ListSlot.Child(parsed.Tree, parent, index)) == SyntaxKinds.ListHole) != 1)
                        if (invalidLists.Add(parent))
                            diagnostics.Add(new("NQP0003", parsed.Tree.Span(node), "A physical list requires one capture at a supported item site.", name));
                }
            }
            foreach (string name in copied.Keys)
                if (!used.Contains(name)) diagnostics.Add(new("NQP0002", default, "A declared capture must occur in the parsed pattern.", name));
        }
        if (diagnostics.Count == 0) return new(parsed, category, copied, []);
        parsed.Dispose();
        return new(null, category, copied, diagnostics.ToArray());
    }
}
