using System.Collections.ObjectModel;

namespace Nitrogen;

internal sealed class QuotePatternMatcher(QuotePattern root, Func<QuoteNode, QuoteNode>? resolve, QuoteMatchBudget budget)
{
    static readonly IReadOnlyDictionary<string, QuoteArgument> s_empty =
        new ReadOnlyDictionary<string, QuoteArgument>(new Dictionary<string, QuoteArgument>());

    // A frame is the pattern a job walks and the capture scope it binds in; scope 0 is the published one.
    sealed record Frame(QuotePattern Pattern, int Scope);
    abstract record Job;
    sealed record NodeJob(Frame Frame, int Pattern, QuoteNode Input) : Job;
    sealed record SequenceJob(Frame Frame, QuotePatternList Plan, QuoteNode[] Items, int PatternIndex, int InputIndex, int Length = 0) : Job;
    sealed record SegmentJob(Frame Frame, int Pattern, QuoteNode[] Items, int Start, int Count) : Job;
    // Tries a named pattern's alternatives in order; ChoiceDepth is the choice count before the first try.
    sealed record ReferenceJob(Frame Caller, NamedPatternDefinition Target, QuoteNode Input, string? BindName,
        int Alternative = 0, int ChoiceDepth = -1) : Job;
    // Tries each preorder candidate at or below a hole in turn; later failure resumes with the next one.
    // Remaining is a persistent cursor: the next node first, so nodes are visited only as candidates are needed.
    sealed record DescendantJob(Frame Caller, NamedPatternDefinition Target, NodeStack? Remaining, string BindName) : Job;
    sealed record NodeStack(QuoteNode Node, NodeStack? Next);
    // Commits a successful reference: the callee's choices are cut, since any alternative binds the same node.
    sealed record BindJob(Frame Caller, string? Name, QuoteNode Value, int ChoiceDepth) : Job;
    // Persistent continuations let a choice restore pending outer/nested work without copying a stack.
    sealed record Pending(Job Job, Pending? Next);
    sealed record Choice(Pending Work, int CaptureCount);
    sealed record Binding(QuoteNode? Scalar, QuoteNode[]? Items = null, int Start = 0, int Count = 0);
    readonly Dictionary<(int Scope, string Name), Binding> _captures = new();
    readonly List<(int Scope, string Name)> _trail = [];
    readonly Stack<Choice> _choices = [];
    readonly Language _language = root.Tree.Language!;
    int _scopes;

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

    // Nodes whose subtree Validate would reject, computed bottom-up in one pass.
    internal static HashSet<int> InvalidSubtrees(QuoteNode root, QuoteMatchBudget budget)
    {
        var tree = root.Tree;
        var invalid = new HashSet<int>();
        var work = new Stack<(int Node, bool Exit)>();
        work.Push((root.Node, false));
        while (work.TryPop(out var item))
        {
            budget.Step();
            int node = item.Node;
            if (!item.Exit)
            {
                work.Push((node, true));
                for (int i = tree.ChildCount(node) - 1; i >= 0; i--)
                    if (tree.Child(node, i) is var child && child != SyntaxTree.NoNode) work.Push((child, false));
                continue;
            }
            bool bad = tree.Flags(node) != NodeFlags.None || tree.Kind(node) is
                SyntaxKinds.None or SyntaxKinds.Error or SyntaxKinds.Ambiguous or SyntaxKinds.Hole or SyntaxKinds.ListHole or SyntaxKinds.Skipped ||
                !tree.SkippedTrivia(node).IsEmpty;
            for (int i = 0; !bad && i < tree.ChildCount(node); i++)
                bad = tree.Child(node, i) is var child && child != SyntaxTree.NoNode && invalid.Contains(child);
            if (bad) invalid.Add(node);
        }
        return invalid;
    }

