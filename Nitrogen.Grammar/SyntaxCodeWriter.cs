using System.Text;

namespace Nitrogen.Grammar;

/// <summary>
/// Emits syntax rules and extensible alternatives. Every element becomes a bool expression that
/// pushes exactly one node (none for a predicate) and leaves no trace when it fails.
/// </summary>
internal sealed class SyntaxCodeWriter
{
    readonly EmitModel _model;
    readonly ModuleInfo _info;
    readonly TokenCodeWriter _tokens;
    readonly StringBuilder _helpers;
    int _counter;
    readonly Dictionary<RecoverySite, string> _siteFields = new();

    public SyntaxCodeWriter(EmitModel model, ModuleInfo info, TokenCodeWriter tokens, StringBuilder helpers)
    {
        _model = model;
        _info = info;
        _tokens = tokens;
        _helpers = helpers;
    }

    public static IReadOnlyList<Expr> Elements(Expr body) =>
        body is SequenceExpr sequence ? sequence.Items.ToList() : new List<Expr> { body };

    public static string MethodName(KindInfo kind) => $"Parse{kind.Point!.Rule.Name}_{kind.Alternative!.Name}";

    public void WriteRule(StringBuilder b, SyntaxRule rule)
    {
        var module = _info.Module;
        if (EmitModel.IsAlias(rule))
        {
            var alternatives = rule.Body is ChoiceExpr choice ? choice.Alternatives.ToList() : new List<Expr> { rule.Body };
            b.Append($"    internal static bool Parse{rule.Name}(ref ParserState s) =>\n        ")
                .Append(string.Join("\n        || ", alternatives.Select(a => Element(a, module, null, 0))))
                .Append(";\n\n");
            return;
        }
        var elements = Elements(rule.Body);
        b.Append($"    internal static bool Parse{rule.Name}(ref ParserState s)\n    {{\n");
        if (LeadingLiteral(elements[0]) is { } literal)
        {
            // Fail on the first character before any bookkeeping, recording the expectation the
            // literal's own match would have recorded.
            b.Append("        s.SkipTrivia();\n")
                .Append($"        if (s.Current != {CSharpText.CharLiteral(literal[0])}) return s.ExpectLiteral({CSharpText.Literal(literal)});\n");
        }
        b.Append("        var start = s.Mark();\n")
            .Append($"        s.Open({_info.KindsName}.{CSharpText.Identifier(rule.Name)});\n");
        WriteElements(b, rule.Body as SequenceExpr, elements, module, null);
        b.Append("    }\n\n");
    }

    static string? LeadingLiteral(Expr element) =>
        (element is LabeledExpr labeled ? labeled.Inner : element) is LiteralExpr literal ? literal.Value : null;

    /// <summary>
    /// An alternative matches only its elements: the runtime opens and closes its node and, for a
    /// postfix alternative, has already added the left operand. A commit point (Plan 2b) repairs only
    /// when the runtime says this call of the point and this alternative's composition allow it.
    /// </summary>
    public void WriteAlternative(StringBuilder b, KindInfo kind)
    {
        var alternative = kind.Alternative!;
        var point = kind.Point!;
        string implicitModule = kind.ImplicitModule!;
        var module = _info.Module;
        bool postfix = _model.Analysis.IsPostfix(alternative, module, implicitModule, point.Key);
        var elements = Elements(alternative.Body);
        int first = postfix ? 1 : 0;
        var sites = new RecoverySite?[elements.Count];
        if (alternative.Body is SequenceExpr sequence)
            for (int i = first; i < elements.Count; i++)
                if (_model.Commits.Site(sequence, i) is { Kind: not CommitKind.None } committed) sites[i] = committed;
        bool labels = sites.Any(site => site is not null);
        string kindRef = $"{_info.KindsName}.{CSharpText.Identifier(kind.Name)}";
        b.Append($"    static bool {MethodName(kind)}(ref ParserState s)\n    {{\n");
        for (int i = first; i < elements.Count; i++)
        {
            int minPrecedence = i == elements.Count - 1 && ReferencesPoint(elements[i], module, implicitModule, point)
                ? OperandPrecedence(alternative, postfix)
                : 0;
            if (labels) b.Append($"    e{i}:\n");
            string match = Element(elements[i], module, implicitModule, minPrecedence);
            b.Append("        s.SkipTrivia();\n");
            if (sites[i] is not { } site)
            {
                b.Append($"        if (!{match}) return false;\n");
                continue;
            }
            b.Append($"        if (!{match})\n        {{\n")
                .Append($"            if (!s.Recovering || !s.AlternativeCommitted({kindRef}, {(postfix ? "true" : "false")})) return false;\n");
            WriteRepair(b, site, elements, module, implicitModule);
        }
        if (labels) b.Append("    done:\n");
        b.Append("        return true;\n    }\n\n");
    }

