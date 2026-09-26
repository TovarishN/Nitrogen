using Nitrogen.Tests.Mini;
using Xunit;

namespace Nitrogen.Tests;

public class MiniParsingTests
{
    static readonly Language Mini = new LanguageBuilder().Add(MiniModule.Instance).Build();

    [Fact]
    public void Sequence_with_trivia()
    {
        using var result = Mini.Parse("( hello )", MiniModule.Pair);
        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics.ToArray());
        Assert.Equal("(Pair \"(\" Word:\"hello\" \")\")", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(1, 1), new TextSpan(7, 1) }, result.Tree.Trivia.ToArray());
    }

    [Fact]
    public void Ordered_choice_backtracks_and_truncates_the_arena()
    {
        // "a", "b" and "c" are keywords in the generated parser, so they need a boundary.
        using var result = Mini.Parse("a c", MiniModule.Choice);
        Assert.True(result.Success);
        Assert.Equal("(Ac \"a\" \"c\")", SyntaxDumper.Dump(result.Tree));
        // The failed Ab attempt left nothing behind: Ac node plus two literals.
        Assert.Equal(3, result.Stats.ArenaNodes);
        Assert.Equal(3, result.Stats.TreeNodes);
    }

    [Fact]
    public void Failure_reports_every_expectation_at_the_furthest_position()
    {
        using var result = Mini.Parse("a x", MiniModule.Choice);
        Assert.False(result.Success);
        Assert.True(result.HasErrors);
        var diagnostic = Assert.Single(result.Diagnostics.ToArray());
        Assert.Equal(DiagnosticCode.Expected, diagnostic.Code);
        Assert.Equal(new TextSpan(2, 0), diagnostic.Span);
        Assert.Equal("expected 'b' or 'c'", result.FormatMessage(diagnostic));
        // Recovery (issue 235): "c" is committed after "a"; the stray "x" is skipped.
        Assert.Equal("(Ac~ \"a\" !Literal)", SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void Keyword_needs_a_boundary()
    {
        using var ok = Mini.Parse("if x", MiniModule.If);
        Assert.Equal("(If \"if\" Word:\"x\")", SyntaxDumper.Dump(ok.Tree));

        using var bad = Mini.Parse("iffy", MiniModule.If);
        Assert.False(bad.Success);
        Assert.Equal("expected 'if'", bad.FormatMessage(bad.Diagnostics[0]));
    }

    [Fact]
    public void Absent_optional_is_an_empty_node()
    {
        using var absent = Mini.Parse("()", MiniModule.Maybe);
        Assert.Equal("(Maybe \"(\" _ \")\")", SyntaxDumper.Dump(absent.Tree));

        using var present = Mini.Parse("( x )", MiniModule.Maybe);
        Assert.Equal("(Maybe \"(\" Word:\"x\" \")\")", SyntaxDumper.Dump(present.Tree));
    }

    [Fact]
    public void Trailing_input_is_an_error()
    {
        using var result = Mini.Parse("( a ) x", MiniModule.Pair);
        Assert.False(result.Success);
        Assert.Equal(new TextSpan(6, 0), result.Diagnostics[0].Span);
        Assert.Equal("expected end of input", result.FormatMessage(result.Diagnostics[0]));
    }

    [Fact]
    public void Kind_names_resolve_through_the_language()
    {
        Assert.Equal("Pair", Mini.GetKindName(MiniModule.Kind(MiniModule.LPair)));
        Assert.Equal("List", Mini.GetKindName(SyntaxKinds.List));
        Assert.Equal(MiniModule.Instance.Id, SyntaxKinds.ModuleOf(MiniModule.Kind(MiniModule.LPair)));
    }

    [Fact]
    public void Adding_a_module_twice_is_harmless()
    {
        var language = new LanguageBuilder().Add(MiniModule.Instance).Add(MiniModule.Instance).Build();
        Assert.Single(language.Modules);
    }

    [Fact]
    public void Early_first_character_failure_reports_the_same_expectation()
    {
        using var result = Mini.Parse("x", MiniModule.Choice);
        Assert.Equal(new TextSpan(0, 0), result.Diagnostics[0].Span);
        Assert.Equal("expected 'a'", result.FormatMessage(result.Diagnostics[0]));
    }
}
