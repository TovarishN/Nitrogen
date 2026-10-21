using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteSequencePatternTests
{
    static readonly Language Language = TemplateListInstantiationTests.CreateLanguage();
    static readonly Dictionary<string, QuoteHole> Captures = new()
    {
        ["X"] = QuoteHole.Node("QuoteLists.Expr"), ["Y"] = QuoteHole.Node("QuoteLists.Expr"),
        ["Z"] = QuoteHole.Node("QuoteLists.Expr"), ["A"] = QuoteHole.List("QuoteLists.Expr"),
        ["B"] = QuoteHole.List("QuoteLists.Expr"), ["C"] = QuoteHole.List("QuoteLists.Expr"),
    };

    static Dictionary<string, string>? Match(string pattern, string source, params string[] names)
    {
        using var compiled = Language.CompilePattern("QuoteLists.Expr", pattern, names.ToDictionary(n => n, n => Captures[n]));
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        using var input = Language.ParseTemplate(source, "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.True(input.Success);
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.Empty(result.Diagnostics);
        if (!result.Matched) { Assert.Empty(result.Captures); return null; }
        return result.Captures.ToDictionary(p => p.Key, p => p.Value switch
        {
            QuoteArgument.Scalar scalar => scalar.Value.Tree.GetText(scalar.Value.Node).ToString(),
            QuoteArgument.Many many => string.Join("|", many.Values.Select(v => v.Tree.GetText(v.Node).ToString())),
            _ => throw new InvalidOperationException(),
        });
    }

    [Fact]
    public void One_segment_with_fixed_suffix_keeps_linear_work()
    {
        using var compiled = Language.CompilePattern("QuoteLists.Expr", "f(0, ..$A, 2)",
            new Dictionary<string, QuoteHole> { ["A"] = Captures["A"] });
        using var source = Language.ParseTemplate("f(0," + string.Join(",", Enumerable.Repeat("1", 300)) + ",2)", "QuoteLists.Expr", new Dictionary<string, string>());
        var result = compiled.Pattern!.Match(new(source.Tree, source.Tree.Root), new QuoteMatchOptions { MaxWorkSteps = 5_000 });
        Assert.Empty(result.Diagnostics);
        Assert.True(result.Matched);
        Assert.Equal(300, Assert.IsType<QuoteArgument.Many>(result.Captures["A"]).Values.Count);
    }

    [Fact]
    public void Deep_nested_sequence_matching_uses_explicit_work_stacks()
    {
        string pattern = "$_", input = "1";
        for (int i = 0; i < 250; i++) { pattern = "f(..$_, " + pattern + ", ..$_)"; input = "f(" + input + ")"; }
        Assert.Empty(Match(pattern, input)!);
    }

    [Fact]
    public void Exhaustion_after_partial_captures_returns_no_bindings()
    {
        using var compiled = Language.CompilePattern("QuoteLists.Expr", "f($X, ..$_, $_, ..$_, $_, ..$_, $_, ..$_, 999)",
            new Dictionary<string, QuoteHole> { ["X"] = Captures["X"] });
        Assert.True(compiled.Success);
        using var input = Language.ParseTemplate("f(" + string.Join(",", Enumerable.Repeat("1", 35)) + ")", "QuoteLists.Expr", new Dictionary<string, string>());
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root), new QuoteMatchOptions { MaxWorkSteps = 2_000 });
        Assert.Equal("NQP0006", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Captures);
    }

    [Fact]
    public void Selected_elements_use_earliest_viable_positions()
    {
        var result = Match("f(..$_, $X, ..$_, $Y, ..$_)", "f(1, 2, 3)", "X", "Y")!;
        Assert.Equal("1", result["X"]); Assert.Equal("2", result["Y"]);
        result = Match("f(..$_, $X, ..$_, $Y, ..$_, $Z, ..$_)", "f(1, 2, 3, 4)", "X", "Y", "Z")!;
        Assert.Equal("1", result["X"]); Assert.Equal("2", result["Y"]); Assert.Equal("3", result["Z"]);
        Assert.Null(Match("f(..$_, $X, ..$_, $Y, ..$_)", "f(1)", "X", "Y"));
        result = Match("f(..$_, $X, $_, $Y, ..$_)", "f(1, 2, 3, 4)", "X", "Y")!;
        Assert.Equal("1", result["X"]); Assert.Equal("3", result["Y"]);
    }

    [Fact]
    public void Adjacent_segments_are_shortest_first()
    {
        var result = Match("f(..$A, ..$B)", "f(1, 2)", "A", "B")!;
        Assert.Equal("", result["A"]); Assert.Equal("1|2", result["B"]);
        Assert.Empty(Match("f(..$_, ..$_, ..$_)", "f(1, 2, 3)")!);
        Assert.Empty(Match("f(..$_, ..$_)", "f()")!);
    }

    [Fact]
    public void Later_equality_reopens_nested_choices()
    {
        var result = Match("f(f(..$_, $X, ..$_), $X)", "f(f(1, 2), 2)", "X")!;
        Assert.Equal("2", result["X"]);
        result = Match("f(f(..$A, ..$_), f(..$A))", "f(f(1, 2), f(1))", "A")!;
        Assert.Equal("1", result["A"]);
    }

    [Theory]
    [InlineData("f(..$_, $X, ..$_, $X, ..$_)", "f(1, 2, 2, 3)", "2")]
    [InlineData("f(..$_, $X, ..$_, $X, ..$_)", "f(1, 2, 3)", null)]
    [InlineData("f(..$_, $X, ..$_, 2)", "f(9, 1, 2)", "9")]
    [InlineData("space[..$_ $X ..$_]", "space[1 2]", "1")]
    [InlineData("required(..$_, $X, ..$_)", "required(1)", "1")]
    public void Search_obeys_structure_and_repeated_bindings(string pattern, string source, string? expected)
    {
        var result = Match(pattern, source, "X");
        Assert.Equal(expected, result?.GetValueOrDefault("X"));
    }

    [Theory]
    [InlineData("f()", "")]
    [InlineData("f(1, 1)", "1")]
    [InlineData("f(1, 2, 1, 2)", "1|2")]
    [InlineData("f(1, 2)", null)]
    public void Repeated_list_segments_in_one_list_require_equality(string source, string? expected)
        => Assert.Equal(expected, Match("f(..$A, ..$A)", source, "A")?.GetValueOrDefault("A"));

    [Fact]
    public void Captures_do_not_leak_between_invocations_or_failed_branches()
    {
        using var compiled = Language.CompilePattern("QuoteLists.Expr", "f(..$_, $X, ..$_, $X, ..$_)",
            new Dictionary<string, QuoteHole> { ["X"] = Captures["X"] });
        Assert.True(compiled.Success);
        using var input = Language.ParseTemplate("f(1, 2, 2)", "QuoteLists.Expr", new Dictionary<string, string>());
        using var miss = Language.ParseTemplate("f(1, 2, 3)", "QuoteLists.Expr", new Dictionary<string, string>());
        for (int i = 0; i < 2; i++)
        {
            Assert.True(compiled.Pattern!.Match(new(input.Tree, input.Tree.Root)).Matched);
            var mismatch = compiled.Pattern.Match(new(miss.Tree, miss.Tree.Root));
            Assert.False(mismatch.Matched); Assert.Empty(mismatch.Captures);
        }
    }
}