    static int OperandPrecedence(Alternative alternative, bool postfix)
    {
        int precedence = alternative.Precedence ?? 0;
        return postfix && alternative.Associativity == GrammarAssociativity.Right ? precedence - 1 : precedence;
    }

    bool ReferencesPoint(Expr element, ModuleDecl module, string implicitModule, GrammarSymbol point)
    {
        if (element is LabeledExpr labeled) element = labeled.Inner;
        return element is ReferenceExpr reference && _model.Resolve(reference.Name, module, implicitModule).Key == point.Key;
    }

    string Element(Expr expr, ModuleDecl module, string? implicitModule, int minPrecedence, string? listRepair = null)
    {
        switch (expr)
        {
            case LabeledExpr labeled:
                return Element(labeled.Inner, module, implicitModule, minPrecedence, listRepair);
            case LiteralExpr literal:
                return IsKeywordLike(literal.Value)
                    ? $"s.MatchKeyword({CSharpText.Literal(literal.Value)})"
                    : $"s.MatchLiteral({CSharpText.Literal(literal.Value)})";
            case ReferenceExpr reference:
            {
                var symbol = _model.Resolve(reference.Name, module, implicitModule);
                var target = _model.Info(symbol.Module);
                string prefix = target.ClassPrefix(_info);
                return symbol.Rule switch
                {
                    TokenRule token =>
                        $"s.MatchToken({target.TypeRef(_info, target.KindsName)}.{CSharpText.Identifier(token.Name)}, "
                        + $"{prefix}Match{token.Name}(s.Text, s.Position), {CSharpText.Literal(token.Name)})",
                    SyntaxRule syntax => $"{prefix}Parse{syntax.Name}(ref s)",
                    ExtensibleRule extensible =>
                        $"s.ParseExtensible({prefix}Instance.{CSharpText.Identifier(extensible.Name)}, {minPrecedence}{CallFlagsArgument(reference)})",
                    _ => throw new InvalidOperationException("unknown rule kind"),
                };
            }
            case CharClassExpr:
            case AnyCharExpr:
                return $"s.MatchToken(SyntaxKinds.Literal, {_tokens.MatcherMethod(expr, module, implicitModule)}(s.Text, s.Position), \"character\")";
            default:
                return Helper(expr, module, implicitModule, listRepair) + "(ref s)";
        }
    }

    /// <summary><c>, bits</c> for an extensible call with recovery bits (Plan 2b); nothing otherwise.</summary>
    string CallFlagsArgument(ReferenceExpr reference)
    {
        int flags = _model.Commits.CallFlags(reference);
        return flags == 0 ? "" : ", " + flags;
    }

