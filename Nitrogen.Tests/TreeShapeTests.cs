using Nitrogen.Tests.Shapes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Compact tree shapes: absent optionals and empty lists are null child slots.</summary>
public class TreeShapeTests
{
    static readonly Language Shapes = new LanguageBuilder().Add(ShapesModule.Instance).Build();

    // Decl = Mods:Mod* "decl" Name:Name Type:(":" TypeName)? "{" Items:(Name; ",")* "}"
    const int ModsSlot = 0, TypeSlot = 3, ItemsSlot = 5;

    static int DeclNode(SyntaxTree tree) => tree.Child(tree.Child(tree.Root, 0), 0);

    [Fact]
    public void Absent_optional_and_empty_list_are_null_slots()
    {
        using var result = Shapes.Parse("decl a { }", ShapesModule.File);
        Assert.True(result.Success);
        var tree = result.Tree;
        int decl = DeclNode(tree);

        Assert.Equal(7, tree.ChildCount(decl));
        Assert.Equal(SyntaxTree.NoNode, tree.Child(decl, ModsSlot));
        Assert.Equal(SyntaxTree.NoNode, tree.Child(decl, TypeSlot));
        Assert.Equal(SyntaxTree.NoNode, tree.Child(decl, ItemsSlot));
        // File, Decls list, Decl, "decl", Name, "{", "}": the Empty leaf and two empty lists are gone.
        Assert.Equal(10 - 3, tree.NodeCount);

        var view = SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(tree, decl);
        Assert.False(view.Type.HasValue);
        Assert.Equal(0, view.Mods.Count);
        Assert.Equal(0, view.Items.Count);
        Assert.Equal(0, view.Items.SeparatorCount);
        Assert.Equal("a", view.Name.ToString());
        Assert.Equal("(File (List (Decl _ \"decl\" Name:\"a\" _ \"{\" _ \"}\")))", SyntaxDumper.Dump(tree));
    }

    [Fact]
    public void A_present_optional_and_non_empty_lists_keep_their_nodes()
    {
        using var result = Shapes.Parse("public decl a : int { b, c }", ShapesModule.File);
        var tree = result.Tree;
        int decl = DeclNode(tree);
        Assert.Equal(SyntaxKinds.List, tree.Kind(tree.Child(decl, ModsSlot)));
        Assert.Equal(SyntaxKinds.Group, tree.Kind(tree.Child(decl, TypeSlot)));
        Assert.Equal(SyntaxKinds.List, tree.Kind(tree.Child(decl, ItemsSlot)));
        var view = SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(tree, decl);
        Assert.True(view.Type.HasValue);
        Assert.Equal(1, view.Mods.Count);
        Assert.Equal(2, view.Items.Count);
    }

    [Fact]
    public void A_null_slot_is_the_null_node_and_casts_cleanly()
    {
        using var result = Shapes.Parse("decl a { }", ShapesModule.File);
        var tree = result.Tree;
        int decl = DeclNode(tree);
        var node = new SyntaxNode(tree, decl);
        Assert.True(node.Child(TypeSlot).IsNull);
        Assert.Equal("", node.Child(TypeSlot).ToString());
        Assert.False(node.Child(2).IsNull);
        Assert.False(SyntaxView.TryCast<Token>(tree, SyntaxTree.NoNode, out _));
        Assert.False(SyntaxView.TryCast<SyntaxNode>(tree, SyntaxTree.NoNode, out _));
        Assert.False(SyntaxView.TryCast<Nitrogen.Tests.Shapes.TypeNameNode>(tree, SyntaxTree.NoNode, out _));
        Assert.Throws<InvalidCastException>(() => SyntaxView.Cast<Nitrogen.Tests.Shapes.TypeNameNode>(tree, SyntaxTree.NoNode));
        Assert.Equal("_", SyntaxDumper.Dump(tree, SyntaxTree.NoNode));
    }

    [Fact]
    public void Null_slots_have_no_parent_entry_and_spans_skip_them()
    {
        using var result = Shapes.Parse("  decl a { }  ", ShapesModule.File);
        var tree = result.Tree;
        int decl = DeclNode(tree);
        Assert.Equal(new TextSpan(2, 10), tree.Span(decl));
        for (int n = 1; n < tree.NodeCount; n++) Assert.True(tree.Parent(n) >= 0);
        // The file is a single empty list: its span is empty, where the list would be.
        using var empty = Shapes.Parse("  ", ShapesModule.File);
        Assert.Equal(1, empty.Tree.NodeCount);
        Assert.Equal(SyntaxTree.NoNode, empty.Tree.Child(0, 0));
        Assert.Equal(new TextSpan(2, 0), empty.Tree.Span(0));
        Assert.Equal("(File _)", SyntaxDumper.Dump(empty.Tree));
    }
}
