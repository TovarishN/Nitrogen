using Nitrogen.MotionDsl;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>PolicyValues' number helpers against PolicyParser's rules (issue 241).</summary>
public class PolicyValuesTests
{
    [Theory]
    [InlineData("1.5", false, 1.5f)]
    [InlineData("2", true, -2f)]
    [InlineData("1e-3", false, 0.001f)]
    public void Numbers_parse_as_the_mapper_parses_them(string text, bool negative, float expected) =>
        Assert.Equal(expected, PolicyValues.Parse(text, negative));

    [Theory]
    [InlineData(2f, true)]
    [InlineData(2.5f, false)]
    [InlineData(-3f, true)]
    [InlineData(null, true)]
    public void Integers_are_whole_numbers(float? value, bool expected) => Assert.Equal(expected, PolicyValues.Integer(value));

    [Fact]
    public void A_fraction_is_reported_as_the_parser_reports_it() =>
        Assert.Equal($"Expected an integer, got {2.5f}", PolicyValues.NotInteger(2.5f));

    [Theory]
    [InlineData("10", 10L, null)]
    [InlineData("10k", 10_000L, null)]
    [InlineData("60M", 60_000_000L, null)]
    [InlineData("10G", null, "Unknown number suffix 'G' (use k or M)")]
    [InlineData("1.5k", null, "Expected an integer count, got '1.5'")]
    public void Counts_take_k_and_M(string text, long? value, string? problem)
    {
        Assert.Equal(value, PolicyValues.Count(text));
        Assert.Equal(problem, PolicyValues.CountProblem(text));
    }

    [Fact]
    public void Amounts_format_as_the_compiler_formats_them() => Assert.Equal("1.50", PolicyValues.F(1.5f));

    [Fact]
    public void A_num_carries_its_amount()
    {
        using var file = new PolicyValueChecksTests.Typed("p.policy", PolicyValueChecksTests.Reference);
        var nums = file.Nodes(Nitrogen.MotionDsl.PolicySyntax.PolicyKinds.Num).Select(n => file.Semantics.Get(n, Nitrogen.MotionDsl.PolicySyntax.PolicyModule.P_Num_Amount)).ToList();
        Assert.Contains(0.5f, nums);
        Assert.DoesNotContain(null, nums);
    }
}
