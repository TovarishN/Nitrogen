using Nitrogen.Tests.Shapes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// Compact tree shapes: absent optionals and empty lists are null child slots, and a one-item list
/// is its item.
/// </summary>
public class TreeShapeTests
{
    static readonly Language Shapes = new LanguageBuilder().Add(ShapesModule.Instance).Build();

    // Decl = Mods:Mod* "decl" Name:Name Type:(":" TypeName)? "{" Items:(Name; ",")* "}"
    const int ModsSlot = 0, TypeSlot = 3, ItemsSlot = 5;

    // The file's one declaration is its Decls slot.
    static int DeclNode(SyntaxTree tree) => tree.Child(tree.Root, 0);

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
        // File, Decl, "decl", Name, "{", "}": the Empty leaf and two empty lists are gone, and the
        // one-item Decls list is its Decl.
        Assert.Equal(10 - 4, tree.NodeCount);

        var view = SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(tree, decl);
        Assert.False(view.Type.HasValue);
        Assert.Equal(0, view.Mods.Count);
        Assert.Equal(0, view.Items.Count);
        Assert.Equal(0, view.Items.SeparatorCount);
        Assert.Equal("a", view.Name.ToString());
        Assert.Equal("(File (Decl _ \"decl\" Name:\"a\" _ \"{\" _ \"}\"))", SyntaxDumper.Dump(tree));
    }

    [Fact]
    public void Leaf_choice_rule_has_no_node()
    {
        using var result = Shapes.Parse("static decl a : int { }", ShapesModule.File);
        Assert.True(result.Success);
        var tree = result.Tree;
        int decl = DeclNode(tree);
        int mods = tree.Child(decl, ModsSlot);
        Assert.Equal(SyntaxKinds.Literal, tree.Kind(mods));
        Assert.Equal("static", tree.GetText(mods).ToString());
        int type = tree.Child(decl, TypeSlot);
        Assert.Equal(SyntaxKinds.Group, tree.Kind(type));
        int typeName = tree.Child(type, 1);
        Assert.Equal(SyntaxKinds.Literal, tree.Kind(typeName));
        Assert.Equal("int", tree.GetText(typeName).ToString());
        for (int n = 0; n < tree.NodeCount; n++)
        {
            // No Mod or TypeName kind exists in the module any more.
            string name = SyntaxDumper.KindName(tree.Kind(n), Shapes);
            Assert.DoesNotContain("Mod", name);
            Assert.DoesNotContain("TypeName", name);
        }
        // The standing-in leaf casts to Token; the removed wrapper's view does not exist.
        Assert.True(SyntaxView.TryCast<Token>(tree, mods, out var token));
        Assert.Equal("static", token.ToString());
        Assert.Equal("int", new SyntaxNode(tree, type).Child(1).ToString());
        // A leaf-choice alias gets a Token view, so a list of it is a list of tokens.
        SyntaxList<Token> modifiers = SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(tree, decl).Mods;
        Assert.Equal("static", modifiers[0].ToString());
    }

    [Fact]
    public void A_present_optional_and_non_empty_lists_keep_their_nodes()
    {
        using var result = Shapes.Parse("public decl a : int { b, c }", ShapesModule.File);
        var tree = result.Tree;
        int decl = DeclNode(tree);
        Assert.Equal(SyntaxKinds.Literal, tree.Kind(tree.Child(decl, ModsSlot))); // one modifier: the slot is its item
        Assert.Equal(SyntaxKinds.Group, tree.Kind(tree.Child(decl, TypeSlot)));
        Assert.Equal(SyntaxKinds.List, tree.Kind(tree.Child(decl, ItemsSlot)));
        var view = SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(tree, decl);
        Assert.True(view.Type.HasValue);
        Assert.Equal(1, view.Mods.Count);
        Assert.Equal(2, view.Items.Count);
    }

    [Fact]
    public void One_item_list_is_its_item()
    {
        using var result = Shapes.Parse("public decl a { b }", ShapesModule.File);
        Assert.True(result.Success);
        var tree = result.Tree;
        int decl = DeclNode(tree);
        Assert.Equal(ShapesKinds.Decl, tree.Kind(decl));
        int mods = tree.Child(decl, ModsSlot);
        Assert.Equal(SyntaxKinds.Literal, tree.Kind(mods)); // Mod is an alias: the slot is the literal
        Assert.Equal("public", tree.GetText(mods).ToString());
        Assert.Equal(ShapesKinds.Name, tree.Kind(tree.Child(decl, ItemsSlot)));
        // Generic walkers learn the list slots from the language.
        Assert.True(Shapes.IsListSlot(ShapesKinds.Decl, ModsSlot));
        Assert.True(Shapes.IsListSlot(ShapesKinds.Decl, ItemsSlot));
        Assert.False(Shapes.IsListSlot(ShapesKinds.Decl, TypeSlot));

        var view = SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(tree, decl);
        Assert.Equal(1, view.Mods.Count);
        Assert.Equal(1, view.Items.Count);
        Assert.Equal(0, view.Items.SeparatorCount);
        Assert.Equal("b", view.Items[0].ToString());
        var seen = new List<string>();
        foreach (var item in view.Items) seen.Add(item.ToString());
        foreach (var mod in view.Mods) seen.Add(mod.ToString());
        Assert.Equal(["b", "public"], seen);

        using var two = Shapes.Parse("decl a { b, c }", ShapesModule.File);
        int items = two.Tree.Child(DeclNode(two.Tree), ItemsSlot);
        Assert.Equal(SyntaxKinds.List, two.Tree.Kind(items));
        Assert.Equal(3, two.Tree.ChildCount(items));
    }

    [Theory]
    [InlineData("nest a b .", "(Nest \"nest\" (List (List Name:\"a\" Name:\"b\")) \".\")")]
    [InlineData("nest a .", "(Nest \"nest\" (List Name:\"a\") \".\")")]
    [InlineData("nest a ; b c .", "(Nest \"nest\" (List Name:\"a\" \";\" (List Name:\"b\" Name:\"c\")) \".\")")]
    public void A_one_item_list_of_lists_keeps_its_list_node(string text, string dump)
    {
        // An item that is itself a list (collapsed or not) would be ambiguous in the slot.
        using var result = Shapes.Parse(text, ShapesModule.Nest);
        Assert.True(result.Success);
        Assert.Equal(dump, SyntaxDumper.Dump(result.Tree));
        var lines = SyntaxView.Cast<NestNode>(result.Tree, result.Tree.Root).Lines;
        Assert.Equal(SyntaxKinds.List, result.Tree.Kind(result.Tree.Child(result.Tree.Root, 1)));
        Assert.Equal(text.Count(c => c == ';') + 1, lines.Count);
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
        Assert.False(SyntaxView.TryCast<Nitrogen.Tests.Shapes.NestNode>(tree, SyntaxTree.NoNode, out _));
        Assert.Throws<InvalidCastException>(() => SyntaxView.Cast<Nitrogen.Tests.Shapes.NestNode>(tree, SyntaxTree.NoNode));
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

    [Fact]
    public void Transparent_alternative_has_no_node()
    {
        using var result = Shapes.Parse("{ var a ; }", ShapesModule.Block);
        Assert.True(result.Success);
        var tree = result.Tree;
        // Block = "{" Stmts:Stmt* "}": the Stmts list holds the standing-in Var and ";" directly.
        int stmts = tree.Child(tree.Root, 1);
        Assert.Equal(SyntaxKinds.List, tree.Kind(stmts));
        Assert.Equal(2, tree.ChildCount(stmts));
        Assert.Equal(ShapesKinds.Var, tree.Kind(tree.Child(stmts, 0)));
        Assert.Equal(SyntaxKinds.Literal, tree.Kind(tree.Child(stmts, 1)));
        Assert.Equal(";", tree.GetText(tree.Child(stmts, 1)).ToString());
        for (int n = 0; n < tree.NodeCount; n++) Assert.NotEqual(ShapesKinds.Plain, tree.Kind(n));
        Assert.Equal("(Block \"{\" (List (Var \"var\" Name:\"a\") \";\") \"}\")", SyntaxDumper.Dump(tree));

        // One statement: the one-item list is its item, which is the Var itself.
        using var one = Shapes.Parse("{ var b }", ShapesModule.Block);
        Assert.Equal(ShapesKinds.Var, one.Tree.Kind(one.Tree.Child(one.Tree.Root, 1)));
    }

    [Fact]
    public void A_transparent_alternative_stand_in_is_in_the_points_quote_category()
    {
        // A hole of the point accepts what can stand in for it: the Var node and the ";" leaf.
        Assert.Contains("Shapes.Stmt", ShapesModule.Instance.GetQuoteCategories(ShapesModule.LVar));
        Assert.Contains("Shapes.Var", ShapesModule.Instance.GetQuoteCategories(ShapesModule.LVar));
        Assert.DoesNotContain("Shapes.Stmt", ShapesModule.Instance.GetQuoteCategories(ShapesModule.LPlain));
        Assert.True(ShapesModule.Instance.AcceptsQuoteLiteral("Shapes.Stmt", ";"));
        Assert.False(ShapesModule.Instance.AcceptsQuoteLiteral("Shapes.Stmt", "var"));
        Assert.Contains("Shapes.Tie", ShapesModule.Instance.GetQuoteCategories(ShapesModule.LName));
    }

    [Fact]
    public void A_tie_with_a_transparent_alternative_is_ambiguous_over_the_standing_in_node()
    {
        // Tie: | transparent Word = Name | Wrapped = Inner:Name. Both end after "a" at precedence 0.
        using var result = Shapes.Parse("tie a", ShapesModule.Ties);
        Assert.True(result.Success);
        var tree = result.Tree;
        int tie = tree.Child(tree.Root, 1);
        Assert.Equal(SyntaxKinds.Ambiguous, tree.Kind(tie));
        Assert.Equal(2, tree.ChildCount(tie));
        var kinds = new[] { tree.Kind(tree.Child(tie, 0)), tree.Kind(tree.Child(tie, 1)) };
        Assert.Contains(ShapesKinds.Name, kinds);
        Assert.Contains(ShapesKinds.Wrapped, kinds);
        Assert.Equal(new TextSpan(4, 1), tree.Span(tie));
    }
}
