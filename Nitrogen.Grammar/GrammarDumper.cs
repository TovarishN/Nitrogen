using System.Text;

namespace Nitrogen.Grammar;

/// <summary>Canonical S-expression dump of a grammar model, without spans. Used by tests and debugging.</summary>
public static class GrammarDumper
{
    public static string Dump(GrammarFile file) => string.Join("\n", file.Modules.Select(m => Dump(m)));

    public static string Dump(ModuleDecl module)
    {
        var builder = new StringBuilder("(module ").Append(module.Name);
        foreach (var u in module.Usings) builder.Append(" (using ").Append(u.Module).Append(')');
        foreach (var symbols in module.Symbols)
            builder.Append(" (symbols ").Append(string.Join(" ", symbols.Kinds.Select(k => k.Name))).Append(')');
        foreach (var builtin in module.Builtins)
            builder.Append(" (builtin ").Append(builtin.Kind.Name).Append(builtin.Scope is { } scope ? " in " + scope.Name : "").Append(' ')
                .Append(string.Join(" ", builtin.Names.Select(n => n.Name))).Append(')');
        foreach (var property in module.SymbolProperties) builder.Append(SymbolProperty(property));
        foreach (var rule in module.Rules) builder.Append(' ').Append(Dump(rule));
        foreach (var extend in module.Extends)
        {
            builder.Append(" (extend ").Append(extend.Target);
            foreach (var alternative in extend.Alternatives) builder.Append(' ').Append(Dump(alternative));
            builder.Append(')');
        }
        return builder.Append(')').ToString();
    }

