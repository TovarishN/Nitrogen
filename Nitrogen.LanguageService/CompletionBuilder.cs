using Nitrogen.Binding;
using Nitrogen.Semantics;

namespace Nitrogen.LanguageService;

/// <summary>
/// Completion (issue 238). Names: at a reference's name (the outermost one, so a dotted prefix is
/// whole) or at a hole, the symbols visible there of the kinds the grammar allows, filtered by the
/// typed prefix. Keywords: the text before the word being typed, with a sentinel no grammar accepts,
/// is parsed fast; the word literals it expected there are offered. Names come first.
/// </summary>
internal static class CompletionBuilder
{
    const string Sentinel = "\u0001";

    public static List<CompletionItem> Build(Document document, Project project, FileSemantics? semantics, SemanticsInfo info, int offset)
    {
        string text = document.Text;
        var binding = project[document.Uri];
        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var containing = binding.References.Where(r => r.NameSpan.Start <= offset && offset <= r.NameSpan.End).ToList();
        int nameStart = offset, node = -1;
        IReadOnlyList<Symbol>? candidates = null;
        if (containing.Count > 0)
        {
            var outer = containing.MinBy(r => r.NameSpan.Start)!;
            nameStart = outer.NameSpan.Start;
            node = outer.Node;
            candidates = outer.Qualifier is not null
                ? project.CandidatesFor(outer)
                : project.VisibleAt(document.Uri, offset, containing.SelectMany(r => r.Kinds).Distinct().ToArray());
        }
        else if (binding.Holes.FirstOrDefault(h => Blank(text, h.Position, offset)) is { } hole)
        {
            node = hole.Node;
            candidates = project.CandidatesFor(hole);
        }
        else if (semantics is not null && info.Expected is { } expectedProperty && info.SymbolType is { } typeProperty
            && MissingAt(semantics.Tree, text, offset) is int missing && expectedProperty.Read(semantics, missing) is { } wanted)
        {
            // A missing expression (recovery inserted it) with an expected type: the visible names of that type (issue 239).
            node = missing;
            var kinds = binding.References.SelectMany(r => r.Kinds).Concat(binding.Declarations.Select(d => d.Kind)).Distinct().ToArray();
            candidates = project.VisibleAt(document.Uri, offset, kinds).Where(s => Equals(typeProperty.Read(semantics, s), wanted)).ToList();
        }

        if (candidates is not null)
        {
            string prefix = text[nameStart..offset];
            var replace = new DocumentRange(document.Lines.PositionOf(nameStart), document.Lines.PositionOf(offset));
            var names = candidates.Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(s.Name)).ToList();
            // Names whose type is the expected one come first (a stable sort keeps the rest in scope order).
            if (semantics is not null && node >= 0 && info.Expected?.Read(semantics, node) is { } expected && info.SymbolType is { } type)
                names = names.OrderBy(s => Equals(type.Read(semantics, s), expected) ? 0 : 1).ToList();
            foreach (var symbol in names)
                items.Add(new CompletionItem(symbol.Name, KindOf(document.Language.Presentation.StyleOf(symbol.Kind).Outline),
                    symbol.IsBuiltin ? $"{symbol.Kind} (built-in)" : symbol.Kind, replace));
        }

        int wordStart = offset;
        while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '_')) wordStart--;
        var (at, expectedItems) = document.Language.Language.Expected(text[..wordStart] + Sentinel, document.Start);
        if (at >= 0 && Blank(text, at, wordStart))
        {
            string word = text[wordStart..offset];
            var replace = new DocumentRange(document.Lines.PositionOf(wordStart), document.Lines.PositionOf(offset));
            foreach (var (item, isLiteral) in expectedItems)
                if (isLiteral && SemanticTokenBuilder.IsWord(item) && item.StartsWith(word, StringComparison.Ordinal) && seen.Add(item))
                    items.Add(new CompletionItem(item, CompletionKind.Keyword, "keyword", replace));
        }
        return items.Select((item, i) => item with { SortText = i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) }).ToList();
    }

    /// <summary>A Missing node with only whitespace between it and the cursor; null when none.</summary>
    static int? MissingAt(SyntaxTree tree, string text, int offset)
    {
        for (int node = 0; node < tree.NodeCount; node++)
            if ((tree.Flags(node) & NodeFlags.Missing) != 0 && Blank(text, tree.Span(node).Start, offset)) return node;
        return null;
    }

    /// <summary>True when only whitespace lies between two offsets, in either order.</summary>
    static bool Blank(string text, int a, int b)
    {
        int from = Math.Clamp(Math.Min(a, b), 0, text.Length), to = Math.Clamp(Math.Max(a, b), 0, text.Length);
        return string.IsNullOrWhiteSpace(text[from..to]);
    }

    static CompletionKind KindOf(OutlineKind outline) => outline switch
    {
        OutlineKind.Class or OutlineKind.Struct or OutlineKind.Interface => CompletionKind.Class,
        OutlineKind.Function => CompletionKind.Function,
        OutlineKind.Method => CompletionKind.Method,
        OutlineKind.Field => CompletionKind.Field,
        OutlineKind.Property => CompletionKind.Property,
        OutlineKind.EnumMember => CompletionKind.EnumMember,
        OutlineKind.Event => CompletionKind.Event,
        OutlineKind.Module or OutlineKind.Namespace => CompletionKind.Module,
        OutlineKind.Constant => CompletionKind.Constant,
        _ => CompletionKind.Variable,
    };
}
