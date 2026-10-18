using System.Reflection;
using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public unsafe class TreeFreezerTests
{
    const int K1 = (99 << 16) | 1;
    const int K2 = (99 << 16) | 2;

    static ParserState State(string text, BuildArena arena) =>
        new(text, arena, new MemoTable(), &StandardTrivia.WhitespaceAndComments);

    [Theory]
    [InlineData(TreeFreezer.PooledTreeNodes)]
    [InlineData(0)] // forces exact-size arrays
    public void Freeze_renumbers_reachable_nodes_in_preorder(int pooledTreeNodes)
    {
        var a = new BuildArena();
        var s = State("a  b", a);
        a.NewNode(K2, 0, 0, NodeFlags.None, 0, 0); // dead node: never pushed, must not survive
        s.Open(K1);
        s.MatchLiteral("a");
        s.SkipTrivia();
        s.Open(K2);
        s.MatchLiteral("b");
        s.Close();
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, "a  b", language: null, pooledTreeNodes);

        Assert.Equal(pooledTreeNodes > 0, tree.IsPooled);
        Assert.Equal(4, tree.NodeCount);
        Assert.Equal(new[] { K1, SyntaxKinds.Literal, K2, SyntaxKinds.Literal },
            Enumerable.Range(0, 4).Select(tree.Kind));
        Assert.Equal(new TextSpan(0, 4), tree.Span(0));
        Assert.Equal(1, tree.Child(0, 0));
        Assert.Equal(2, tree.Child(0, 1));
        Assert.Equal(3, tree.Child(2, 0));
        Assert.Equal(-1, tree.Parent(0));
        Assert.Equal(2, tree.Parent(3));
        Assert.Equal("b", tree.GetText(3).ToString());
        Assert.Equal(new[] { new TextSpan(1, 2) }, tree.Trivia.ToArray());
    }

    [Theory]
    [InlineData(TreeFreezer.PooledTreeNodes)]
    [InlineData(0)] // forces exact-size arrays
    public void Freeze_records_leading_and_trailing_trivia(int pooledTreeNodes)
    {
        var a = new BuildArena();
        var s = State(" a ", a);
        s.Open(K1);
        s.SkipTrivia();
        s.MatchLiteral("a");
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, " a ", language: null, pooledTreeNodes);

        Assert.Equal(new[] { new TextSpan(0, 1), new TextSpan(2, 1) }, tree.Trivia.ToArray());
    }

    [Theory]
    [InlineData(TreeFreezer.PooledTreeNodes)]
    [InlineData(0)] // forces exact-size arrays
    public void Freeze_copies_a_shared_node_once_per_occurrence(int pooledTreeNodes)
    {
        var a = new BuildArena();
        a.HasAmbiguity = true; // a hand-built shared subtree, as only ambiguity produces in a real parse
        var s = State("x", a);
        s.MatchLiteral("x");
        int shared = a.ChildStack[0];
        a.ChildStackCount = 0;
        s.Open(K1);
        a.PushChild(shared);
        a.PushChild(shared);
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, "x", language: null, pooledTreeNodes);

        Assert.Equal(3, tree.NodeCount);
        Assert.NotEqual(tree.Child(0, 0), tree.Child(0, 1));
        Assert.Equal(tree.Span(tree.Child(0, 0)), tree.Span(tree.Child(0, 1)));
    }

    [Theory]
    [InlineData(TreeFreezer.PooledTreeNodes)]
    [InlineData(0)] // forces exact-size arrays
    public void Child_index_out_of_range_throws(int pooledTreeNodes)
    {
        var a = new BuildArena();
        var s = State("x", a);
        s.Open(K1);
        s.MatchLiteral("x");
        int root = s.Close();
        using var tree = TreeFreezer.Freeze(a, root, "x", language: null, pooledTreeNodes);

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Child(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Kind(2));
    }

    [Fact]
    public void ErrorTree_is_a_single_error_root()
    {
        using var tree = TreeFreezer.ErrorTree("oops", language: null);
        Assert.Equal(1, tree.NodeCount);
        Assert.Equal(SyntaxKinds.Error, tree.Kind(0));
        Assert.Equal(NodeFlags.Error, tree.Flags(0));
        Assert.Equal(new TextSpan(0, 4), tree.Span(0));
        Assert.Equal(0, tree.ChildCount(0));
        Assert.Equal(0, tree.Trivia.Length);
    }

    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();

    static object? Field(SyntaxTree tree, string name) =>
        typeof(SyntaxTree).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tree);

    [Fact]
    public void Kinds_round_trip_through_dense_ids()
    {
        using var result = Calc.Parse("x = -(1 + 2.5) * f(a, 3);\nf();\ny;", CalcModule.Program);
        var tree = result.Tree;

        Assert.IsType<ushort[]>(Field(tree, "_kind"));
        // The kinds before dense storage, by name in preorder, and the dump.
        Assert.Equal(
            "Program List Assign Ref Identifier Literal Mul Neg Literal Paren Literal Add Num Number Literal Num Number Literal Literal "
            + "Call Identifier Literal List Ref Identifier Literal Num Number Literal Literal ExprStatement Call Identifier Literal Literal "
            + "Literal ExprStatement Ref Identifier Literal",
            string.Join(" ", Enumerable.Range(0, tree.NodeCount).Select(n => Calc.GetKindName(tree.Kind(n)))));
        Assert.Equal(
            "(Program (List (Assign (Ref Identifier:\"x\") \"=\" (Mul (Neg \"-\" (Paren \"(\" (Add (Num Number:\"1\") \"+\" (Num Number:\"2.5\")) \")\")) \"*\" "
            + "(Call Identifier:\"f\" \"(\" (List (Ref Identifier:\"a\") \",\" (Num Number:\"3\")) \")\")) \";\") "
            + "(ExprStatement (Call Identifier:\"f\" \"(\" _ \")\") \";\") (ExprStatement (Ref Identifier:\"y\") \";\")))",
            SyntaxDumper.Dump(tree));
        for (int n = 0; n < tree.NodeCount; n++)
        {
            int kind = tree.Kind(n);
            Assert.Equal(kind, Calc.KindOf(Calc.DenseKind(kind)));
            Assert.Equal(SyntaxKinds.ModuleOf(kind) == 0, kind is SyntaxKinds.List or SyntaxKinds.Literal);
        }
    }

    [Fact]
    public void Built_in_kinds_are_pre_assigned_dense_ids()
    {
        var table = new DenseKindTable();
        for (int kind = SyntaxKinds.None; kind <= SyntaxKinds.ListHole; kind++)
        {
            Assert.Equal(kind, table.DenseKind(kind));
            Assert.Equal(kind, table.KindOf((ushort)kind));
        }
        Assert.Equal(SyntaxKinds.ListHole + 1, table.DenseKind((1 << 16) | 7));
        Assert.Equal(SyntaxKinds.Literal, Calc.DenseKind(SyntaxKinds.Literal));
    }

    [Theory]
    [InlineData(TreeFreezer.PooledTreeNodes)]
    [InlineData(0)] // forces exact-size arrays
    public void A_tree_without_a_language_keeps_any_kind(int pooledTreeNodes)
    {
        const int far = (0xFFFF << 16) | 0xFFFF;
        var a = new BuildArena();
        var s = State("x", a);
        s.Open(far);
        s.Open(K1);
        s.MatchLiteral("x");
        s.Close();
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, "x", language: null, pooledTreeNodes);

        Assert.Null(tree.Language);
        Assert.Equal(new[] { far, K1, SyntaxKinds.Literal }, Enumerable.Range(0, 3).Select(tree.Kind));
    }

    [Fact]
    public void The_dense_kind_table_throws_past_65535_kinds()
    {
        var table = new DenseKindTable();
        int assigned = SyntaxKinds.ListHole + 1, local = 0;
        for (; assigned <= ushort.MaxValue; assigned++, local++)
            Assert.Equal(assigned, table.DenseKind((1 + local / 0x10000) << 16 | local % 0x10000));
        Assert.Throws<InvalidOperationException>(() => { table.DenseKind((5 << 16) | 1); });
        Assert.Equal(ushort.MaxValue, table.DenseKind((1 + (local - 1) / 0x10000) << 16 | (local - 1) % 0x10000));
    }

    [Fact]
    public void Concurrent_readers_and_writers_agree_on_dense_ids()
    {
        var table = new DenseKindTable();
        const int threads = 8, kinds = 4000;
        var seen = new ushort[threads][];
        Parallel.For(0, threads, t =>
        {
            seen[t] = new ushort[kinds];
            for (int i = 0; i < kinds; i++)
            {
                int k = (i * 7 + t * 131) % kinds; // each thread walks the kinds in its own order
                int kind = ((1 + k % 5) << 16) | (k * 3);
                ushort dense = table.DenseKind(kind);
                Assert.Equal(kind, table.KindOf(dense));
                seen[t][k] = dense;
            }
        });
        for (int t = 1; t < threads; t++) Assert.Equal(seen[0], seen[t]);
        Assert.Equal(kinds, seen[0].Distinct().Count());
    }

    [Theory]
    [InlineData(TreeFreezer.PooledTreeNodes)]
    [InlineData(0)] // forces exact-size arrays
    public void Trivia_is_built_on_first_access(int pooledTreeNodes)
    {
        var a = new BuildArena();
        var s = State(" a ", a);
        s.Open(K1);
        s.SkipTrivia();
        s.MatchLiteral("a");
        int root = s.Close();

        using var tree = TreeFreezer.Freeze(a, root, " a ", language: null, pooledTreeNodes);

        Assert.Null(Field(tree, "_trivia"));
        Assert.Equal(new[] { new TextSpan(0, 1), new TextSpan(2, 1) }, tree.Trivia.ToArray());
        Assert.NotNull(Field(tree, "_trivia"));
        Assert.Equal(new[] { new TextSpan(0, 1), new TextSpan(2, 1) }, tree.Trivia.ToArray());
    }
}
