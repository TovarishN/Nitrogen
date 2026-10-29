using Nitrogen.Tests.QuoteLists;
using Xunit;
using static Nitrogen.Tests.NamedPatternTestModule;

namespace Nitrogen.Tests;

public sealed class QuotePatternFindAllTests
{
    static readonly Language Language = NamedPatternMatchTests.Shared;

    static T WithInput<T>(string source, Func<QuoteNode, T> read)
    {
        using var input = Language.ParseTemplate(source, Expr, new Dictionary<string, string>());
        Assert.True(input.Success, source);
        return read(new(input.Tree, input.Tree.Root));
    }

    static int[] Starts(QuoteFindResult result) =>
        result.Matches.Select(match => match.Node.Tree.Span(match.Node.Node).Start).ToArray();

    [Fact]
    public void Named_pattern_matches_directly()
    {
        var pattern = Language.NamedPattern("P.Nested");
        Assert.NotNull(pattern);
        var result = WithInput("f(f(1))", input => pattern!.Match(input));
        Assert.True(result.Matched);
        Assert.Empty(result.Captures);
        Assert.False(WithInput("f(2)", input => pattern!.Match(input)).Matched);
    }

    [Fact]
    public void Unknown_named_pattern_is_null()
    {
        Assert.Null(Language.NamedPattern("P.Missing"));
    }

    [Fact]
    public void FindAll_returns_preorder_document_order()
    {
        var (complete, starts) = WithInput("f(1, f(1), 1)", input => Language.NamedPattern("P.One")!.FindAll(input) is var r ? (r.Complete, Starts(r)) : default);
        Assert.True(complete);
        Assert.Equal([2, 7, 11], starts);
    }

    [Fact]
    public void FindAll_returns_nested_matches()
    {
        Assert.Equal([0, 2, 4], WithInput("f(f(1))", input => Starts(Language.NamedPattern("P.Nested")!.FindAll(input))));
    }

    [Fact]
    public void FindAll_reports_parsed_pattern_captures()
    {
        using var compiled = Language.CompilePattern(Expr, "f($x)", new Dictionary<string, QuoteHole> { ["x"] = QuoteHole.Node(Expr) });
        var texts = WithInput("f(f(2))", input => compiled.Pattern!.FindAll(input).Matches
            .Select(match => match.Captures["x"] is QuoteArgument.Scalar scalar ? scalar.Value.Tree.GetText(scalar.Value.Node).ToString() : "").ToArray());
        Assert.Equal(["f(2)", "2"], texts);
    }

    [Fact]
    public void FindAll_budget_returns_partial_and_NQP0006()
    {
        string source = "f(" + string.Join(", ", Enumerable.Repeat("1", 300)) + ")";
        var result = WithInput(source, input => Language.NamedPattern("P.One")!.FindAll(input, new QuoteMatchOptions { MaxWorkSteps = 3_500 }));
        Assert.False(result.Complete);
        Assert.True(result.Matches.Count > 0, "steps too few");
        Assert.True(result.Matches.Count < 300);
        Assert.Equal("NQP0006", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void FindAll_cancellation_throws()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => WithInput("f(1)", input =>
            Language.NamedPattern("P.One")!.FindAll(input, new QuoteMatchOptions { CancellationToken = cancel.Token })));
    }

    [Fact]
    public void FindAll_skips_invalid_subtrees()
    {
        // QuoteLists has no error recovery; a template hole is syntax Validate rejects in the same way.
        using var parsed = Language.ParseTemplate("f(1, f($X), 1)", Expr, new Dictionary<string, string> { ["X"] = Expr });
        Assert.True(parsed.Success);
        var result = Language.NamedPattern("P.One")!.FindAll(new(parsed.Tree, parsed.Tree.Root));
        Assert.True(result.Complete);
        Assert.Empty(result.Diagnostics);
        Assert.Equal([2, 12], Starts(result));
        using var calls = Language.CompilePattern(Expr, "f(..$_)", new Dictionary<string, QuoteHole>());
        Assert.Empty(calls.Pattern!.FindAll(new(parsed.Tree, parsed.Tree.Root)).Matches);
    }
}
