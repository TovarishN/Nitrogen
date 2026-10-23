using System.Collections.ObjectModel;

namespace Nitrogen;

internal sealed class QuotePatternMatcher(SyntaxTree pattern, IReadOnlyDictionary<string, QuoteHole> contracts,
    IReadOnlyDictionary<int, QuotePatternList> lists, Func<QuoteNode, QuoteNode>? resolve)
{
    static readonly IReadOnlyDictionary<string, QuoteArgument> s_empty =
        new ReadOnlyDictionary<string, QuoteArgument>(new Dictionary<string, QuoteArgument>());
    sealed record Work(int Pattern, QuoteNode Input, QuoteNode[]? Items = null,
        int PatternIndex = -1, int InputIndex = 0, int Length = 0);
    sealed record Choice(Work Alternative, Work[] Pending, Dictionary<string, QuoteArgument> Captures);
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
                if (tree.Child(value.Node, i) is var child && child != SyntaxTree.NoNode)
                    work.Push(new(tree, child, value.Origin));
        }
        return null;
    }

    internal QuoteMatchResult Match(QuoteNode input)
    {
        var work = new Stack<Work>();
        var choices = new Stack<Choice>();
        work.Push(new(pattern.Root, input));
        while (work.TryPop(out var pair))
        {
            if (MatchOne(pair)) continue;
            if (!choices.TryPop(out var choice)) return Mismatch();
            work = new Stack<Work>(choice.Pending.Reverse());
            work.Push(choice.Alternative);
            _captures.Clear();
            foreach (var capture in choice.Captures) _captures.Add(capture.Key, capture.Value);
        }
        return new(true, new ReadOnlyDictionary<string, QuoteArgument>(_captures), Array.Empty<QuoteDiagnostic>());

        bool MatchOne(Work pair)
        {
            if (pair.PatternIndex >= 0)
            {
                var stepList = lists[pair.Pattern];
                var items = pair.Items!;
                if (pair.PatternIndex == stepList.Items.Length) return pair.InputIndex == items.Length;
                int item = stepList.Items[pair.PatternIndex];
                if (pattern.Kind(item) == SyntaxKinds.ListHole)
                {
                    int fixedTail = stepList.Items.Skip(pair.PatternIndex + 1).Count(n => pattern.Kind(n) != SyntaxKinds.ListHole);
                    int maximum = items.Length - pair.InputIndex - fixedTail;
                    bool variableTail = stepList.Items.Skip(pair.PatternIndex + 1).Any(n => pattern.Kind(n) == SyntaxKinds.ListHole);
                    int length = variableTail ? pair.Length : maximum;
                    if (length < 0 || length > maximum) return false;
                    // Shortest prefix first: a surrounding discard finds the leftmost matching structure.
                    // Keep one lazy alternative so captures and all pending work roll back together.
                    if (variableTail && length < maximum)
                        choices.Push(new(pair with { Length = pair.Length + 1 }, work.ToArray(),
                            new Dictionary<string, QuoteArgument>(_captures, StringComparer.Ordinal)));
                    work.Push(pair with { PatternIndex = pair.PatternIndex + 1, InputIndex = pair.InputIndex + length, Length = 0 });
                    work.Push(new(item, pair.Input, items[pair.InputIndex..(pair.InputIndex + length)]));
                }
                else
                {
                    if (pair.InputIndex >= items.Length) return false;
                    work.Push(pair with { PatternIndex = pair.PatternIndex + 1, InputIndex = pair.InputIndex + 1, Length = 0 });
                    work.Push(new(item, items[pair.InputIndex]));
                }
                return true;
            }
            // A null slot (absent optional, empty list) matches only a null slot.
            if (pair.Pattern == SyntaxTree.NoNode)
            {
                return pair.Input.Node == SyntaxTree.NoNode;
            }
            var source = Resolve(pair.Input);
            bool absent = source.Node == SyntaxTree.NoNode;
            int kind = pattern.Kind(pair.Pattern);
            if (kind == SyntaxKinds.ListHole && pair.Items is not null)
            {
                string name = pattern.GetText(pair.Pattern)[3..].ToString();
                if (name == "_") return true;
                var items = pair.Items!;
                if (items.Any(item => !pattern.Language!.AcceptsQuoteNode(contracts[name].Category, item.Tree, item.Node))) return false;
                if (_captures.TryGetValue(name, out var previous))
                {
                    var first = ((QuoteArgument.Many)previous).Values;
                    if (first.Count != items.Length) return false;
                    for (int i = 0; i < items.Length; i++)
                        if (!Equal(first[i], items[i])) return false;
                }
                else _captures[name] = QuoteArgument.List(Array.AsReadOnly(items));
                return true;
            }
            if (kind == SyntaxKinds.Hole)
            {
                string name = pattern.GetText(pair.Pattern)[1..].ToString();
                if (name == "_") return !absent;
                if (absent || !pattern.Language!.AcceptsQuoteNode(contracts[name].Category, source.Tree, source.Node)) return false;
                if (_captures.TryGetValue(name, out var previous))
                {
                    if (!Equal(((QuoteArgument.Scalar)previous).Value, source)) return false;
                }
                else _captures[name] = QuoteArgument.Node(source);
                return true;
            }
            if (lists.TryGetValue(pair.Pattern, out var list))
            {
                // An absent input list is an empty one: the capture takes no items. Either side may be
                // a one-item list collapsed to its item: the pattern a lone list hole, the input any
                // node that is not a List (an item is never one where a list hole can stand).
                int stride = list.Shape.Separator is null ? 1 : 2;
                int childCount = ListSlot.Count(source.Tree, source.Node);
                if (stride == 2 && childCount != 0 && childCount % 2 == 0) return false;
                int itemCount = (childCount + stride - 1) / stride;
                if (itemCount < list.Shape.MinimumCount) return false;
                if (stride == 2)
                    for (int i = 1; i < childCount; i += 2)
                    {
                        int separator = ListSlot.Child(source.Tree, source.Node, i);
                        if (source.Tree.Kind(separator) != SyntaxKinds.Literal ||
                            !source.Tree.GetText(separator).SequenceEqual(list.Shape.Separator.AsSpan())) return false;
                    }
                var items = Enumerable.Range(0, itemCount).Select(index =>
                    Resolve(new(source.Tree, ListSlot.Child(source.Tree, source.Node, index * stride), source.Origin))).ToArray();
                work.Push(new(pair.Pattern, source, items, PatternIndex: 0));
                return true;
            }
            if (kind == SyntaxKinds.ListHole) return false; // every list hole's list is in lists
            if (!absent && kind != source.Tree.Kind(source.Node)) return false;
            if (absent || pattern.ChildCount(pair.Pattern) != source.Tree.ChildCount(source.Node)) return false;
            int count = pattern.ChildCount(pair.Pattern);
            if (count == 0 && !pattern.GetText(pair.Pattern).SequenceEqual(source.Tree.GetText(source.Node))) return false;
            for (int i = count - 1; i >= 0; i--)
                work.Push(new(pattern.Child(pair.Pattern, i), new(source.Tree, source.Tree.Child(source.Node, i), source.Origin)));
            return true;
        }
    }

    QuoteNode Resolve(QuoteNode node) => resolve is null ? node : resolve(node);

    bool Equal(QuoteNode first, QuoteNode second)
    {
        var work = new Stack<(QuoteNode First, QuoteNode Second)>();
        work.Push((first, second));
        while (work.TryPop(out var pair))
        {
            if (pair.First.Node == SyntaxTree.NoNode || pair.Second.Node == SyntaxTree.NoNode)
            {
                if (pair.First.Node != pair.Second.Node) return false;
                continue;
            }
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
