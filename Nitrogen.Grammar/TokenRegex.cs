using System.Text;

namespace Nitrogen.Grammar;

/// <summary>Token rule bodies as Oniguruma regular expressions, for generated TextMate grammars.</summary>
internal static class TokenRegex
{
    /// <summary>
    /// <paramref name="body"/> as a regex, inlining the token rules <paramref name="resolve"/> finds; null for what a
    /// regex cannot say here (grammar functions, unresolved or recursive references).
    /// </summary>
    public static string? TryConvert(Expr body, Func<string, Expr?> resolve) => Convert(body, resolve, new HashSet<string>(StringComparer.Ordinal));

    static string? Convert(Expr expr, Func<string, Expr?> resolve, HashSet<string> inlining)
    {
        switch (expr)
        {
            case LiteralExpr literal:
                return Escape(literal.Value);
            case AnyCharExpr:
                return ".";
            case CharClassExpr chars:
            {
                var b = new StringBuilder(chars.Negated ? "[^" : "[");
                foreach (var range in chars.Ranges)
                {
                    b.Append(ClassChar(range.First));
                    if (range.Last != range.First) b.Append('-').Append(ClassChar(range.Last));
                }
                return b.Append(']').ToString();
            }
            case SequenceExpr sequence:
            {
                var b = new StringBuilder();
                foreach (var item in sequence.Items)
                {
                    if (Convert(item, resolve, inlining) is not { } part) return null;
                    b.Append(part);
                }
                return b.ToString();
            }
            case ChoiceExpr choice:
            {
                var parts = new List<string>();
                foreach (var alternative in choice.Alternatives)
                {
                    if (Convert(alternative, resolve, inlining) is not { } part) return null;
                    parts.Add(part);
                }
                return "(?:" + string.Join("|", parts) + ")";
            }
            case LabeledExpr labeled:
                return Convert(labeled.Inner, resolve, inlining);
            case RepeatExpr repeat:
                return Convert(repeat.Inner, resolve, inlining) is { } inner
                    ? "(?:" + inner + ")" + repeat.Kind switch { RepeatKind.Optional => "?", RepeatKind.ZeroOrMore => "*", _ => "+" }
                    : null;
            case PredicateExpr predicate:
                return Convert(predicate.Inner, resolve, inlining) is { } look
                    ? (predicate.Kind == PredicateKind.Not ? "(?!" : "(?=") + look + ")"
                    : null;
            case GrammarCallExpr { Expansion: { } expansion }:
                return Convert(expansion, resolve, inlining);
            case ReferenceExpr reference:
            {
                if (resolve(reference.Name) is not { } target || !inlining.Add(reference.Name)) return null;
                string? result = Convert(target, resolve, inlining);
                inlining.Remove(reference.Name);
                return result is null ? null : "(?:" + result + ")";
            }
            default:
                return null;
        }
    }

    const string Meta = "\\^$.|?*+()[]{}/";

    /// <summary><paramref name="text"/> matched literally.</summary>
    public static string Escape(string text)
    {
        var b = new StringBuilder();
        foreach (char c in text)
        {
            if (Meta.IndexOf(c) >= 0) b.Append('\\').Append(c);
            else if (c < ' ') b.Append("\\x").Append(((int)c).ToString("x2"));
            else b.Append(c);
        }
        return b.ToString();
    }

    static string ClassChar(char c) =>
        c < ' ' || c == 0x7f ? "\\x" + ((int)c).ToString("x2")
        : c is '\\' or ']' or '[' or '^' or '-' ? "\\" + c
        : c.ToString();
}
