using System.Text;

namespace Nitrogen.Grammar;

/// <summary>Emits character-level matchers <c>(ReadOnlySpan&lt;char&gt; text, int p) → end or -1</c>.</summary>
internal sealed class TokenCodeWriter
{
    readonly EmitModel _model;
    readonly ModuleInfo _info;
    readonly StringBuilder _helpers;
    int _counter;

    public TokenCodeWriter(EmitModel model, ModuleInfo info, StringBuilder helpers)
    {
        _model = model;
        _info = info;
        _helpers = helpers;
    }

    public void WriteMatcher(StringBuilder b, TokenRule rule)
    {
        string code = Code(rule.Body, "position", _info.Module, null);
        if (rule.Except.Count == 0)
        {
            b.Append("    internal static int Match").Append(rule.Name).Append("(ReadOnlySpan<char> text, int position) =>\n        ")
                .Append(code).Append(";\n\n");
            return;
        }
        // Reserved words: a C# span switch compiles to a length jump plus comparisons, no allocation.
        var words = rule.Except.Select(e => e.Value).Distinct(StringComparer.Ordinal).Select(CSharpText.Literal);
        b.Append("    internal static int Match").Append(rule.Name).Append("(ReadOnlySpan<char> text, int position)\n    {\n")
            .Append("        int end = ").Append(code).Append(";\n")
            .Append("        return end < 0 || IsReserved").Append(rule.Name).Append("(text.Slice(position, end - position)) ? -1 : end;\n    }\n\n")
            .Append("    static bool IsReserved").Append(rule.Name).Append("(ReadOnlySpan<char> word) => word switch\n    {\n")
            .Append("        ").Append(string.Join(" or ", words)).Append(" => true,\n")
            .Append("        _ => false,\n    };\n\n");
    }

    /// <summary>A helper method matching <paramref name="expr"/>; returns its name.</summary>
    public string MatcherMethod(Expr expr, ModuleDecl module, string? implicitModule)
    {
        if (expr is SequenceExpr or ChoiceExpr or RepeatExpr or SeparatedListExpr) return Method(expr, module, implicitModule);
        string name = NextName();
        string body = Code(expr, "p", module, implicitModule);
        _helpers.Append($"    static int {name}(ReadOnlySpan<char> text, int p) =>\n        {body};\n\n");
        return name;
    }

    /// <summary>A C# expression evaluating to the end of <paramref name="expr"/> matched at <paramref name="pos"/>, or -1.</summary>
    string Code(Expr expr, string pos, ModuleDecl module, string? implicitModule)
    {
        switch (expr)
        {
            case LiteralExpr literal when literal.Value.Length == 1:
                return $"({pos} < text.Length && text[{pos}] == {CSharpText.CharLiteral(literal.Value[0])} ? {pos} + 1 : -1)";
            case LiteralExpr literal:
                return $"(text.Slice({pos}).StartsWith({CSharpText.Literal(literal.Value)}) ? {pos} + {literal.Value.Length} : -1)";
            case CharClassExpr charClass:
                return $"({pos} < text.Length && {Condition(charClass, $"text[{pos}]")} ? {pos} + 1 : -1)";
            case AnyCharExpr:
                return $"({pos} < text.Length ? {pos} + 1 : -1)";
            case LabeledExpr labeled:
                return Code(labeled.Inner, pos, module, implicitModule);
            case PredicateExpr predicate:
            {
                string inner = Code(predicate.Inner, pos, module, implicitModule);
                return predicate.Kind == PredicateKind.Not ? $"({inner} >= 0 ? -1 : {pos})" : $"({inner} >= 0 ? {pos} : -1)";
            }
            case ReferenceExpr reference:
            {
                var symbol = _model.Resolve(reference.Name, module, implicitModule);
                return $"{_model.Info(symbol.Module).ClassPrefix(_info)}Match{symbol.Rule.Name}(text, {pos})";
            }
            default:
                return $"{Method(expr, module, implicitModule)}(text, {pos})";
        }
    }

    string Method(Expr expr, ModuleDecl module, string? implicitModule)
    {
        string name = NextName();
        var body = new StringBuilder();
        switch (expr)
        {
            case SequenceExpr sequence:
                foreach (var item in sequence.Items)
                    body.Append($"        p = {Code(item, "p", module, implicitModule)};\n        if (p < 0) return -1;\n");
                body.Append("        return p;\n");
                break;
            case ChoiceExpr choice:
                body.Append("        int r;\n");
                foreach (var alternative in choice.Alternatives)
                    body.Append($"        if ((r = {Code(alternative, "p", module, implicitModule)}) >= 0) return r;\n");
                body.Append("        return -1;\n");
                break;
            case RepeatExpr { Kind: RepeatKind.Optional } optional:
                body.Append($"        int r = {Code(optional.Inner, "p", module, implicitModule)};\n        return r >= 0 ? r : p;\n");
                break;
            case RepeatExpr repeat:
            {
                string inner = MatcherMethod(repeat.Inner, module, implicitModule);
                if (repeat.Kind == RepeatKind.OneOrMore)
                    body.Append($"        p = {inner}(text, p);\n        if (p < 0) return -1;\n");
                body.Append("        while (true)\n        {\n")
                    .Append($"            int r = {inner}(text, p);\n")
                    .Append("            if (r <= p) return p;\n            p = r;\n        }\n");
                break;
            }
            case SeparatedListExpr list:
            {
                string item = MatcherMethod(list.Item, module, implicitModule);
                body.Append($"        int start = p;\n        p = {item}(text, p);\n")
                    .Append($"        if (p < 0) return {(list.AtLeastOne ? "-1" : "start")};\n")
                    .Append("        while (true)\n        {\n")
                    .Append($"            int r = {Code(list.Separator, "p", module, implicitModule)};\n")
                    .Append("            if (r < 0) return p;\n")
                    .Append($"            r = {item}(text, r);\n")
                    .Append("            if (r < 0) return p;\n            p = r;\n        }\n");
                break;
            }
            default:
                throw new InvalidOperationException("unexpected token expression " + expr.GetType().Name);
        }
        _helpers.Append($"    static int {name}(ReadOnlySpan<char> text, int p)\n    {{\n{body}    }}\n\n");
        return name;
    }

    string NextName() => "T" + (++_counter);

    static string Condition(CharClassExpr charClass, string ch)
    {
        string any = string.Join(" || ", charClass.Ranges.Select(r => r.First == r.Last
            ? $"{ch} == {CSharpText.CharLiteral(r.First)}"
            : $"({ch} >= {CSharpText.CharLiteral(r.First)} && {ch} <= {CSharpText.CharLiteral(r.Last)})"));
        return charClass.Negated ? $"!({any})" : $"({any})";
    }
}