    internal QuoteMatchResult Match(QuoteNode input)
    {
        Pending? work = new(new NodeJob(new(root, 0), root.Tree.Root, input), null);
        while (work is not null)
        {
            var job = work.Job;
            work = work.Next;
            bool matched = job switch
            {
                NodeJob node => MatchNode(node, ref work),
                SequenceJob sequence => MatchSequence(sequence, ref work),
                SegmentJob segment => MatchSegment(segment, ref work),
                ReferenceJob reference => MatchReference(reference, ref work),
                BindJob bind => Commit(bind),
                DescendantJob descendant => MatchDescendant(descendant, ref work),
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
        foreach (var ((scope, name), value) in _captures)
            if (scope == 0) captures[name] = value.Scalar is { } node ? QuoteArgument.Node(node)
                : QuoteArgument.List(Array.AsReadOnly(value.Items!.AsSpan(value.Start, value.Count).ToArray()));
        return new(true, new ReadOnlyDictionary<string, QuoteArgument>(captures), Array.Empty<QuoteDiagnostic>());
    }

    bool MatchNode(NodeJob pair, ref Pending? work)
    {
        budget.Step();
        if (pair.Pattern == SyntaxTree.NoNode) return pair.Input.Node == SyntaxTree.NoNode;
        var frame = pair.Frame;
        var pattern = frame.Pattern.Tree;
        var source = Resolve(pair.Input);
        bool absent = source.Node == SyntaxTree.NoNode;
        int kind = pattern.Kind(pair.Pattern);
        if (kind == SyntaxKinds.Hole)
        {
            string name = pattern.GetText(pair.Pattern)[1..].ToString();
            bool discard = pattern.PatternHoleSites.TryGetValue(pair.Pattern, out var site);
            if (absent) return false;
            // A descendant search accepts any node at its site; the named pattern constrains what it finds.
            if (!discard && frame.Pattern.Contracts[name].Descendant && frame.Pattern.References.TryGetValue(name, out var searched))
            {
                work = new(new DescendantJob(frame, searched, new(source, null), name), work);
                return true;
            }
            if (!_language.AcceptsQuoteNode(discard ? site!.Category : frame.Pattern.Contracts[name].Category, source.Tree, source.Node)) return false;
            if (discard) return true;
            // Equal syntax matches the same named pattern, so a repeated reference capture only compares.
            if (_captures.TryGetValue((frame.Scope, name), out var previous)) return Equal(previous.Scalar!, source);
            if (frame.Pattern.References.TryGetValue(name, out var target))
            {
                work = new(new ReferenceJob(frame, target, source, name), work);
                return true;
            }
            Bind((frame.Scope, name), new(source));
            return true;
        }
        if (frame.Pattern.Lists.TryGetValue(pair.Pattern, out var list))
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
            work = new(new SequenceJob(frame, list, items, 0, 0), work);
            return true;
        }
        if (kind == SyntaxKinds.ListHole || absent || kind != source.Tree.Kind(source.Node) ||
            pattern.ChildCount(pair.Pattern) != source.Tree.ChildCount(source.Node)) return false;
        int count = pattern.ChildCount(pair.Pattern);
        if (count == 0 && !pattern.GetText(pair.Pattern).SequenceEqual(source.Tree.GetText(source.Node))) return false;
        for (int i = count - 1; i >= 0; i--)
            work = new(new NodeJob(frame, pattern.Child(pair.Pattern, i), new(source.Tree, source.Tree.Child(source.Node, i), source.Origin)), work);
        return true;
    }

    bool MatchSequence(SequenceJob sequence, ref Pending? work)
    {
        var (frame, plan, items, pi, ii, length) = sequence;
        var pattern = frame.Pattern.Tree;
        if (pi == plan.Items.Length) return ii == items.Length;
        int node = plan.Items[pi];
        if (pattern.Kind(node) != SyntaxKinds.ListHole)
        {
            if (ii >= items.Length) return false;
            work = new(new SequenceJob(frame, plan, items, pi + 1, ii + 1), work);
            work = new(new NodeJob(frame, node, items[ii]), work);
            return true;
        }
        int maximum = items.Length - ii - plan.MinimumRemaining[pi + 1];
        if (maximum < 0) return false;
        string name = pattern.GetText(node)[3..].ToString();
        bool bound = _captures.TryGetValue((frame.Scope, name), out var previous);
        // With no later segment, complete coverage determines this segment's length uniquely.
        if (bound) length = previous!.Count;
        else if (plan.SegmentsRemaining[pi + 1] == 0) length = maximum;
        if (length > maximum) return false;
        budget.Step(); // one attempted split, including fixed/repeated segment lengths
        if (!bound && plan.SegmentsRemaining[pi + 1] > 0 && length < maximum)
            _choices.Push(new(new(sequence with { Length = length + 1 }, work), _trail.Count));
        work = new(new SequenceJob(frame, plan, items, pi + 1, ii + length), work);
        work = new(new SegmentJob(frame, node, items, ii, length), work);
        return true;
    }

    bool MatchSegment(SegmentJob segment, ref Pending? work)
    {
        var frame = segment.Frame;
        var pattern = frame.Pattern.Tree;
        string name = pattern.GetText(segment.Pattern)[3..].ToString();
        bool discard = pattern.PatternHoleSites.TryGetValue(segment.Pattern, out var site);
        string category = discard ? site!.Category : frame.Pattern.Contracts[name].Category;
        for (int i = 0; i < segment.Count; i++)
        {
            budget.Step();
            var item = segment.Items[segment.Start + i];
            if (!_language.AcceptsQuoteNode(category, item.Tree, item.Node)) return false;
        }
        if (discard) return true;
        if (_captures.TryGetValue((frame.Scope, name), out var previous))
        {
            if (previous.Count != segment.Count) return false;
            for (int i = 0; i < segment.Count; i++)
                if (!Equal(previous.Items![previous.Start + i], segment.Items[segment.Start + i])) return false;
            return true;
        }
        Bind((frame.Scope, name), new(null, segment.Items, segment.Start, segment.Count));
        if (frame.Pattern.References.TryGetValue(name, out var target))
            for (int i = segment.Count - 1; i >= 0; i--)
                work = new(new ReferenceJob(frame, target, segment.Items[segment.Start + i], null), work);
        return true;
    }

    bool MatchReference(ReferenceJob reference, ref Pending? work)
    {
        budget.Step();
        var input = Resolve(reference.Input);
        var alternatives = reference.Target.Alternatives;
        if (input.Node == SyntaxTree.NoNode || reference.Alternative >= alternatives.Length ||
            !_language.AcceptsQuoteNode(reference.Target.Category, input.Tree, input.Node)) return false;
        int depth = reference.ChoiceDepth < 0 ? _choices.Count : reference.ChoiceDepth;
        if (reference.Alternative + 1 < alternatives.Length)
            _choices.Push(new(new(reference with { Alternative = reference.Alternative + 1, ChoiceDepth = depth }, work), _trail.Count));
        var alternative = alternatives[reference.Alternative];
        work = new(new BindJob(reference.Caller, reference.BindName, input, depth), work);
        work = new(new NodeJob(new(alternative, ++_scopes), alternative.Tree.Root, input), work);
        return true;
    }

    bool MatchDescendant(DescendantJob descendant, ref Pending? work)
    {
        if (descendant.Remaining is not { } remaining) return false;
        budget.Step();
        var node = Resolve(remaining.Node);
        // Preorder, document order: the node's children come next, before its later siblings.
        var next = remaining.Next;
        for (int i = node.Tree.ChildCount(node.Node) - 1; i >= 0; i--)
            if (node.Tree.Child(node.Node, i) is var child && child != SyntaxTree.NoNode)
                next = new(new(node.Tree, child, node.Origin), next);
        if (next is not null) _choices.Push(new(new(descendant with { Remaining = next }, work), _trail.Count));
        work = new(new ReferenceJob(descendant.Caller, descendant.Target, node, descendant.BindName), work);
        return true;
    }

    bool Commit(BindJob bind)
    {
        while (_choices.Count > bind.ChoiceDepth) _choices.Pop();
        if (bind.Name is null) return true;
        if (_captures.TryGetValue((bind.Caller.Scope, bind.Name), out var previous)) return Equal(previous.Scalar!, bind.Value);
        Bind((bind.Caller.Scope, bind.Name), new(bind.Value));
        return true;
    }

    void Bind((int Scope, string Name) name, Binding value)
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
