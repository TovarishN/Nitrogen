using System.Text;

namespace Nitrogen.Grammar;

/// <summary>
/// What a generated TextMate grammar is named and how it colours: <paramref name="RoleScopes"/> per literal role,
/// <paramref name="DeclarationScopes"/> per declared symbol kind (else <paramref name="DefaultDeclarationScope"/>).
/// Scopes are written without the language suffix; the writer appends <c>.<paramref name="LanguageId"/></c>.
/// </summary>
public sealed record TextMateOptions(
    string LanguageId,
    string ScopeName,
    IReadOnlyDictionary<GrammarLiteralRole, string> RoleScopes,
    IReadOnlyDictionary<string, string> DeclarationScopes,
    string DefaultDeclarationScope);

/// <summary>A generated <c>.tmLanguage.json</c>, or null with the errors that stopped it.</summary>
public sealed record TextMateResult(string? Json, EquatableArray<CompiledDiagnostic> Diagnostics);

/// <summary>
/// A TextMate grammar that paints the first frame the way the server's palette will (issue: TextMate palette):
/// comments, strings and numbers as the server classifies them, declaration names after their keyword, then
/// every literal by its <see cref="LiteralRoles"/> role, including a lowered rule's operation words.
/// </summary>
internal static class TextMateWriter
{
    /// <summary>When a word has several roles across rules, the first of these wins.</summary>
    static readonly GrammarLiteralRole[] WordPriority =
    [
        GrammarLiteralRole.Operation, GrammarLiteralRole.DeclarationKeyword, GrammarLiteralRole.SectionKeyword,
        GrammarLiteralRole.ConstantWord, GrammarLiteralRole.ModifierWord,
    ];

    const string Identifier = "[A-Za-z_][A-Za-z0-9_]*";

