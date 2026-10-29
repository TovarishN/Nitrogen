namespace Nitrogen.Semantics;

public sealed partial class FileSemantics
{
    HashSet<int>? _invalidSubtrees;

    /// <summary>Nodes whose subtree has recovered, ambiguous or template syntax; computed once and shared by every pattern check.</summary>
    internal HashSet<int> InvalidSubtrees => LazyInitializer.EnsureInitialized(ref _invalidSubtrees,
        () => QuotePatternMatcher.InvalidSubtrees(new(Tree, Tree.Root), new QuoteMatchBudget(new QuoteMatchOptions { MaxWorkSteps = int.MaxValue })));
}
