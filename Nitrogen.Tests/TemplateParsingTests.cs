using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Parsing templates with holes (spec: quasiquote rewrites, part 1).</summary>
public class TemplateParsingTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();
    static readonly Dictionary<string, string> Holes = new() { ["X"] = "Calc.Expr", ["S"] = "Calc.Statement" };

    /// <summary>The tree as kind names, holes as their text: <c>Mul(Hole $X, Num)</c>.</summary>
    static string Shape(SyntaxTree tree, int node)
    {
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Hole) return "Hole " + tree.GetText(node).ToString();
        string name = tree.Language!.GetKindName(kind);
        name = name[(name.LastIndexOf('.') + 1)..];
        // Tokens (leaves other than holes) are left out: the shape is the nodes.
        var children = Enumerable.Range(0, tree.ChildCount(node)).Select(i => tree.Child(node, i))
            .Where(child => tree.ChildCount(child) > 0 || tree.Kind(child) == SyntaxKinds.Hole).Select(child => Shape(tree, child)).ToList();
        return children.Count == 0 ? name : $"{name}({string.Join(", ", children)})";
    }

    static string Template(string text, string category = "Calc.Expr")
    {
        using var result = Calc.ParseTemplate(text, category, Holes);
        Assert.True(result.Success, $"'{text}' did not parse as {category}");
        return Shape(result.Tree, result.Tree.Root);
    }

    [Fact]
    public void A_hole_parses_as_its_rule_and_operators_extend_past_it()
    {
        Assert.Equal("Hole $X", Template("$X"));
        Assert.Equal("Mul(Hole $X, Num)", Template("$X * 2"));
        Assert.Equal("Add(Hole $X, Mul(Num, Hole $X))", Template("$X + 2 * $X"));
    }

    [Fact]
    public void A_hole_is_one_operand()
    {
        // Neg binds tighter than Mul: -$X * 2 is (-$X) * 2, and the hole is a single leaf.
        Assert.Equal("Mul(Neg(Hole $X), Num)", Template("-$X * 2"));
    }

    [Fact]
    public void A_hole_for_an_alias_rule_stands_where_the_alias_does()
    {
        Assert.Equal("Program(Hole $S)", Template("$S", "Calc.Program"));
    }

    [Fact]
    public void A_hole_matches_only_where_its_rule_is_parsed()
    {
        using var wrongKind = Calc.ParseTemplate("$S * 2", "Calc.Expr", Holes);
        Assert.False(wrongKind.Success);
        using var unknown = Calc.ParseTemplate("$Y * 2", "Calc.Expr", Holes);
        Assert.False(unknown.Success);
    }

    [Fact]
    public void A_normal_parse_never_matches_a_hole()
    {
        using var result = Calc.Parse("$X;", CalcModule.Program);
        Assert.True(result.HasErrors);
    }
}
