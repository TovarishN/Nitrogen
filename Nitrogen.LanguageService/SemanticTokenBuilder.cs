using Nitrogen.Binding;
using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>
/// Semantic tokens from a document's tree and binding (issue 238). Leaves are visited in text order
/// (the tree is preorder). A leaf that starts a declared, or effectively referenced and resolved,
/// name becomes one token for the whole name. Then what the leaf lowers to (typed HIR, when the
/// language lowers): a word that spells an operation is a function, and a single token that lowers to
/// a value is coloured by its type. Other leaves are classified by shape: word literals are keywords,
/// other literals operators, number- and string-like token rules numbers and strings. Comments come
/// from the trivia. Every token is split per line.
/// </summary>
internal static class SemanticTokenBuilder
{
    public static List<SemanticToken> Build(Document document, Project project, IReadOnlyList<HirNode>? lowered = null,
        IEnumerable<AssistToken>? assisted = null)
    {
        var tree = document.Parsed.Tree;
        string text = document.Text;
        var presentation = document.Language.Presentation;
        var binding = project[document.Uri];

        // Name spans by start; a declaration wins over a reference on the same name.
        var names = new Dictionary<int, (int End, TokenType Type, TokenModifiers Modifiers)>();
        foreach (var reference in binding.References)
        {
            if (!project.IsEffective(reference)) continue;
            var resolved = project.Resolve(reference);
            if (resolved.Count == 0) continue;
            var symbol = resolved[0];
            names[reference.NameSpan.Start] = (reference.NameSpan.End, presentation.StyleOf(symbol.Kind).Token,
                symbol.IsBuiltin ? TokenModifiers.DefaultLibrary : TokenModifiers.None);
        }
        foreach (var declaration in binding.Declarations)
            names[declaration.NameSpan.Start] = (declaration.NameSpan.End, presentation.StyleOf(declaration.Kind).Token, TokenModifiers.Declaration);

        var hir = Lowered(document, lowered ?? []);
        foreach (var token in assisted ?? []) hir[token.Span.Start] = (token.Span.End, token.Type); // the language knows best

        var tokens = new List<SemanticToken>();
        int covered = 0; // text before this offset is already coloured
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.ChildCount(node) != 0) continue;
            int kind = tree.Kind(node);
            if (kind is SyntaxKinds.Empty or SyntaxKinds.List || (tree.Flags(node) & NodeFlags.Missing) != 0) continue;
            var span = tree.Span(node);
            if (span.Length == 0 || span.Start < covered) continue;
            if (names.TryGetValue(span.Start, out var name))
            {
                Add(tokens, document.Lines, span.Start, name.End, name.Type, name.Modifiers);
                covered = name.End;
                continue;
            }
            if (hir.TryGetValue(span.Start, out var meaning) && meaning.End == span.End)
            {
                Add(tokens, document.Lines, span.Start, span.End, meaning.Type, TokenModifiers.None);
                covered = span.End;
                continue;
            }
            if (Classify(tree, kind, text, span) is { } type)
            {
                Add(tokens, document.Lines, span.Start, span.End, type, TokenModifiers.None);
                covered = span.End;
            }
        }
        foreach (var trivia in tree.Trivia) Comments(tokens, document.Lines, text, trivia);
        tokens.Sort((a, b) => a.Start.Line != b.Start.Line ? a.Start.Line.CompareTo(b.Start.Line) : a.Start.Character.CompareTo(b.Start.Character));
        return tokens;
    }

    /// <summary>
    /// Leaf spans by start → their colour from the lowered HIR. An operation's own words (outside its
    /// arguments) are functions; a node lowered from one token is a value of its type. Outer nodes win.
    /// </summary>
    static Dictionary<int, (int End, TokenType Type)> Lowered(Document document, IReadOnlyList<HirNode> roots)
    {
        var meanings = new Dictionary<int, (int End, TokenType Type)>();
        if (roots.Count == 0) return meanings;
        var tree = document.Parsed.Tree;
        string text = document.Text;
        var leaves = new List<(TextSpan Span, bool Word, bool Literal)>();
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.ChildCount(node) != 0 || (tree.Flags(node) & NodeFlags.Missing) != 0) continue;
            int kind = tree.Kind(node);
            var span = tree.Span(node);
            if (kind is SyntaxKinds.Empty or SyntaxKinds.List || span.Length == 0) continue;
            leaves.Add((span, IsWord(text.AsSpan(span.Start, span.Length)), kind == SyntaxKinds.Literal));
        }
        leaves.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));

        IEnumerable<(TextSpan Span, bool Word, bool Literal)> Within(TextSpan outer)
        {
            int low = 0, high = leaves.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (leaves[mid].Span.Start < outer.Start) low = mid + 1; else high = mid;
            }
            for (int i = low; i < leaves.Count && leaves[i].Span.End <= outer.End; i++) yield return leaves[i];
        }

        void Mark(TextSpan span, TokenType type) => meanings.TryAdd(span.Start, (span.End, type));

        IEnumerable<TextSpan> Here(HirNode node) => node.Origins.Where(o => o.Path == document.Uri).Select(o => o.Span);

        void Visit(HirNode node)
        {
            foreach (var span in Here(node))
            {
                var inside = Within(span).ToList();
                if (inside.Count == 1 && !inside[0].Literal && node is not HirSymbolRef)
                    Mark(inside[0].Span, document.Language.Presentation.TypeStyle(node.Type) ?? ValueToken(node.Type));
                else if (node is HirOperation operation)
                {
                    var arguments = operation.Arguments.SelectMany(Here).ToList();
                    foreach (var leaf in inside)
                        if (leaf.Literal && leaf.Word && !arguments.Any(a => a.Start <= leaf.Span.Start && leaf.Span.End <= a.End))
                            Mark(leaf.Span, TokenType.Function);
                }
            }
            switch (node)
            {
                case HirOperation operation:
                    foreach (var argument in operation.Arguments) Visit(argument);
                    break;
                case HirSequence sequence:
                    foreach (var item in sequence.Items) Visit(item);
                    break;
                case HirOptional { Value: { } value }:
                    Visit(value);
                    break;
                case HirRepeat repeat:
                    Visit(repeat.Count);
                    Visit(repeat.Template);
                    break;
            }
        }

        foreach (var root in roots) Visit(root);
        return meanings;
    }

    static TokenType ValueToken(SemanticType type) =>
        type.Equals(SemanticTypes.Text) ? TokenType.String : type.Equals(SemanticTypes.Bool) ? TokenType.Keyword : TokenType.Number;

    static TokenType? Classify(SyntaxTree tree, int kind, string text, TextSpan span)
    {
        if (kind == SyntaxKinds.Literal) return IsWord(text.AsSpan(span.Start, span.Length)) ? TokenType.Keyword : TokenType.Operator;
        if (SyntaxKinds.ModuleOf(kind) == 0 || tree.Language is null) return null;
        string rule = tree.Language.GetKindName(kind);
        if (rule.Contains("Number", StringComparison.Ordinal) || rule.Contains("Digit", StringComparison.Ordinal)
            || rule.Contains("Integer", StringComparison.Ordinal)) return TokenType.Number;
        if (rule.Contains("String", StringComparison.Ordinal) || rule.Contains("Char", StringComparison.Ordinal)) return TokenType.String;
        return null;
    }

    internal static bool IsWord(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !(char.IsLetter(text[0]) || text[0] == '_')) return false;
        foreach (char c in text)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary><c>//</c> to the end of the line and <c>/* … */</c> inside one trivia span.</summary>
    static void Comments(List<SemanticToken> tokens, LineMap lines, string text, TextSpan trivia)
    {
        int i = trivia.Start, end = trivia.End;
        while (i < end - 1)
        {
            if (text[i] == '/' && text[i + 1] == '/')
            {
                int stop = i;
                while (stop < end && text[stop] != '\n' && text[stop] != '\r') stop++;
                Add(tokens, lines, i, stop, TokenType.Comment, TokenModifiers.None);
                i = stop;
            }
            else if (text[i] == '/' && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, end - i - 2, StringComparison.Ordinal);
                int stop = close < 0 ? end : close + 2;
                Add(tokens, lines, i, stop, TokenType.Comment, TokenModifiers.None);
                i = stop;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>Adds [start, end) as one token per line it touches.</summary>
    static void Add(List<SemanticToken> tokens, LineMap lines, int start, int end, TokenType type, TokenModifiers modifiers)
    {
        var from = lines.PositionOf(start);
        var to = lines.PositionOf(end);
        for (int line = from.Line; line <= to.Line; line++)
        {
            int first = line == from.Line ? from.Character : 0;
            int last = line == to.Line ? to.Character : lines.LineLength(line);
            if (last > first) tokens.Add(new SemanticToken(new DocumentPosition(line, first), last - first, type, modifiers));
        }
    }
}
