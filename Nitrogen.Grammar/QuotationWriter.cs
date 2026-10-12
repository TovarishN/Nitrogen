using System.Text;

namespace Nitrogen.Grammar;

internal static class QuotationWriter
{
    internal sealed record Hole(string Name, bool IsList, int Start, int Length);

    // This only finds potential markers for structural validation. Actual occurrences and placement
    // are confirmed by the composed parser, whose token and trivia rules are authoritative.
    internal static IEnumerable<Hole> Holes(string text, GrammarAnalysis? analysis = null, GrammarSymbol? category = null)
    {
        var opaque = analysis is null || category is null ? new Dictionary<int, int>() : OpaqueTokens(text, analysis, category);
        int i = 0;
        while (i < text.Length)
        {
            if (opaque.TryGetValue(i, out int tokenEnd)) { i = tokenEnd; continue; }
            if (text[i] is '\'' or '"')
            {
                char quote = text[i++];
                while (i < text.Length)
                {
                    if (text[i++] == '\\' && i < text.Length) i++;
                    else if (text[i - 1] == quote) break;
                }
                continue;
            }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (text[i] != '$') { i++; continue; }
            int start = i++;
            if (i >= text.Length || !(char.IsLetter(text[i]) || text[i] == '_')) continue;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            bool list = start >= 2 && text[start - 1] == '.' && text[start - 2] == '.';
            yield return new(text.Substring(start + 1, i - start - 1), list, list ? start - 2 : start, i - start + (list ? 2 : 0));
        }
    }