    string Helper(Expr expr, ModuleDecl module, string? implicitModule, string? listRepair = null)
    {
        string name = "P" + (++_counter);
        var body = new StringBuilder();
        switch (expr)
        {
            case RepeatExpr { Kind: RepeatKind.Optional } optional:
                body.Append($"        if (!{Element(optional.Inner, module, implicitModule, 0)}) s.PushEmpty();\n")
                    .Append("        return true;\n");
                break;
            case RepeatExpr repeat:
            {
                string item = Element(repeat.Inner, module, implicitModule, 0);
                if (repeat.Kind == RepeatKind.OneOrMore)
                    body.Append("        var start = s.Mark();\n        s.Open(SyntaxKinds.List);\n")
                        .Append($"        if (!{item}) return Fail(ref s, start);\n");
                else
                    body.Append("        s.Open(SyntaxKinds.List);\n");
                body.Append("        while (true)\n        {\n")
                    .Append("            var m = s.Mark();\n            s.SkipTrivia();\n            int before = s.Position;\n")
                    .Append($"            if (!{item} || s.Position == before)\n")
                    .Append("            {\n                s.Reset(m);\n")
                    .Append(listRepair is null ? "" : $"                if (s.Recovering && s.RecoverInList({listRepair})) continue;\n")
                    .Append("                break;\n            }\n        }\n")
                    .Append("        s.Close();\n        return true;\n");
                break;
            }
            case SeparatedListExpr list:
            {
                string item = Element(list.Item, module, implicitModule, 0);
                string separator = Element(list.Separator, module, implicitModule, 0);
                if (list.AtLeastOne) body.Append("        var start = s.Mark();\n");
                body.Append("        s.Open(SyntaxKinds.List);\n");
                if (listRepair is null)
                {
                    body.Append($"        if ({item})\n        {{\n")
                        .Append("            while (true)\n            {\n")
                        .Append("                var m = s.Mark();\n                s.SkipTrivia();\n")
                        .Append($"                if (!{separator})\n                {{\n                    s.Reset(m);\n                    break;\n                }}\n")
                        .Append("                s.SkipTrivia();\n")
                        .Append($"                if (!{item})\n                {{\n                    s.Reset(m);\n                    break;\n                }}\n")
                        .Append("            }\n        }\n");
                    if (list.AtLeastOne) body.Append("        else\n        {\n            return Fail(ref s, start);\n        }\n");
                    body.Append("        s.Close();\n        return true;\n");
                    break;
                }
                // Recovery (Plan 3b): re-enter after a bad item, insert a missing separator. The
                // fast pass takes exactly the branches of the plain loop above.
                body.Append($"        if (!{item} && !(s.Recovering && s.RecoverInList({listRepair}) && {item}))\n")
                    .Append(list.AtLeastOne ? "            return Fail(ref s, start);\n" : "            goto done;\n")
                    .Append("        while (true)\n        {\n")
                    .Append("            var m = s.Mark();\n            s.SkipTrivia();\n")
                    .Append($"            if (!{separator})\n            {{\n                s.Reset(m);\n")
                    .Append($"                if (!(s.Recovering && s.MissingSeparator({listRepair}, {CSharpText.Literal(Describe(list.Separator))}))) break;\n")
                    .Append($"                {Missing(list.Separator, module, implicitModule)}\n            }}\n")
                    .Append("            s.SkipTrivia();\n")
                    .Append("        item:\n")
                    .Append($"            if ({item}) continue;\n")
                    .Append($"            if (s.Recovering && s.RecoverInList({listRepair})) goto item;\n")
                    .Append("            s.Reset(m);\n            break;\n        }\n")
                    .Append("    done:\n        s.Close();\n        return true;\n");
                break;
            }
            case ChoiceExpr choice:
                body.Append("        return ")
                    .Append(string.Join("\n            || ", choice.Alternatives.Select(a => Element(a, module, implicitModule, 0))))
                    .Append(";\n");
                break;
            case SequenceExpr sequence:
                body.Append("        var start = s.Mark();\n        s.Open(SyntaxKinds.Group);\n");
                WriteElements(body, sequence, sequence.Items, module, implicitModule);
                break;
            case PredicateExpr predicate:
                body.Append("        var start = s.Mark();\n")
                    .Append($"        bool matched = {Element(predicate.Inner, module, implicitModule, 0)};\n")
                    .Append("        s.Reset(start);\n")
                    .Append(predicate.Kind == PredicateKind.Not ? "        return !matched;\n" : "        return matched;\n");
                break;
            default:
                throw new InvalidOperationException("unexpected syntax expression " + expr.GetType().Name);
        }
        _helpers.Append($"    static bool {name}(ref ParserState s)\n    {{\n{body}    }}\n\n");
        return name;
    }

