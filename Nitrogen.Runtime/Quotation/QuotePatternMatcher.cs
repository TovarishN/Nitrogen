using System.Collections.ObjectModel;

namespace Nitrogen;

internal sealed class QuotePatternMatcher(SyntaxTree pattern, IReadOnlyDictionary<string, QuoteHole> contracts,
    IReadOnlyDictionary<int, QuotePatternList> lists, Func<QuoteNode, QuoteNode>? resolve, QuoteMatchBudget budget)
{
    static readonly IReadOnlyDictionary<string, QuoteArgument> s_empty =
        new ReadOnlyDictionary<string, QuoteArgument>(new Dictionary<string, QuoteArgument>());

    abstract record Job;
    sealed record NodeJob(int Pattern, QuoteNode Input) : Job;
    sealed record SequenceJob(QuotePatternList Plan, QuoteNode[] Items, int PatternIndex, int InputIndex, int Length = 0) : Job;
    sealed record SegmentJob(int Pattern, QuoteNode[] Items, int Start, int Count) : Job;
    // Persistent continuations let a choice restore pending outer/nested work without copying a stack.
    sealed record Pending(Job Job, Pending? Next);
    sealed record Choice(Pending Work, int CaptureCount);
    sealed record Binding(QuoteNode? Scalar, QuoteNode[]? Items = null, int Start = 0, int Count = 0);
    readonly Dictionary<string, Binding> _captures = new(StringComparer.Ordinal);
    readonly List<string> _trail = [];
    readonly Stack<Choice> _choices = [];

    internal static QuoteMatchResult Invalid(string code, string message) => new(false, s_empty,
        Array.AsReadOnly(new[] { new QuoteDiagnostic(code, default, message) }));
    static QuoteMatchResult Mismatch() => new(false, s_empty, Array.Empty<QuoteDiagnostic>());

    internal static QuoteMatchResult? Validate(QuoteNode root, Language language, Func<QuoteNode, QuoteNode>? resolve, QuoteMatchBudget budget)
    {
        var work = new Stack<QuoteNode>();
        work.Push(root);
        while (work.TryPop(out var value))
        {
            budget.Step();
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
        Pending? work = new(new NodeJob(pattern.Root, input), null);
        while (work is not null)
        {
            var job = work.Job;
            work = work.Next;
            bool matched = job switch
            {
                NodeJob node => MatchNode(node, ref work),
                SequenceJob sequence => MatchSequence(sequence, ref work),
                SegmentJob segment => MatchSegment(segment),
                _ => throw new InvalidOperationException(),
            };
            if (matched) continue;
            if (!_choices.TryPop(out var choice)) return Mismatch();
            while (_trail.Count > choice.CaptureCount)
            {
                _captures.Remove(_trail[^1]);
                _trail.RemoveAt(_trail.Count - 1);
            }
            work = choice.Work;
        }
        var captures = new Dictionary<string, QuoteArgument>(StringComparer.Ordinal);
        foreach (var (name, value) in _captures)
            captures[name] = value.Scalar is { } node ? QuoteArgument.Node(node)
                : QuoteArgument.List(Array.AsReadOnly(value.Items!.AsSpan(value.Start, value.Count).ToArray()));
        return new(true, new ReadOnlyDictionary<string, QuoteArgument>(captures), Array.Empty<QuoteDiagnostic>());
    }

    bool MatchNode(NodeJob pair, ref Pending? work)
    {
        budget.Step();
        if (pair.Pattern == SyntaxTree.NoNode) return pair.Input.Node == SyntaxTree.NoNode;
        var source = Resolve(pair.Input);
        bool absent = source.Node == SyntaxTree.NoNode;
        int kind = pattern.Kind(pair.Pattern);
        if (kind == SyntaxKinds.Hole)
        {
            string name = pattern.GetText(pair.Pattern)[1..].ToString();
            bool discard = pattern.PatternHoleSites.TryGetValue(pair.Pattern, out var site);
            if (absent || !pattern.Language!.AcceptsQuoteNode(discard ? site!.Category : contracts[name].Category, source.Tree, source.Node)) return false;
            if (discard) return true;
            if (_captures.TryGetValue(name, out var previous)) return Equal(previous.Scalar!, source);
            Bind(name, new(source));
            return true;
        }
        if (lists.TryGetValue(pair.Pattern, out var list))
        {
            int stride = list.Shape.Separator is null ? 1 : 2;
            int childCount = ListSlot.Count(source.Tree, source.Node);
            if (stride == 2 && childCount != 0 && childCount % 2 == 0) return false;
            int itemCount = (childCount + stride - 1) / stride;
            if (itemCount < list.MinimumRemaining[0] || itemCount < list.Shape.MinimumCount) return false;
            if (stride == 2)
                for (int i = 1; i < childCount; i += 2)
                {
                    budget.Step();
                    int separator = ListSlot.Child(source.Tree, source.Node, i);
                    if (source.Tree.Kind(separator) != SyntaxKinds.Literal ||
                        !source.Tree.GetText(separator).SequenceEqual(list.Shape.Separator.AsSpan())) return false;
                }
            var items = new QuoteNode[itemCount];
            for (int i = 0; i < items.Length; i++)
            {
                budget.Step();
                items[i] = Resolve(new(source.Tree, ListSlot.Child(source.Tree, source.Node, i * stride), source.Origin));
            }
            work = new(new SequenceJob(list, items, 0, 0), work);
            return true;
        }
        if (kind == SyntaxKinds.ListHole || absent || kind != source.Tree.Kind(source.Node) ||
            pattern.ChildCount(pair.Pattern) != source.Tree.ChildCount(source.Node)) return false;
        int count = pattern.ChildCount(pair.Pattern);
        if (count == 0 && !pattern.GetText(pair.Pattern).SequenceEqual(source.Tree.GetText(source.Node))) return false;
        for (int i = count - 1; i >= 0; i--)
            work = new(new NodeJob(pattern.Child(pair.Pattern, i), new(source.Tree, source.Tree.Child(source.Node, i), source.Origin)), work);
        return true;
    }

    bool MatchSequence(SequenceJob sequence, ref Pending? work)
    {
        var (plan, items, pi, ii, length) = sequence;
        if (pi == plan.Items.Length) return ii == items.Length;
        int node = plan.Items[pi];
        if (pattern.Kind(node) != SyntaxKinds.ListHole)
        {
            if (ii >= items.Length) return false;
            work = new(new SequenceJob(plan, items, pi + 1, ii + 1), work);
            work = new(new NodeJob(node, items[ii]), work);
            return true;
        }
        int maximum = items.Length - ii - plan.MinimumRemaining[pi + 1];
        if (maximum < 0) return false;
        string name = pattern.GetText(node)[3..].ToString();
        bool bound = _captures.TryGetValue(name, out var previous);
        // With no later segment, complete coverage determines this segment's length uniquely.
        if (bound) length = previous!.Count;
        else if (plan.SegmentsRemaining[pi + 1] == 0) length = maximum;
        if (length > maximum) return false;
        budget.Step(); // one attempted split, including fixed/repeated segment lengths
        if (!bound && plan.SegmentsRemaining[pi + 1] > 0 && length < maximum)
            _choices.Push(new(new(sequence with { Length = length + 1 }, work), _trail.Count));
        work = new(new SequenceJob(plan, items, pi + 1, ii + length), work);
        work = new(new SegmentJob(node, items, ii, length), work);
        return true;
    }

    bool MatchSegment(SegmentJob segment)
    {
        string name = pattern.GetText(segment.Pattern)[3..].ToString();
        bool discard = pattern.PatternHoleSites.TryGetValue(segment.Pattern, out var site);
        string category = discard ? site!.Category : contracts[name].Category;
        for (int i = 0; i < segment.Count; i++)
        {
            budget.Step();
            var item = segment.Items[segment.Start + i];
            if (!pattern.Language!.AcceptsQuoteNode(category, item.Tree, item.Node)) return false;
        }
        if (discard) return true;
        if (_captures.TryGetValue(name, out var previous))
        {
            if (previous.Count != segment.Count) return false;
            for (int i = 0; i < segment.Count; i++)
                if (!Equal(previous.Items![previous.Start + i], segment.Items[segment.Start + i])) return false;
        }
        else Bind(name, new(null, segment.Items, segment.Start, segment.Count));
        return true;
    }

    void Bind(string name, Binding value)
    {
        _captures.Add(name, value);
        _trail.Add(name);
    }

    QuoteNode Resolve(QuoteNode node) => resolve is null ? node : resolve(node);

    bool Equal(QuoteNode first, QuoteNode second)
    {
        var work = new Stack<(QuoteNode First, QuoteNode Second)>();
        work.Push((first, second));
        while (work.TryPop(out var pair))
        {
            budget.Step();
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
