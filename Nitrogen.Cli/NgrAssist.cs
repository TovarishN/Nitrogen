using System.Text.RegularExpressions;
using Nitrogen.LanguageService;
using Nitrogen.Ngr.Syntax;
using Nitrogen.Semantic;

namespace Nitrogen.Cli;

/// <summary>
/// The lowering language inside .ngr grammars: the <c>lowers</c> clauses and the <c>type</c> of
/// <c>declares</c>. Operations and types are coloured, completed, explained on hover and checked against
/// the semantic catalog of the workspace language the grammar belongs to (the built-in types otherwise);
/// field labels are coloured as parameters where they are declared and where clauses pass them.
/// </summary>
internal sealed partial class NgrAssist : ILanguageAssist
{
    enum Role { Operation, Type, Field, Property }

    static readonly SemanticCatalog Builtins = SemanticCatalog.Compose([], out _)!;

    static readonly HashSet<int> Clauses =
    [
        NitrogenKinds.LowersCall, NitrogenKinds.LowersLiteral, NitrogenKinds.LowersText, NitrogenKinds.LowersSequence,
        NitrogenKinds.LowersValue, NitrogenKinds.LowersReference, NitrogenKinds.LowersRepeat, NitrogenKinds.LowersTemplate,
        NitrogenKinds.LowersExpand, NitrogenKinds.LowersSequenceArgument, NitrogenKinds.LowersOptionalArgument,
        NitrogenKinds.LowersOptionalTextArgument,
        NitrogenKinds.LowersTextArgument, NitrogenKinds.Declares,
    ];

    static readonly string[] Forms = ["literal", "text", "sequence", "value", "reference", "repeat", "template", "expand", "operation"];

    public IEnumerable<AssistToken> Tokens(AssistDocument document)
    {
        foreach (var name in Names(document))
            yield return new AssistToken(name.Span, name.Role switch
            {
                Role.Operation => TokenType.Function,
                Role.Type => TokenType.Type,
                Role.Field => TokenType.Parameter,
                _ => TokenType.Property,
            });
        foreach (var label in Labels(document.Tree)) yield return new AssistToken(label, TokenType.Parameter);
    }

    public IEnumerable<AssistDiagnostic> Diagnostics(AssistDocument document)
    {
        if (document.Catalog is not { } catalog) yield break;
        foreach (var name in Names(document))
        {
            if (name.Role == Role.Operation)
            {
                if (!catalog.Operations.TryGetValue(name.Text, out var operation))
                    yield return new AssistDiagnostic(name.Span, ServiceSeverity.Error, "NM0008", $"'{name.Text}' is not an operation of the semantic catalog");
                else if (name.Arguments is int count && count != operation.Inputs.Count)
                    yield return new AssistDiagnostic(name.Span, ServiceSeverity.Error, "NM0010", $"'{name.Text}' takes {operation.Inputs.Count} arguments, not {count}");
            }
            else if (name.Role == Role.Type && Resolve(catalog, name.Text) is null)
                yield return new AssistDiagnostic(name.Span, ServiceSeverity.Error, "NM0009", $"'{name.Text}' is not a type of the semantic catalog");
        }
    }

    public AssistHover? Hover(AssistDocument document, int offset)
    {
        var catalog = document.Catalog ?? Builtins;
        foreach (var name in Names(document))
        {
            if (offset < name.Span.Start || offset >= name.Span.End) continue;
            if (name.Role == Role.Operation)
                return new AssistHover(catalog.Operations.TryGetValue(name.Text, out var operation)
                    ? $"`{Signature(operation)}` — operation" : $"`{name.Text}` — not in the semantic catalog", name.Span);
            if (name.Role == Role.Type)
                return new AssistHover(Resolve(catalog, name.Text) is { } type
                    ? $"`{type}` — semantic type" : $"`{name.Text}` — not in the semantic catalog", name.Span);
            return null;
        }
        return null;
    }

