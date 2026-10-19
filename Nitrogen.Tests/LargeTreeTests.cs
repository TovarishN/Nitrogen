using Nitrogen.Tests.Recovery;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// Trees past <see cref="TreeFreezer.PooledTreeNodes"/> own exact-size arrays instead of pooled
/// ones. A file of repeated items must freeze to the one-item tree repeated, on both the fast and
/// the recovery path.
/// </summary>
public class LargeTreeTests
{
    static readonly Language Recovery = new LanguageBuilder().Add(RecoveryModule.Instance).Build();

    const string Good = "item a { x = 1; }\n";
    const string Bad = "item a { x = 1 ?? ; }\n";
    const int Items = 20_000;

    static string Repeat(string item, int count) => string.Concat(Enumerable.Repeat(item, count));

    /// <summary>The dump of the single item in a one-item file: <c>(File item)</c> minus the wrapper (a one-item list is its item).</summary>
    static string ItemDump(string item)
    {
        using var result = Recovery.Parse(item, RecoveryModule.File);
        Assert.True(result.Tree.IsPooled);
        var dump = SyntaxDumper.Dump(result.Tree);
        return dump["(File ".Length..^1];
    }

    [Fact]
    public void A_large_tree_owns_exact_arrays_and_matches_the_pooled_shape()
    {
        string text = Repeat(Good, Items);
        using var result = Recovery.Parse(text, RecoveryModule.File);
        var tree = result.Tree;

        Assert.True(result.Success);
        Assert.False(tree.IsPooled);
        Assert.True(tree.NodeCount > TreeFreezer.PooledTreeNodes, $"input too small: {tree.NodeCount} nodes");
        Assert.Equal(result.Stats.TreeNodes, tree.NodeCount);
        Assert.Equal("(File (List " + string.Join(" ", Enumerable.Repeat(ItemDump(Good), Items)) + "))", SyntaxDumper.Dump(tree));

        Assert.Null(typeof(SyntaxTree).GetField("_trivia", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tree));
        TextSpan[] one;
        using (var small = Recovery.Parse(Good, RecoveryModule.File)) one = small.Tree.Trivia.ToArray();
        var expected = Enumerable.Range(0, Items)
            .SelectMany(k => one.Select(t => new TextSpan(t.Start + k * Good.Length, t.Length)));
        Assert.Equal(expected, tree.Trivia.ToArray());
    }

    [Fact]
    public void A_large_recovered_tree_owns_exact_arrays_and_matches_the_pooled_shape()
    {
        string text = Repeat(Good, Items - 1) + Bad;
        using var result = Recovery.Parse(text, RecoveryModule.File);
        var tree = result.Tree;

        Assert.False(result.Success);
        Assert.False(tree.IsPooled);
        Assert.Equal(new[] { new TextSpan((Items - 1) * Good.Length + 15, 2) }, tree.SkippedSpans.ToArray());
        Assert.Equal("(File (List " + string.Join(" ", Enumerable.Repeat(ItemDump(Good), Items - 1).Append(ItemDump(Bad))) + "))",
            SyntaxDumper.Dump(tree));

        // Flags live in the top bits of the exact-size kind array: the bad item's flags survive there.
        NodeFlags[] FlagsOf(SyntaxTree t, int from) => Enumerable.Range(from, t.NodeCount - from).Select(t.Flags).ToArray();
        using var bad = Recovery.Parse(Bad, RecoveryModule.File);
        var badFlags = FlagsOf(bad.Tree, 1);
        Assert.Contains(badFlags, f => (f & NodeFlags.Skipped) != 0);
        Assert.Equal(badFlags, FlagsOf(tree, tree.NodeCount - badFlags.Length));
        Assert.All(FlagsOf(tree, 0)[..^badFlags.Length], f => Assert.Equal(NodeFlags.None, f));
    }

    [Fact]
    public void A_large_tree_has_consistent_parents_and_throws_after_disposal()
    {
        var result = Recovery.Parse(Repeat(Good, Items), RecoveryModule.File);
        var tree = result.Tree;

        Assert.Equal(-1, tree.Parent(0));
        for (int n = 0; n < tree.NodeCount; n++)
            for (int k = 0; k < tree.ChildCount(n); k++)
                Assert.Equal(n, tree.Parent(tree.Child(n, k)));

        result.Dispose();
        Assert.True(tree.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => tree.Kind(0));
    }
}