    /// <summary>
    /// The elements of a rule body or group, then its close. A committed element (issue 235) fails
    /// as before in the fast pass; in the recovery pass it repairs and jumps to the element the
    /// repair resumes at, pushing the missing shape of every element it passes over.
    /// </summary>
    void WriteElements(StringBuilder b, SequenceExpr? sequence, IReadOnlyList<Expr> elements, ModuleDecl module, string? implicitModule)
    {
        var sites = new RecoverySite?[elements.Count];
        if (sequence is not null)
            for (int i = 0; i < elements.Count; i++)
                if (_model.Commits.Site(sequence, i) is { Kind: CommitKind.Static } committed) sites[i] = committed;
        bool labels = sites.Any(site => site is not null);
        for (int i = 0; i < elements.Count; i++)
        {
            if (labels) b.Append($"    e{i}:\n");
            string? listRepair = i + 1 < elements.Count && sites[i + 1] is { } after && ReentersList(after, elements[i])
                ? SiteField(after, module, implicitModule)
                : null;
            string match = Element(elements[i], module, implicitModule, 0, listRepair);
            b.Append("        s.SkipTrivia();\n");
            if (sites[i] is not { } site)
            {
                b.Append($"        if (!{match}) return Fail(ref s, start);\n");
                continue;
            }
            b.Append($"        if (!{match})\n        {{\n")
                .Append("            if (!s.Recovering) return Fail(ref s, start);\n");
            WriteRepair(b, site, elements, module, implicitModule);
        }
        if (labels) b.Append("    done:\n");
        b.Append("        s.Close();\n        return true;\n");
    }

    /// <summary>A committed element's repair: recover, push the missing shapes, jump to the resumed element.</summary>
    void WriteRepair(StringBuilder b, RecoverySite site, IReadOnlyList<Expr> elements, ModuleDecl module, string? implicitModule)
    {
        int i = site.Index;
        b.Append($"            switch (s.Recover({SiteField(site, module, implicitModule)}))\n            {{\n")
            .Append($"                case {i}:\n                    goto e{i};\n");
        foreach (int resume in ResumePoints(site, elements.Count))
        {
            b.Append($"                case {resume}:\n");
            AppendMissing(b, elements, i, resume, module, implicitModule);
            b.Append($"                    goto e{resume};\n");
        }
        b.Append("                default:\n");
        AppendMissing(b, elements, i, elements.Count, module, implicitModule);
        b.Append("                    goto done;\n            }\n        }\n");
    }

    static IEnumerable<int> ResumePoints(RecoverySite site, int count) =>
        new[] { site.Index + 1 }.Concat(site.Sync.Select(p => p.Index))
            .Where(k => k > site.Index && k < count).Distinct().OrderBy(k => k);

    /// <summary>
    /// A repetition or separated list right before a commit point whose sync set re-enters it, where
    /// every set the list's recovery tests is testable: with <i>any</i> in one, a valid list end could
    /// not be told from garbage (Plan 3b).
    /// </summary>
    static bool ReentersList(RecoverySite site, Expr element) =>
        (element is LabeledExpr labeled ? labeled.Inner : element) is RepeatExpr { Kind: not RepeatKind.Optional } or SeparatedListExpr
        && site.Sync.Any(p => p.Index == site.Index - 1)
        && site.Sync.Where(p => p.Index >= site.Index - 1).All(p => p.First.Items.All(t => t.Kind != TerminalKind.Any));

    void AppendMissing(StringBuilder b, IReadOnlyList<Expr> elements, int from, int to, ModuleDecl module, string? implicitModule)
    {
        for (int j = from; j < to; j++)
        {
            string statement = Missing(elements[j], module, implicitModule);
            if (statement.Length > 0) b.Append("                    ").Append(statement).Append('\n');
        }
    }

    /// <summary>What an element pushes when recovery passes over it: its empty shape if nullable, else a Missing node of its kind.</summary>
    string Missing(Expr element, ModuleDecl module, string? implicitModule)
    {
        var bare = element is LabeledExpr labeled ? labeled.Inner : element;
        if (_model.Analysis.IsNullable(bare, module, implicitModule))
            return bare switch
            {
                RepeatExpr { Kind: RepeatKind.Optional } => "s.PushEmpty();",
                RepeatExpr or SeparatedListExpr => "s.PushEmptyList();",
                PredicateExpr => "",
                _ => "s.PushMissing(SyntaxKinds.Error);",
            };
        return "s.PushMissing(" + MissingKind(bare, module, implicitModule) + ");";
    }

