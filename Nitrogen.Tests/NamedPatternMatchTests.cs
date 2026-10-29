using Xunit;
using static Nitrogen.Tests.NamedPatternTestModule;

namespace Nitrogen.Tests;

public sealed class NamedPatternMatchTests
{
    static readonly Language Language = Compose(
        Spec("Lit", Alt("1"), Alt("2")),
        Spec("Pair", Alt("$a + $a", ("a", QuoteHole.Node(Expr)))),
        Spec("Nested", Alt("f($n)", ("n", QuoteHole.Pattern("P.Nested"))), Alt("1")),
        Spec("Wide", Alt("f(..$a, ..$_)", ("a", QuoteHole.List(Expr))), Alt("1")),
        Spec("One", Alt("1")),
        Spec("Two", Alt("2")));

    internal static Language Shared => Language;

    // Captures borrow the input tree, so results are read before the input is disposed.
    internal static T Run<T>(string pattern, string source, QuoteMatchOptions? options, Func<QuoteMatchResult, T> read,
        params (string Name, QuoteHole Hole)[] captures)
    {
        using var compiled = Language.CompilePattern(Expr, pattern, captures.ToDictionary(c => c.Name, c => c.Hole));
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        using var input = Language.ParseTemplate(source, Expr, new Dictionary<string, string>());
        Assert.True(input.Success, source);
        return read(compiled.Pattern!.Match(new(input.Tree, input.Tree.Root), options ?? new QuoteMatchOptions()));
    }

    internal static QuoteMatchResult Run(string pattern, string source, QuoteMatchOptions? options, params (string Name, QuoteHole Hole)[] captures)
        => Run(pattern, source, options, result => result, captures);

    internal static Dictionary<string, string>? Match(string pattern, string source, params (string Name, QuoteHole Hole)[] captures)
        => Run(pattern, source, null, Texts, captures);

    internal static Dictionary<string, string>? Texts(QuoteMatchResult result)
    {
        Assert.Empty(result.Diagnostics);
        if (!result.Matched) { Assert.Empty(result.Captures); return null; }
        return result.Captures.ToDictionary(p => p.Key, p => p.Value switch
        {
            QuoteArgument.Scalar scalar => scalar.Value.Tree.GetText(scalar.Value.Node).ToString(),
            QuoteArgument.Many many => string.Join("|", many.Values.Select(v => v.Tree.GetText(v.Node).ToString())),
            _ => throw new InvalidOperationException(),
        });
    }

    internal static string Deep(int depth) => string.Concat(Enumerable.Repeat("f(", depth)) + "1" + new string(')', depth);

    [Fact]
    public void Second_alternative_is_tried_on_alternative_mismatch()
    {
        Assert.Equal("2", Match("f($x)", "f(2)", ("x", QuoteHole.Pattern("P.Lit")))!["x"]);
        Assert.Null(Match("f($x)", "f(3)", ("x", QuoteHole.Pattern("P.Lit"))));
    }

    [Fact]
    public void Repeated_reference_capture_uses_equality()
    {
        Assert.NotNull(Match("f($x, $x)", "f(2, 2)", ("x", QuoteHole.Pattern("P.Lit"))));
        Assert.Null(Match("f($x, $x)", "f(1, 2)", ("x", QuoteHole.Pattern("P.Lit"))));
    }

    [Fact]
    public void Successful_reference_is_committed()
    {
        string wide = "f(" + string.Join(", ", Enumerable.Repeat("1", 200)) + ")";
        var result = Run("f($x, 9)", $"f({wide}, 8)", new QuoteMatchOptions { MaxWorkSteps = 2_000 }, ("x", QuoteHole.Pattern("P.Wide")));
        Assert.Empty(result.Diagnostics);
        Assert.False(result.Matched);
    }

    [Fact]
    public void Private_captures_do_not_leak()
    {
        var captures = Match("f($x)", "f(1 + 1)", ("x", QuoteHole.Pattern("P.Pair")))!;
        Assert.Equal(["x"], captures.Keys);
        Assert.Equal("1 + 1", captures["x"]);
    }

    [Fact]
    public void Same_capture_name_in_caller_and_callee_is_independent()
    {
        var captures = Match("f($a, $x)", "f(5, 1 + 1)", ("a", QuoteHole.Node(Expr)), ("x", QuoteHole.Pattern("P.Pair")))!;
        Assert.Equal("5", captures["a"]);
        Assert.Equal("1 + 1", captures["x"]);
    }

    [Fact]
    public void Callee_repeated_capture_equality_still_applies()
    {
        Assert.Null(Match("f($x)", "f(1 + 2)", ("x", QuoteHole.Pattern("P.Pair"))));
    }

    [Fact]
    public void Recursive_pattern_matches_several_depths()
    {
        foreach (string source in new[] { "1", "f(1)", "f(f(f(1)))" })
            Assert.NotNull(Match("$x", source, ("x", QuoteHole.Pattern("P.Nested"))));
        Assert.Null(Match("$x", "f(2)", ("x", QuoteHole.Pattern("P.Nested"))));
    }

    [Fact]
    public void List_capture_items_must_each_match()
    {
        Assert.Equal("1|2", Match("f(..$xs)", "f(1, 2)", ("xs", QuoteHole.PatternList("P.Lit")))!["xs"]);
        Assert.Null(Match("f(..$xs)", "f(1, 3)", ("xs", QuoteHole.PatternList("P.Lit"))));
    }

    [Fact]
    public void Deep_recursion_hits_budget_not_stack()
    {
        var limited = Run("$x", Deep(1_000), new QuoteMatchOptions { MaxWorkSteps = 2_000 }, ("x", QuoteHole.Pattern("P.Nested")));
        Assert.Equal("NQP0006", Assert.Single(limited.Diagnostics).Code);
        Assert.True(Run("$x", Deep(1_000), null, ("x", QuoteHole.Pattern("P.Nested"))).Matched);
    }
}