    public static string Write(EmitModel model, TextMateOptions options)
    {
        string Scope(string scope) => scope + "." + options.LanguageId;
        var patterns = new List<string>
        {
            Match(Scope("comment.line.double-slash"), "//.*$"),
            $"{{\"name\":{Json(Scope("comment.block"))},\"begin\":{Json("/\\*")},\"end\":{Json("\\*/")}}}",
        };

        var tokens = model.Modules.SelectMany(info => info.Kinds.Select(k => (info, k.Rule))).Where(p => p.Rule is TokenRule)
            .Select(p => (p.info, Rule: (TokenRule)p.Rule!)).ToList();
        Expr? Resolve(ModuleDecl module, string name) =>
            model.Analysis.Resolve(name, default, module, null) is { Rule: TokenRule token } ? token.Body : null;
        string? RegexOf(ModuleInfo info, TokenRule rule) => TokenRegex.TryConvert(rule.Body, name => Resolve(info.Module, name));

        // Strings and numbers as SemanticTokenBuilder classifies token rules: by name.
        bool anyString = false, anyNumber = false;
        foreach (var (info, rule) in tokens)
        {
            if (rule.Name.Contains("String") || rule.Name.Contains("Char"))
            {
                string scope = Scope(rule.Name.Contains("Char") && !rule.Name.Contains("String") ? "string.quoted.single" : "string.quoted.double");
                if (RegexOf(info, rule) is { } regex) { patterns.Add(Match(scope, regex)); anyString = true; }
            }
            else if (rule.Name.Contains("Number") || rule.Name.Contains("Digit") || rule.Name.Contains("Integer"))
            {
                if (RegexOf(info, rule) is { } regex) { patterns.Add(Match(Scope("constant.numeric"), "\\b" + regex + "\\b")); anyNumber = true; }
            }
        }
        if (!anyString)
            patterns.Add($"{{\"name\":{Json(Scope("string.quoted.double"))},\"begin\":\"\\\"\",\"end\":\"\\\"\",\"patterns\":[{{\"match\":{Json("\\\\.")}}}]}}");
        if (!anyNumber) patterns.Add(Match(Scope("constant.numeric"), "\\b\\d+(?:\\.\\d+)?\\b"));

        var roles = new Dictionary<string, GrammarLiteralRole>(StringComparer.Ordinal);
        foreach (var info in model.Modules)
        {
            var moduleRoles = LiteralRoles.Of(model, info, operations: true);
            foreach (var kind in info.Kinds) DeclarationName(info, kind, moduleRoles);
            foreach (var role in moduleRoles)
                if (!roles.TryGetValue(role.Key.Literal, out var known) || Rank(role.Value) < Rank(known))
                    roles[role.Key.Literal] = role.Value;
        }

        // Words: one alternation per role, longest first; then punctuation the same way.
        foreach (var role in WordPriority)
            if (Literals(role, word: true) is { Count: > 0 } words)
                patterns.Add(Match(Scope(options.RoleScopes[role]), "\\b(?:" + string.Join("|", words.Select(TokenRegex.Escape)) + ")\\b"));
        foreach (var role in options.RoleScopes.Keys.Where(r => !WordPriority.Contains(r)).OrderBy(r => r))
            if (Literals(role, word: false) is { Count: > 0 } symbols)
                patterns.Add(Match(Scope(options.RoleScopes[role]), string.Join("|", symbols.Select(TokenRegex.Escape))));

        return "{\n  \"$schema\": \"https://raw.githubusercontent.com/martinring/tmlanguage/master/tmlanguage.json\",\n" +
            $"  \"name\": {Json(options.LanguageId)},\n  \"scopeName\": {Json(options.ScopeName)},\n" +
            "  \"patterns\": [\n    " + string.Join(",\n    ", patterns) + "\n  ]\n}\n";

        List<string> Literals(GrammarLiteralRole role, bool word) => roles
            .Where(r => r.Value == role && LiteralRoles.IsWord(r.Key) == word && r.Key.Length > 0)
            .Select(r => r.Key).OrderByDescending(l => l.Length).ThenBy(l => l, StringComparer.Ordinal).ToList();

        // `keyword Name` where a declaring rule starts with a word and then its declared field.
        void DeclarationName(ModuleInfo info, KindInfo kind, IReadOnlyDictionary<(string Kind, string Literal), GrammarLiteralRole> moduleRoles)
        {
            var (body, clauses) = kind.Rule is SyntaxRule syntax ? (syntax.Body, syntax.Clauses)
                : kind.Alternative is { } alternative ? (alternative.Body, alternative.Clauses) : (null, default);
            if (clauses.FirstOrDefault(c => c.Kind == BindingClauseKind.Declares) is not { } declares) return;
            if (body is not SequenceExpr { Items.Count: >= 2 } sequence
                || sequence.Items[0] is not LiteralExpr keyword || !LiteralRoles.IsWord(keyword.Value)
                || sequence.Items[1] is not LabeledExpr { Inner: ReferenceExpr field } labeled || labeled.Label != declares.Field)
                return;
            string name = Resolve(info.Module, field.Name) is { } fieldBody
                ? TokenRegex.TryConvert(fieldBody, n => Resolve(info.Module, n)) ?? Identifier : Identifier;
            var role = moduleRoles.TryGetValue((kind.Name, keyword.Value), out var r) ? r : GrammarLiteralRole.DeclarationKeyword;
            string symbolScope = declares.Kinds.Count > 0 && options.DeclarationScopes.TryGetValue(declares.Kinds[0].Name, out var s)
                ? s : options.DefaultDeclarationScope;
            patterns.Add($"{{\"match\":{Json("\\b(" + TokenRegex.Escape(keyword.Value) + ")\\s+(" + name + ")")}," +
                $"\"captures\":{{\"1\":{{\"name\":{Json(Scope(options.RoleScopes[role]))}}},\"2\":{{\"name\":{Json(Scope(symbolScope))}}}}}}}");
        }
    }

    static int Rank(GrammarLiteralRole role) => Array.IndexOf(WordPriority, role) is var i and >= 0 ? i : WordPriority.Length;

    static string Match(string name, string regex) => $"{{\"name\":{Json(name)},\"match\":{Json(regex)}}}";

    static string Json(string text)
    {
        var b = new StringBuilder("\"");
        foreach (char c in text)
        {
            if (c is '"' or '\\') b.Append('\\').Append(c);
            else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }
}