    string MissingKind(Expr bare, ModuleDecl module, string? implicitModule)
    {
        switch (bare)
        {
            case LiteralExpr:
            case CharClassExpr:
            case AnyCharExpr:
                return "SyntaxKinds.Literal";
            case RepeatExpr:
            case SeparatedListExpr:
                return "SyntaxKinds.List";
            case SequenceExpr:
                return "SyntaxKinds.Group";
            case ReferenceExpr reference:
            {
                var symbol = _model.Resolve(reference.Name, module, implicitModule);
                if (symbol.Rule is TokenRule || (symbol.Rule is SyntaxRule syntax && !EmitModel.IsAlias(syntax)))
                {
                    var target = _model.Info(symbol.Module);
                    return $"{target.TypeRef(_info, target.KindsName)}.{CSharpText.Identifier(symbol.Rule.Name)}";
                }
                return "SyntaxKinds.Error";
            }
            default:
                return "SyntaxKinds.Error";
        }
    }

    /// <summary>The static <c>RepairSite</c> field of a commit point, written once per module.</summary>
    string SiteField(RecoverySite site, ModuleDecl module, string? implicitModule)
    {
        if (_siteFields.TryGetValue(site, out string? field)) return field;
        field = "s_repair" + _siteFields.Count;
        _siteFields[site] = field;
        string sync = string.Join(",\n            ", site.Sync.Select(p => $"new SyncPoint({p.Index}, {LookaheadCode(p.First)})"));
        _helpers.Append($"    static readonly RepairSite {field} = new({site.Index}, {CSharpText.Literal(Describe(site.Element))},\n")
            .Append($"        {LookaheadCode(site.Insert)},\n")
            .Append($"        new SyncPoint[]\n        {{\n            {sync},\n        }});\n\n");
        return field;
    }

    string LookaheadCode(Lookahead lookahead)
    {
        var literals = new List<string>();
        var keywords = new List<string>();
        var tokens = new List<string>();
        bool end = false;
        foreach (var terminal in lookahead.Items)
        {
            switch (terminal.Kind)
            {
                case TerminalKind.Literal:
                    (IsKeywordLike(terminal.Value) ? keywords : literals).Add(CSharpText.Literal(terminal.Value));
                    break;
                case TerminalKind.Token:
                {
                    var (rule, owner) = _model.Commits.Token(terminal.Value);
                    tokens.Add($"&{_model.Info(owner).ClassPrefix(_info)}Match{rule.Name}");
                    break;
                }
                case TerminalKind.End:
                    end = true;
                    break;
                // TerminalKind.Any cannot be tested; the set simply never matches on it.
            }
        }
        return $"new LookaheadSet(new string[] {{ {string.Join(", ", literals)} }}, new string[] {{ {string.Join(", ", keywords)} }}, "
            + $"new delegate*<ReadOnlySpan<char>, int, int>[] {{ {string.Join(", ", tokens)} }}, {(end ? "true" : "false")})";
    }

    /// <summary>An element as a diagnostic phrase.</summary>
    static string Describe(Expr element) => element switch
    {
        LabeledExpr labeled => Describe(labeled.Inner),
        LiteralExpr literal => "'" + literal.Value + "'",
        ReferenceExpr reference => reference.Name.Substring(reference.Name.LastIndexOf('.') + 1),
        RepeatExpr repeat => Describe(repeat.Inner),
        SeparatedListExpr list => Describe(list.Item),
        SequenceExpr sequence => Describe(sequence.Items[0]),
        ChoiceExpr choice => string.Join(" or ", choice.Alternatives.Select(Describe)),
        _ => "character",
    };

    static bool IsKeywordLike(string literal) =>
        GrammarLexer.IsIdentifierStart(literal[0]) && literal.All(GrammarLexer.IsIdentifierPart);
}