    public static string Dump(RuleDecl rule) => rule switch
    {
        TokenRule t => t.Except.Count == 0
            ? $"(token {t.Name} {Dump(t.Body)})"
            : $"(token {t.Name} {Dump(t.Body)} (except {string.Join(" ", t.Except.Select(e => Quote(e.Value, '"')))}))",
        SyntaxRule s => $"(syntax {s.Name} {Dump(s.Body)}{Clauses(s.Clauses)}{Semantics(s.Semantics)})",
        ExtensibleRule e => "(extensible " + e.Name + string.Concat(e.Properties.Select(p => " " + Dump(p))) + string.Concat(e.Alternatives.Select(a => " " + Dump(a))) + ")",
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    public static string Dump(Alternative alternative)
    {
        var builder = new StringBuilder("(alt ")
            .Append(alternative.Name.Length == 0 ? "?" : alternative.Name)
            .Append(' ').Append(Dump(alternative.Body));
        if (alternative.Precedence is int precedence) builder.Append(' ').Append(precedence);
        if (alternative.Associativity is GrammarAssociativity associativity)
            builder.Append(associativity == GrammarAssociativity.Left ? " left" : " right");
        builder.Append(Clauses(alternative.Clauses));
        builder.Append(Semantics(alternative.Semantics));
        return builder.Append(')').ToString();
    }

    static string SymbolProperty(SymbolPropertyDecl p) =>
        $" (symbol-property {p.Name.Name} for {string.Join("|", p.Kinds.Select(k => k.Name))} {{{p.Type.Text}}} {{{p.Default.Text}}})";

    static string Semantics(SemanticsBlock? block) => block is null
        ? ""
        : " (semantics" + string.Concat(block.Properties.Select(p => " " + Dump(p))) + string.Concat(block.Statements.Select(s => " " + Dump(s))) + ")";

    public static string Dump(PropertyDecl p) =>
        $"({(p.Direction == PropertyDirection.Out ? "out" : "in")}{(p.Hover ? " hover" : "")}{(p.Expected ? " expected" : "")} {p.Name.Name} {{{p.Type.Text}}} {{{p.Default.Text}}})";

    public static string Dump(SemanticStatement statement) => statement switch
    {
        AssignStatement a => $"(set {(a.Target == AssignTarget.Self ? "" : a.Target == AssignTarget.Symbol ? "symbol." : a.Child!.Name + ".")}{a.Property.Name} {{{a.Value.Text}}})",
        CheckStatement c => $"(check{(c.Code is { } code ? " " + code.Name : "")} {{{c.Condition.Text}}} {{{c.Message.Text}}}{(c.At is { } at ? " at " + at.Name : "")})",
        _ => throw new ArgumentOutOfRangeException(nameof(statement)),
    };

    static string Clauses(EquatableArray<BindingClause> clauses) => string.Concat(clauses.Select(c => " " + Dump(c)));

    public static string Dump(BindingClause clause) => clause.Kind switch
    {
        BindingClauseKind.Declares =>
            $"(declares {clause.Kinds[0].Name} {clause.Field}{(clause.Sequential ? " sequential" : "")}{(clause.FileScope ? " in file" : "")}{(clause.Export ? " export" : "")}{(clause.Target is { } type ? " type " + type.Name : "")})",
        BindingClauseKind.References =>
            $"(references{(clause.Optional ? "?" : "")} {string.Join("|", clause.Kinds.Select(k => k.Name))} {clause.Field}{(clause.Qualifier is { } q ? " in " + q.Name : "")})",
        BindingClauseKind.Scope => "(scope)",
        BindingClauseKind.LowersRepeat => $"(lowers repeat {clause.Target!.Name} {string.Join(" ", clause.Arguments.Select(argument => argument.Name))})",
        BindingClauseKind.Lowers => $"(lowers {(clause.OperationProperty is { } operation ? "operation" + (clause.Optional ? "? " : " ") + operation.Name : clause.Target!.Name)}({string.Join(", ", clause.Arguments.Select(a => a.InferSequence ? $"sequence inferred {a.Name}" : a.SequenceElementType is { } type ? $"sequence {type.Name} {a.Name}" : a.OptionalElementType is { } optional ? $"optional {optional.Name} {a.Name}" : a.AsText ? $"text {a.Name}" : a.Name))}))",
        BindingClauseKind.LowersLiteral => $"(lowers literal {clause.Target!.Name} {clause.Field})",
        BindingClauseKind.LowersText => $"(lowers text {clause.Target!.Name} {clause.Field})",
        BindingClauseKind.LowersSequence => $"(lowers sequence {clause.Target!.Name} {clause.Field})",
        BindingClauseKind.LowersValue => clause.TypeProperty is { } typeProperty
            ? $"(lowers value type {typeProperty.Name} {clause.Field})"
            : $"(lowers value {clause.Target!.Name} {clause.Field})",
        BindingClauseKind.LowersReference => $"(lowers reference type {clause.TypeProperty!.Name}" +
            (clause.InitializerProperty is { } initializer ? $" initializer {initializer.Name}" : "") + ")",
        _ => "(dynamic)",
    };

    public static string Dump(Expr expr) => expr switch
    {
        SequenceExpr s => "(seq " + string.Join(" ", s.Items.Select(i => Dump(i))) + ")",
        ChoiceExpr c => "(choice " + string.Join(" ", c.Alternatives.Select(a => Dump(a))) + ")",
        LabeledExpr l => $"(label {l.Label} {Dump(l.Inner)})",
        RepeatExpr r => "(" + (r.Kind switch
        {
            RepeatKind.Optional => "opt",
            RepeatKind.ZeroOrMore => "star",
            _ => "plus",
        }) + " " + Dump(r.Inner) + ")",
        SeparatedListExpr l => $"({(l.AtLeastOne ? "sep+" : "sep*")} {Dump(l.Item)} {Dump(l.Separator)})",
        LiteralExpr l => "(lit " + Quote(l.Value, '"') + ")",
        CharClassExpr c => "(class " + (c.Negated ? "^ " : "") + string.Join(" ", c.Ranges.Select(Range)) + ")",
        AnyCharExpr => "any",
        ReferenceExpr r => $"(ref {r.Name})",
        PredicateExpr p => $"({(p.Kind == PredicateKind.Not ? "not" : "and")} {Dump(p.Inner)})",
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };

    static string Range(CharRange range) =>
        range.First == range.Last
            ? Quote(range.First.ToString(), '\'')
            : Quote(range.First.ToString(), '\'') + ".." + Quote(range.Last.ToString(), '\'');

    static string Quote(string value, char quote)
    {
        var builder = new StringBuilder().Append(quote);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default:
                    if (c == quote) builder.Append('\\').Append(c);
                    else if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else builder.Append(c);
                    break;
            }
        }
        return builder.Append(quote).ToString();
    }
}
