using Nitrogen.Binding;

namespace Nitrogen.LanguageService;

/// <summary>
/// Semantic tokens from a document's tree and binding (issue 238). Leaves are visited in text order
/// (the tree is preorder). A leaf that starts a declared, or effectively referenced and resolved,
/// name becomes one token for the whole name. Other leaves are classified by shape: word literals
/// are keywords, other literals operators, number- and string-like token rules numbers and strings.
/// Comments come from the trivia. Every token is split per line.
/// </summary>
internal static class SemanticTokenBuilder
{
    public static List<SemanticToken> Build(Document document, Project project)
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
