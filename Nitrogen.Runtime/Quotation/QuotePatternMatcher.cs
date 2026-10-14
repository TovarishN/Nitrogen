using System.Collections.ObjectModel;

namespace Nitrogen;

internal sealed class QuotePatternMatcher(SyntaxTree pattern, IReadOnlyDictionary<string, QuoteHole> contracts,
    IReadOnlyDictionary<int, QuotePatternList> lists, Func<QuoteNode, QuoteNode>? resolve)
{
    static readonly IReadOnlyDictionary<string, QuoteArgument> s_empty =
        new ReadOnlyDictionary<string, QuoteArgument>(new Dictionary<string, QuoteArgument>());
    sealed record Work(int Pattern, QuoteNode Input, QuoteNode[]? Items = null);
    readonly Dictionary<string, QuoteArgument> _captures = new(StringComparer.Ordinal);

    internal static QuoteMatchResult Invalid(string code, string message) => new(false, s_empty,
        Array.AsReadOnly(new[] { new QuoteDiagnostic(code, default, message) }));
    static QuoteMatchResult Mismatch() => new(false, s_empty, Array.Empty<QuoteDiagnostic>());

    internal static QuoteMatchResult? Validate(QuoteNode root, Language language, Func<QuoteNode, QuoteNode>? resolve)
    {
        var work = new Stack<QuoteNode>();
        work.Push(root);
        while (work.TryPop(out var value))
        {
            if (resolve is not null) value = resolve(value);
            var tree = value.Tree;
            if (tree is null || tree.IsDisposed || (uint)value.Node >= (uint)tree.NodeCount)
                return Invalid("NQP0004", "Invalid or disposed input syntax.");
            if (!ReferenceEquals(tree.Language, language)) return Invalid("NQP0005", "Input language snapshot does not match.");
            if (tree.Flags(value.Node) != NodeFlags.None || tree.Kind(value.Node) is
                SyntaxKinds.None or SyntaxKinds.Error or SyntaxKinds.Ambiguous or SyntaxKinds.Hole or SyntaxKinds.ListHole or SyntaxKinds.Skipped ||
                !tree.SkippedTrivia(value.Node).IsEmpty)
                return Invalid("NQP0004", "Recovered, ambiguous, or template syntax cannot be matched.");
            for (int i = tree.ChildCount(value.Node) - 1; i >= 0; i--)
                work.Push(new(tree, tree.Child(value.Node, i), value.Origin));
        }
        return null;
    }

    internal QuoteMatchResult Match(QuoteNode input)
    {
        var work = new Stack<Work>();
        work.Push(new(pattern.Root, input));
        while (work.TryPop(out var pair))
        {
            var source = Resolve(pair.Input);
            int kind = pattern.Kind(pair.Pattern);
            if (kind == SyntaxKinds.ListHole)
            {
                string name = pattern.GetText(pair.Pattern)[3..].ToString();
                var items = pair.Items!;
                if (items.Any(item => !pattern.Language!.AcceptsQuoteNode(contracts[name].Category, item.Tree.Kind(item.Node)))) return Mismatch();
                if (_captures.TryGetValue(name, out var previous))
                {
                    var first = ((QuoteArgument.Many)previous).Values;
                    if (first.Count != items.Length) return Mismatch();
                    for (int i = 0; i < items.Length; i++)
                        if (!Equal(first[i], items[i])) return Mismatch();
                }
                else _captures[name] = QuoteArgument.List(Array.AsReadOnly(items));
                continue;
            }
            if (kind == SyntaxKinds.Hole)
            {
                string name = pattern.GetText(pair.Pattern)[1..].ToString();
                if (!pattern.Language!.AcceptsQuoteNode(contracts[name].Category, source.Tree.Kind(source.Node))) return Mismatch();
                if (_captures.TryGetValue(name, out var previous))
                {
                    if (!Equal(((QuoteArgument.Scalar)previous).Value, source)) return Mismatch();
                }
                else _captures[name] = QuoteArgument.Node(source);
                continue;
            }
            if (kind != source.Tree.Kind(source.Node)) return Mismatch();
            if (lists.TryGetValue(pair.Pattern, out var list))
            {
                int stride = list.Shape.Separator is null ? 1 : 2;
                int childCount = source.Tree.ChildCount(source.Node);
                if (stride == 2 && childCount != 0 && childCount % 2 == 0) return Mismatch();
                int itemCount = (childCount + stride - 1) / stride;
                int prefix = list.HoleIndex, suffix = list.Items.Length - prefix - 1;
                if (itemCount < prefix + suffix || itemCount < list.Shape.MinimumCount) return Mismatch();
                if (stride == 2)
                    for (int i = 1; i < childCount; i += 2)
                    {
                        int separator = source.Tree.Child(source.Node, i);
                        if (source.Tree.Kind(separator) != SyntaxKinds.Literal ||
                            !source.Tree.GetText(separator).SequenceEqual(list.Shape.Separator.AsSpan())) return Mismatch();
                    }
                var items = Enumerable.Range(0, itemCount).Select(index =>
                    Resolve(new(source.Tree, source.Tree.Child(source.Node, index * stride), source.Origin))).ToArray();
                for (int i = suffix - 1; i >= 0; i--)
                    work.Push(new(list.Items[prefix + 1 + i], items[itemCount - suffix + i]));
                work.Push(new(list.Items[prefix], source, items[prefix..(itemCount - suffix)]));
                for (int i = prefix - 1; i >= 0; i--) work.Push(new(list.Items[i], items[i]));
                continue;
            }
            if (pattern.ChildCount(pair.Pattern) != source.Tree.ChildCount(source.Node)) return Mismatch();
            int count = pattern.ChildCount(pair.Pattern);
            if (count == 0 && !pattern.GetText(pair.Pattern).SequenceEqual(source.Tree.GetText(source.Node))) return Mismatch();
            for (int i = count - 1; i >= 0; i--)
                work.Push(new(pattern.Child(pair.Pattern, i), new(source.Tree, source.Tree.Child(source.Node, i), source.Origin)));
        }
        return new(true, new ReadOnlyDictionary<string, QuoteArgument>(_captures), Array.Empty<QuoteDiagnostic>());
    }

    QuoteNode Resolve(QuoteNode node) => resolve is null ? node : resolve(node);

    bool Equal(QuoteNode first, QuoteNode second)
    {
        var work = new Stack<(QuoteNode First, QuoteNode Second)>();
        work.Push((first, second));
        while (work.TryPop(out var pair))
        {
            var left = Resolve(pair.First);
            var right = Resolve(pair.Second);
            if (left.Tree.Kind(left.Node) != right.Tree.Kind(right.Node) || left.Tree.ChildCount(left.Node) != right.Tree.ChildCount(right.Node)) return false;
            int count = left.Tree.ChildCount(left.Node);
            if (count == 0 && !left.Tree.GetText(left.Node).SequenceEqual(right.Tree.GetText(right.Node))) return false;
            for (int i = count - 1; i >= 0; i--)
                work.Push((new(left.Tree, left.Tree.Child(left.Node, i), left.Origin), new(right.Tree, right.Tree.Child(right.Node, i), right.Origin)));
        }
        return true;
    }
}