    public IEnumerable<AssistCompletion> Completion(AssistDocument document, int offset)
    {
        string text = document.Text;
        int start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '.')) start--;
        var replace = new TextSpan(start, offset - start);
        if (ClauseBefore(text, start) is not { } clause) return [];

        var catalog = document.Catalog ?? Builtins;
        var words = Lexemes(text, clause.Start, start);
        string previous = words.Count > 0 ? words[^1] : "";
        bool inArguments = words.Count(w => w == "(") > words.Count(w => w == ")");

        IEnumerable<AssistCompletion> Operations() => catalog.Operations.Values.OrderBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => new AssistCompletion(o.Id, CompletionKind.Function, Signature(o), replace));
        IEnumerable<AssistCompletion> Types() => catalog.Types.Values.Where(t => t.Id != SemanticTypes.Error.Id).OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new AssistCompletion(t.Id, CompletionKind.Class, "semantic type", replace));
        IEnumerable<AssistCompletion> Fields(params string[] keywords) => FieldLabels(text, clause.Rule, clause.Start)
            .Select(f => new AssistCompletion(f, CompletionKind.Field, "field", replace))
            .Concat(keywords.Select(k => new AssistCompletion(k, CompletionKind.Keyword, "keyword", replace)));
        IEnumerable<AssistCompletion> Keywords(IEnumerable<string> keywords) =>
            keywords.Select(k => new AssistCompletion(k, CompletionKind.Keyword, "keyword", replace));

        if (clause.Keyword == "declares")
            return previous == "type" ? Types().Concat(Fields()) : [];
        if (previous == "lowers") return Operations().Concat(Keywords(Forms));
        if (inArguments)
        {
            if (previous is "sequence" or "optional") return Types().Concat(Keywords(["inferred"]));
            if (previous is "(" or "," or "text" or "inferred") return Fields(previous is "(" or "," ? ["sequence", "optional", "text"] : []);
            if (words.Count >= 2 && words[^2] is "sequence" or "optional") return Fields();
            return [];
        }
        if (previous is "literal" or "text" or "sequence" or "repeat") return Types();
        if (previous == "value") return Types().Concat(Keywords(["type"]));
        if (previous == "operation") return [];
        if (words.Count >= 2 && words[^2] is "literal" or "text" or "sequence" or "value" or "repeat") return Fields("this");
        if (words.Count is >= 3 and <= 5 && words[1] == "repeat") return Fields(); // the count, iterator and template fields
        return [];
    }

    readonly record struct Name(TextSpan Span, string Text, Role Role, int? Arguments);

    /// <summary>The names in clauses, with what they name; an operation carries its argument count.</summary>
    static List<Name> Names(AssistDocument document)
    {
        var tree = document.Tree;
        var names = new List<Name>();
        for (int node = 0; node < tree.NodeCount; node++)
        {
            int kind = tree.Kind(node);
            if (!Clauses.Contains(kind)) continue;
            string? previous = null;
            bool first = true;
            foreach (int leaf in Leaves(tree, node))
            {
                var span = tree.Span(leaf);
                string text = document.Text.Substring(span.Start, span.Length);
                int leafKind = tree.Kind(leaf);
                if (leafKind != NitrogenKinds.Identifier && leafKind != NitrogenKinds.DottedName)
                {
                    previous = text;
                    continue;
                }
                Role? role = Classify(kind, text, previous, first);
                if (role is { } found)
                    names.Add(new Name(span, text, found, found == Role.Operation ? ArgumentCount(tree, node) : null));
                first = false;
                previous = text;
            }
        }
        return names;
    }

    /// <summary>What the name <paramref name="text"/> of a clause of <paramref name="kind"/> names, after the lexeme <paramref name="previous"/>.</summary>
    static Role? Classify(int kind, string text, string? previous, bool first)
    {
        if (text == "this") return null;
        if (kind == NitrogenKinds.LowersCall) return !first ? Role.Field : previous is "operation" or "?" ? Role.Property : Role.Operation;
        if (kind == NitrogenKinds.LowersLiteral || kind == NitrogenKinds.LowersText || kind == NitrogenKinds.LowersSequence ||
            kind == NitrogenKinds.LowersRepeat || kind == NitrogenKinds.LowersSequenceArgument || kind == NitrogenKinds.LowersOptionalArgument)
            return first ? Role.Type : Role.Field;
        if (kind == NitrogenKinds.LowersValue) return !first ? Role.Field : previous == "type" ? Role.Property : Role.Type;
        if (kind == NitrogenKinds.LowersReference) return Role.Property;
        if (kind == NitrogenKinds.Declares)
            return previous == "type" ? text.Contains('.') ? Role.Type : Role.Field
                : first ? null : Role.Field; // the symbol kind, then the declared field
        return Role.Field;
    }

    /// <summary>The leaves of a clause in order, without those of the clauses inside it.</summary>
    static IEnumerable<int> Leaves(SyntaxTree tree, int node)
    {
        for (int i = 0; i < tree.ChildCount(node); i++)
        {
            int child = tree.Child(node, i);
            if (Clauses.Contains(tree.Kind(child)) || (tree.Flags(child) & NodeFlags.Missing) != 0) continue;
            if (tree.ChildCount(child) == 0)
            {
                if (tree.Span(child).Length > 0) yield return child;
            }
            else
            {
                foreach (int leaf in Leaves(tree, child)) yield return leaf;
            }
        }
    }

    /// <summary>The arguments a call clause passes: the items of its argument list, without separators.</summary>
    static int? ArgumentCount(SyntaxTree tree, int call)
    {
        for (int i = 0; i < tree.ChildCount(call); i++)
        {
            int child = tree.Child(call, i);
            if (tree.Kind(child) != SyntaxKinds.List) continue;
            int count = 0;
            for (int k = 0; k < tree.ChildCount(child); k++)
                if (tree.Kind(tree.Child(child, k)) != SyntaxKinds.Literal) count++;
            return count;
        }
        return 0;
    }

    /// <summary>The labels of the grammar's elements (<c>Width:Dimension</c>), which clauses pass as fields.</summary>
    static IEnumerable<TextSpan> Labels(SyntaxTree tree)
    {
        for (int node = 0; node < tree.NodeCount; node++)
        {
            if (tree.Kind(node) != NitrogenKinds.Element || tree.ChildCount(node) == 0) continue;
            int name = tree.Child(node, 0);
            while (tree.ChildCount(name) > 0) name = tree.Child(name, 0);
            if (tree.Kind(name) == NitrogenKinds.Identifier && tree.Span(name).Length > 0) yield return tree.Span(name);
        }
    }

    /// <summary>The clause the cursor is in: its keyword, where it starts, and where its rule starts; null outside clauses.</summary>
    static (string Keyword, int Start, int Rule)? ClauseBefore(string text, int offset)
    {
        var prefix = text.AsSpan(0, offset);
        int lowers = LastWord(prefix, "lowers"), declares = LastWord(prefix, "declares");
        int at = Math.Max(lowers, declares);
        if (at < 0 || prefix[at..].IndexOfAny(';', '{', '}') >= 0) return null;
        int rule = Math.Max(Math.Max(LastWord(prefix[..at], "syntax"), prefix[..at].LastIndexOf('|')), 0);
        return (at == lowers ? "lowers" : "declares", at, rule);
    }

    static int LastWord(ReadOnlySpan<char> text, string word)
    {
        for (int at = text.LastIndexOf(word); at >= 0; at = at == 0 ? -1 : text[..at].LastIndexOf(word))
        {
            bool before = at == 0 || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '_');
            int end = at + word.Length;
            bool after = end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            if (before && after) return at;
        }
        return -1;
    }

    /// <summary>The words and punctuation of a clause up to the cursor.</summary>
    static List<string> Lexemes(string text, int from, int to) =>
        LexemePattern().Matches(text[from..to]).Select(m => m.Value).ToList();

    /// <summary>The labels in a rule's body: the text from the rule's start to its first clause.</summary>
    static IEnumerable<string> FieldLabels(string text, int rule, int clause)
    {
        string body = text[rule..clause];
        int clauseStart = ClauseKeyword().Match(body) is { Success: true } first ? first.Index : body.Length;
        return LabelPattern().Matches(body[..clauseStart]).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);
    }

    static SemanticType? Resolve(SemanticCatalog catalog, string name)
    {
        if (catalog.Types.TryGetValue(name, out var type)) return type;
        if (name.Contains('.')) return null;
        var matches = catalog.Types.Values.Where(t => t.Name == name).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    static string Signature(OperationSignature operation) =>
        $"{operation.Id}({string.Join(", ", operation.Inputs)}) → {operation.Result}";

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_.]*|[()?,]")]
    private static partial Regex LexemePattern();

    [GeneratedRegex(@"\b(?:declares|references|lowers|scope|dynamic)\b")]
    private static partial Regex ClauseKeyword();

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_]*)\s*:(?!:)")]
    private static partial Regex LabelPattern();
}
