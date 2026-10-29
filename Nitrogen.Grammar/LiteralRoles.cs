namespace Nitrogen.Grammar;

/// <summary>
/// <c>Nitrogen.LiteralRole</c> for the grammar compiler, which does not reference the runtime; the generated
/// code names the runtime's member of the same name.
/// </summary>
public enum GrammarLiteralRole
{
    Operation,
    DeclarationKeyword,
    SectionKeyword,
    ModifierWord,
    ConstantWord,
    BlockBracket,
    Parenthesis,
    SquareBracket,
    Terminator,
    Separator,
    Operator,
}

/// <summary>
/// Classifies every literal of a node kind by its place in the kind's rule (the TextMate palette). The
/// generator emits the result as <c>SyntaxModule.GetLiteralRole</c>, and the TextMate writer builds its word
/// and punctuation patterns from the same table, so the server and the grammar cannot disagree.
/// </summary>
internal static class LiteralRoles
{
    /// <summary>
    /// Each (kind, literal) of <paramref name="info"/>'s kinds → its role; the earliest occurrence decides. Without
    /// <paramref name="operations"/>, a lowered rule's words keep their positional role: the server colours operation
    /// words from lowered HIR, which a language may turn off, while a TextMate grammar can only read the clause.
    /// </summary>
    public static IReadOnlyDictionary<(string Kind, string Literal), GrammarLiteralRole> Of(EmitModel model, ModuleInfo info, bool operations)
    {
        var roles = new Dictionary<(string Kind, string Literal), GrammarLiteralRole>();
        foreach (var kind in info.Kinds)
        {
            var (body, clauses, module, implicitModule) = kind.Rule switch
            {
                SyntaxRule syntax => (syntax.Body, syntax.Clauses, info.Module, (string?)null),
                null when kind.Alternative is { } alternative => (alternative.Body, alternative.Clauses, info.Module, kind.ImplicitModule),
                _ => ((Expr?)null, default(EquatableArray<BindingClause>), info.Module, null),
            };
            if (body is null) continue;
            var walker = new Walker(model, module, implicitModule, kind.Name, roles,
                declares: clauses.Any(c => c.Kind == BindingClauseKind.Declares),
                arguments: operations && clauses.FirstOrDefault(c => c.Kind == BindingClauseKind.Lowers) is { } lowers
                    ? new HashSet<string>(lowers.Arguments.Select(a => a.Name), StringComparer.Ordinal) : null);
            walker.Body = body is LabeledExpr labeled ? labeled.Inner : body;
            walker.Walk(body, first: true, inArgument: false, wordChoice: false);
        }
        return roles;
    }

    internal static bool IsWord(string text)
    {
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_')) return false;
        foreach (char c in text)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary>Punctuation by its spelling; null for a word.</summary>
    internal static GrammarLiteralRole? Punctuation(string text) => IsWord(text) ? null : text switch
    {
        "{" or "}" => GrammarLiteralRole.BlockBracket,
        "(" or ")" => GrammarLiteralRole.Parenthesis,
        "[" or "]" => GrammarLiteralRole.SquareBracket,
        ";" => GrammarLiteralRole.Terminator,
        "," => GrammarLiteralRole.Separator,
        _ => GrammarLiteralRole.Operator,
    };

    sealed class Walker(EmitModel model, ModuleDecl module, string? implicitModule, string kind,
        Dictionary<(string Kind, string Literal), GrammarLiteralRole> roles, bool declares, HashSet<string>? arguments)
    {
        readonly HashSet<string> _inlining = new(StringComparer.Ordinal);

        /// <summary>The rule's body without a label: a choice of words that is all of it is an enumeration (constants).</summary>
        public Expr? Body { get; set; }

        /// <summary>Visits <paramref name="expr"/>; returns whether it can match empty input.</summary>
        public bool Walk(Expr expr, bool first, bool inArgument, bool wordChoice)
        {
            switch (expr)
            {
                case LiteralExpr literal:
                    if (!roles.ContainsKey((kind, literal.Value)))
                        roles[(kind, literal.Value)] = Classify(literal.Value, first, inArgument, wordChoice);
                    return literal.Value.Length == 0;
                case SequenceExpr sequence:
                {
                    bool nullable = true;
                    foreach (var item in sequence.Items)
                        nullable &= Walk(item, first && nullable, inArgument, wordChoice: false);
                    return nullable;
                }
                case ChoiceExpr choice:
                {
                    bool words = choice.Alternatives.Count >= 2 && choice.Alternatives.All(a => a is LiteralExpr l && IsWord(l.Value));
                    bool whole = ReferenceEquals(choice, Body);
                    bool nullable = false;
                    foreach (var alternative in choice.Alternatives)
                        nullable |= Walk(alternative, first && !(words && whole), inArgument, words);
                    return nullable;
                }
                case ContextTestExpr:
                    return true;
                case ContextScopeExpr context:
                    return Walk(context.Inner,first,inArgument,wordChoice);
                case LabeledExpr labeled:
                    return Walk(labeled.Inner, first, inArgument || arguments?.Contains(labeled.Label) == true, wordChoice);
                case RepeatExpr repeat:
                    return Walk(repeat.Inner, first, inArgument, wordChoice: false) || repeat.Kind != RepeatKind.OneOrMore;
                case SeparatedListExpr list:
                {
                    bool nullable = Walk(list.Item, first, inArgument, wordChoice: false);
                    Walk(list.Separator, first: false, inArgument, wordChoice: false);
                    return nullable || !list.AtLeastOne;
                }
                case PredicateExpr:
                    return true; // consumes nothing; its literals are never leaves
                case GrammarCallExpr { Expansion: { } expansion }:
                    return Walk(expansion, first, inArgument, wordChoice);
                case ReferenceExpr reference:
                    return Reference(reference, first, inArgument);
                default:
                    return false;
            }
        }

        /// <summary>An alias has no node: its literals are leaves under this kind, so they are classified here.</summary>
        bool Reference(ReferenceExpr reference, bool first, bool inArgument)
        {
            if (model.Analysis.Resolve(reference.Name, default, module, implicitModule) is not { Rule: SyntaxRule rule } symbol
                || !EmitModel.IsAlias(rule) || !_inlining.Add(rule.Name))
                return false;
            var outer = (module, implicitModule);
            (module, implicitModule) = (symbol.Module, null);
            bool nullable = Walk(rule.Body, first, inArgument, wordChoice: false);
            (module, implicitModule) = outer;
            _inlining.Remove(rule.Name);
            return nullable;
        }

        GrammarLiteralRole Classify(string text, bool first, bool inArgument, bool wordChoice)
        {
            if (Punctuation(text) is { } punctuation) return punctuation;
            if (arguments is not null && !inArgument) return GrammarLiteralRole.Operation;
            if (wordChoice && !first) return GrammarLiteralRole.ConstantWord;
            if (first) return declares ? GrammarLiteralRole.DeclarationKeyword : GrammarLiteralRole.SectionKeyword;
            return GrammarLiteralRole.ModifierWord;
        }
    }
}