    // Grammar-only lint cannot load generated assemblies. Probe token sites through the syntax
    // graph (including required prefixes), allowing potential holes to continue that probe.
    // Generated hole metadata comes from fields; the composed parser confirms actual occurrences.
    static Dictionary<int, int> OpaqueTokens(string text, GrammarAnalysis analysis, GrammarSymbol category)
    {
        var spans = new Dictionary<int, int>();
        var memo = new Dictionary<(Expr Expr, string Module, int Position), int>();
        var active = new HashSet<(Expr Expr, string Module, int Position)>();
        var syntaxMemo = new Dictionary<(string Rule, int Position, bool Lookahead), HashSet<int>>();
        var syntaxActive = new HashSet<(string Rule, int Position, bool Lookahead)>();
        int lookaheadDepth = 0;
        RuleEnds(category, Skip(0));
        return spans;

        int Skip(int position)
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) { position++; continue; }
                if (text.Substring(position).StartsWith("//", StringComparison.Ordinal))
                {
                    int end = text.IndexOf('\n', position + 2);
                    position = end < 0 ? text.Length : end;
                    continue;
                }
                if (text.Substring(position).StartsWith("/*", StringComparison.Ordinal))
                {
                    int end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    position = end < 0 ? text.Length : end + 2;
                    continue;
                }
                break;
            }
            return position;
        }

        int Marker(int position, bool list)
        {
            string prefix = list ? "..$" : "$";
            if (!text.Substring(position).StartsWith(prefix, StringComparison.Ordinal)) return -1;
            int end = position + prefix.Length;
            if (end >= text.Length || !(char.IsLetter(text[end]) || text[end] == '_')) return -1;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
            return end;
        }

        HashSet<int> RuleEnds(GrammarSymbol symbol, int position)
        {
            if (symbol.Rule is TokenRule token)
            {
                int end = Match(token.Body, symbol.Module, position, 0);
                if (end <= position || token.Except.Any(e => e.Value == text.Substring(position, end - position))) return new();
                if (lookaheadDepth == 0 && text.IndexOf('$', position, end - position) >= 0) spans[position] = Math.Max(spans.TryGetValue(position, out int old) ? old : 0, end);
                return new() { end };
            }
            var key = (symbol.Key, position, lookaheadDepth > 0);
            if (!syntaxMemo.TryGetValue(key, out var ends)) syntaxMemo[key] = ends = new();
            if (!syntaxActive.Add(key)) return new(ends);
            int marker = Marker(position, false);
            if (marker >= 0) ends.Add(marker);
            int count;
            do
            {
                count = ends.Count;
                if (symbol.Rule is SyntaxRule syntax) ends.UnionWith(Ends(syntax.Body, symbol.Module, null, position));
                else
                    foreach (var alternative in analysis.Point(symbol.Key))
                        ends.UnionWith(Ends(alternative.Alternative.Body, alternative.Module, alternative.ImplicitModule, position));
            } while (ends.Count > count);
            syntaxActive.Remove(key);
            return new(ends);
        }

        HashSet<int> Ends(Expr expr, ModuleDecl module, string? implicitModule, int position)
        {
            switch (expr)
            {
                case ReferenceExpr reference:
                    return analysis.Resolve(reference.Name, reference.Span, module, implicitModule) is { } symbol ? RuleEnds(symbol, position) : new();
                case LabeledExpr labeled: return Ends(labeled.Inner, module, implicitModule, position);
                case SequenceExpr sequence:
                    var positions = new HashSet<int> { position };
                    bool first = true;
                    foreach (var item in sequence.Items)
                    {
                        var next = new HashSet<int>();
                        foreach (int at in positions) next.UnionWith(Ends(item, module, implicitModule, first ? at : Skip(at)));
                        positions = next;
                        first = false;
                        if (positions.Count == 0) break;
                    }
                    return positions;
                case ChoiceExpr choice:
                    var choices = new HashSet<int>();
                    foreach (var item in choice.Alternatives) choices.UnionWith(Ends(item, module, implicitModule, position));
                    return choices;
                case RepeatExpr { Kind: RepeatKind.Optional } optional:
                    var option = Ends(optional.Inner, module, implicitModule, position);
                    option.Add(position);
                    return option;
                case RepeatExpr repeat:
                    return List(repeat.Inner, null, repeat.Kind == RepeatKind.OneOrMore, module, implicitModule, position);
                case SeparatedListExpr list:
                    return List(list.Item, list.Separator, list.AtLeastOne, module, implicitModule, position);
                case PredicateExpr predicate:
                    bool matched;
                    lookaheadDepth++;
                    try { matched = Ends(predicate.Inner, module, implicitModule, position).Count > 0; }
                    finally { lookaheadDepth--; }
                    return matched == (predicate.Kind != PredicateKind.Not) ? new() { position } : new();
                default:
                    int end = Match(expr, module, position, 0);
                    return end >= 0 ? new() { end } : new();
            }
        }

        HashSet<int> List(Expr item, Expr? separator, bool required, ModuleDecl module, string? implicitModule, int position)
        {
            var all = new HashSet<int>();
            if (!required) all.Add(position);
            var pending = new Queue<(int Position, bool First)>();
            pending.Enqueue((position, true));
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                var starts = !current.First && separator is not null
                    ? Ends(separator, module, implicitModule, Skip(current.Position)) : new HashSet<int> { current.Position };
                foreach (int start in starts)
                {
                    int at = current.First ? start : Skip(start);
                    var ends = Ends(item, module, implicitModule, at);
                    if (item is ReferenceExpr && Marker(at, true) is int marker && marker >= 0) ends.Add(marker);
                    foreach (int end in ends)
                        if (end > current.Position && all.Add(end)) pending.Enqueue((end, false));
                }
            }
            return all;
        }

        int Match(Expr expr, ModuleDecl module, int position, int depth)
        {
            if (depth > 64 || position > text.Length) return -1;
            var key = (expr, module.Name, position);
            if (memo.TryGetValue(key, out int known)) return known;
            if (!active.Add(key)) return -1;
            int result = Core(expr, module, position, depth);
            active.Remove(key);
            return memo[key] = result;
        }

        int Core(Expr expr, ModuleDecl module, int position, int depth)
        {
            switch (expr)
            {
                case LiteralExpr literal:
                    return text.Substring(position).StartsWith(literal.Value, StringComparison.Ordinal) ? position + literal.Value.Length : -1;
                case CharClassExpr chars:
                    return position < text.Length && (chars.Ranges.Any(r => text[position] >= r.First && text[position] <= r.Last) != chars.Negated) ? position + 1 : -1;
                case AnyCharExpr:
                    return position < text.Length ? position + 1 : -1;
                case LabeledExpr labeled:
                    return Match(labeled.Inner, module, position, depth + 1);
                case ReferenceExpr reference:
                    return analysis.Resolve(reference.Name, reference.Span, module, null) is { Rule: TokenRule token } symbol
                        ? Match(token.Body, symbol.Module, position, depth + 1) : -1;
                case PredicateExpr predicate:
                    bool matched = Match(predicate.Inner, module, position, depth + 1) >= 0;
                    return (matched == (predicate.Kind != PredicateKind.Not)) ? position : -1;
                case SequenceExpr sequence:
                    foreach (var item in sequence.Items)
                    {
                        position = Match(item, module, position, depth + 1);
                        if (position < 0) return -1;
                    }
                    return position;
                case ChoiceExpr choice:
                    foreach (var item in choice.Alternatives)
                    {
                        int end = Match(item, module, position, depth + 1);
                        if (end >= 0) return end;
                    }
                    return -1;
                case RepeatExpr repeat:
                    int count = 0, cursor = position;
                    while (true)
                    {
                        int end = Match(repeat.Inner, module, cursor, depth + 1);
                        if (end <= cursor) break;
                        count++;
                        cursor = end;
                        if (repeat.Kind == RepeatKind.Optional) break;
                    }
                    return count == 0 && repeat.Kind == RepeatKind.OneOrMore ? -1 : cursor;
                case SeparatedListExpr list:
                    int first = Match(list.Item, module, position, depth + 1);
                    if (first < 0) return list.AtLeastOne ? -1 : position;
                    while (true)
                    {
                        int separator = Match(list.Separator, module, first, depth + 1);
                        int item = separator < 0 ? -1 : Match(list.Item, module, separator, depth + 1);
                        if (item <= first) return first;
                        first = item;
                    }
                default: return -1;
            }
        }
    }

    internal static GrammarSymbol Category(GrammarAnalysis analysis, ModuleDecl module, RuleDecl owner, string? implicitModule)
    {
        if (owner is ExtensibleRule)
            return analysis.Resolve((implicitModule ?? module.Name) + "." + owner.Name, owner.Span, module, null)!;
        var aliases = analysis.PrimaryRules(module).OfType<SyntaxRule>().Where(EmitModel.IsAlias)
            .Where(alias => (alias.Body is ChoiceExpr choice ? choice.Alternatives.ToList() : new List<Expr> { alias.Body })
                .Any(item => item is ReferenceExpr reference && reference.Name == owner.Name)).ToList();
        return new(module, aliases.Count == 1 ? aliases[0] : owner);
    }

    internal static (ReferenceExpr? Item, bool IsList, int Stride) Shape(Expr expr) => expr switch
    {
        ReferenceExpr reference => (reference, false, 1),
        RepeatExpr { Kind: not RepeatKind.Optional, Inner: ReferenceExpr reference } => (reference, true, 1),
        SeparatedListExpr { Item: ReferenceExpr reference, Separator: LiteralExpr } => (reference, true, 2),
        _ => (null, false, 1),
    };

    public static void Write(StringBuilder b, EmitModel model, ModuleInfo info)
    {
        var categories = model.Modules.SelectMany(m => model.Analysis.PrimaryRules(m.Module)
            .Where(r => r is SyntaxRule or ExtensibleRule).Select(r => new GrammarSymbol(m.Module, r))).ToList();
        b.Append("    public override global::System.Collections.Generic.IReadOnlyList<string> QuoteCategoryNames => new string[] { ")
            .Append(string.Join(", ", categories.Where(c => c.Module.Name == info.Module.Name || info.Kinds.Any(k => Members(c, new()).Contains(k)))
                .Select(c => CSharpText.Literal(c.Key))))
            .Append(" };\n\n");
        b.Append("    public override global::System.Collections.Generic.IReadOnlyList<string> GetQuoteCategories(int localKind) => localKind switch\n    {\n");
        foreach (var kind in info.Kinds)
        {
            var names = categories.Where(c => Members(c, new()).Contains(kind)).Select(c => CSharpText.Literal(c.Key));
            b.Append($"        {kind.Local} => new string[] {{ {string.Join(", ", names)} }},\n");
        }
        b.Append("        _ => global::System.Array.Empty<string>(),\n    };\n\n");

        HashSet<KindInfo> Members(GrammarSymbol symbol, HashSet<string> visiting)
        {
            var result = new HashSet<KindInfo>();
            if (!visiting.Add(symbol.Key)) return result;
            if (symbol.Rule is SyntaxRule syntax && EmitModel.IsAlias(syntax))
            {
                var references = syntax.Body is ReferenceExpr one ? new[] { one } : ((ChoiceExpr)syntax.Body).Alternatives.Cast<ReferenceExpr>();
                foreach (var reference in references)
                    result.UnionWith(Members(model.Resolve(reference.Name, symbol.Module, null), new(visiting)));
            }
            else
                foreach (var module in model.Modules)
                    foreach (var kind in module.Kinds)
                        if (ReferenceEquals(kind.Rule, symbol.Rule) || kind.Point?.Key == symbol.Key) result.Add(kind);
            return result;
        }
    }
}
