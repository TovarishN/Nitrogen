using System.Collections.ObjectModel;

namespace Nitrogen;

internal sealed class QuotePatternMatcher(SyntaxTree pattern, IReadOnlyDictionary<string, QuoteHole> contracts,
    Func<QuoteNode, QuoteNode>? resolve)
{
    static readonly IReadOnlyDictionary<string, QuoteArgument> s_empty =
        new ReadOnlyDictionary<string, QuoteArgument>(new Dictionary<string, QuoteArgument>());
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
        var work = new Stack<(int Pattern, QuoteNode Input)>();
        work.Push((pattern.Root, input));
        while (work.TryPop(out var pair))
        {
            var source = Resolve(pair.Input);
            int kind = pattern.Kind(pair.Pattern);
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
            if (kind != source.Tree.Kind(source.Node) || pattern.ChildCount(pair.Pattern) != source.Tree.ChildCount(source.Node)) return Mismatch();
            int count = pattern.ChildCount(pair.Pattern);
            if (count == 0 && !pattern.GetText(pair.Pattern).SequenceEqual(source.Tree.GetText(source.Node))) return Mismatch();
            for (int i = count - 1; i >= 0; i--)
                work.Push((pattern.Child(pair.Pattern, i), new(source.Tree, source.Tree.Child(source.Node, i), source.Origin)));
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
