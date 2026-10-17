using System.Text;

namespace Nitrogen.Grammar;

/// <summary>Canonical S-expression dump of a grammar model, without spans. Used by tests and debugging.</summary>
public static class GrammarDumper
{
    public static string Dump(GrammarFile file) => string.Join("\n",
        file.Languages.Select(l => "(language " + l.Name + " " + string.Join(" ", l.Settings.Select(s =>
            "(" + s.Name + " " + string.Join(" ", s.Values.Select(v => Quote(v.Name, '\"'))) + ")")) + ")")
            .Concat(file.Modules.Select(m => Dump(m))));

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
        foreach (var helper in module.Helpers)
        {
            builder.Append(" (function ").Append(helper.Name.Name).Append(" (")
                .Append(string.Join(" ", helper.Parameters.Select(p => p.Name.Name + ":" + p.Category.Name + (p.IsList ? "*" : ""))))
                .Append(") : ").Append(helper.ReturnCategory.Name);
            if (helper.Match is { } match)
            {
                builder.Append(" (match ").Append(match.Selector.Name);
                foreach (var arm in match.Arms)
                    builder.Append(" (arm (").Append(string.Join(" ", arm.Captures.Select(p => p.Name.Name + ":" + p.Category.Name + (p.IsList ? "*" : ""))))
                        .Append(") ").Append(CSharpText.Literal(arm.Pattern.Text)).Append(" ").Append(CSharpText.Literal(arm.Result.Text)).Append(')');
                builder.Append(" (fallback ").Append(CSharpText.Literal(helper.Body.Text)).Append("))");
            }
            else builder.Append(" ").Append(CSharpText.Literal(helper.Body.Text));
            builder.Append(')');
        }
        foreach (var function in module.Functions)
            builder.Append(" (grammar-function ").Append(function.Name.Name).Append(" (")
                .Append(string.Join(" ", function.Parameters.Select(p => p.Name.Name + ":" + p.Category.Name + (p.IsList ? "*" : ""))))
                .Append(") : ").Append(function.ReturnType.Name).Append(Lets(function.Lets)).Append(' ').Append(Dump(function.Body)).Append(')');
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
        BindingClauseKind.Lowers => $"(lowers {(clause.OperationProperty is { } operation ? "operation" + (clause.Optional ? "? " : " ") + operation.Name : clause.Target!.Name)}({string.Join(", ", clause.Arguments.Select(a => a.OptionalText ? $"optional text {a.Name}" : a.InferSequence ? $"sequence inferred {a.Name}" : a.SequenceElementType is { } type ? $"sequence {type.Name} {a.Name}" : a.OptionalElementType is { } optional ? $"optional {optional.Name} {a.Name}" : a.AsText ? $"text {a.Name}" : a.Name))}))",
        BindingClauseKind.LowersLiteral => $"(lowers literal {clause.Target!.Name} {clause.Field})",
        BindingClauseKind.LowersTemplate => $"(lowers template {clause.Field}({clause.Arguments[0].Name}))",
        BindingClauseKind.LowersExpand => $"(lowers expand {clause.Field}({clause.Arguments[0].Name}))",
        BindingClauseKind.Rewrites => clause.HelperCall is { } call
            ? $"(rewrites {call.Helper.Name}({string.Join(", ", call.Arguments.Select(a => a.Name))}))"
            : $"(rewrites {CSharpText.Literal(clause.Target!.Name)})",
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
        GrammarCallExpr call => "(call " + call.Name.Name + string.Concat(call.Arguments.Select(a => " " + Dump(a))) + ")",
        IfExpr i => $"(if {Dump(i.Condition)} {Dump(i.Then)} {Dump(i.Else)})",
        ComprehensionExpr c => $"({(c.IsChoice ? "choice" : "seq")} {c.Variable.Name} {Dump(c.From)}" + (c.To is { } to ? " " + Dump(to) : "") + " " + Dump(c.Block) + ")",
        ValueClassExpr c => "(value-class " + (c.Negated ? "^ " : "") + string.Join(" ", c.Items.Select(Dump)) + ")",
        ReferenceExpr r => $"(ref {r.Name})",
        PredicateExpr p => $"({(p.Kind == PredicateKind.Not ? "not" : "and")} {Dump(p.Inner)})",
        _ => throw new ArgumentOutOfRangeException(nameof(expr)),
    };

    /// <summary>A grammar function's value expression (spec: grammar functions).</summary>
    public static string Dump(ValueExpr value) => value switch
    {
        IntValueExpr i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        CharValueExpr c => Quote(c.Value.ToString(), '\''),
        TextValueExpr t => Quote(t.Value, '"'),
        NameValueExpr n => n.Name,
        CallValueExpr c => "(" + c.Name.Name + string.Concat(c.Arguments.Select(a => " " + Dump(a))) + ")",
        UnaryValueExpr u => $"({u.Operator} {Dump(u.Operand)})",
        BinaryValueExpr b => $"({b.Operator} {Dump(b.Left)} {Dump(b.Right)})",
        IfValueExpr i => $"(if {Dump(i.Condition)} {Dump(i.Then)} {Dump(i.Else)})",
        ClassValueExpr c => "[" + (c.Negated ? "^ " : "") + string.Join(" ", c.Items.Select(Dump)) + "]",
        RuleValueExpr r => Dump(r.Block),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    static string Dump(ValueClassItem item) => item.Last is { } last ? Dump(item.First) + ".." + Dump(last) : Dump(item.First);

    static string Dump(FunctionBlock block) =>
        "{" + Lets(block.Lets) + (block.Where is { } where ? " (where " + Dump(where) + ")" : "") + " " + Dump(block.Body) + "}";

    static string Lets(EquatableArray<LetBinding> lets) => string.Concat(lets.Select(l => $" (let {l.Name.Name} {Dump(l.Value)})"));

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
