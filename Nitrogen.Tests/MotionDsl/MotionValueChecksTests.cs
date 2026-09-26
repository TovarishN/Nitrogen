using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Motion.ngr's value checks on hand-written skills (issue 240).</summary>
public class MotionValueChecksTests
{
    static float? ConstantAt(MotionTypingTests.Typed typed, int kind, string needle)
    {
        int start = typed.Text.IndexOf(needle, StringComparison.Ordinal);
        for (int node = 0; node < typed.File.Tree.NodeCount; node++)
            if (typed.File.Tree.Kind(node) == kind && typed.File.Tree.Span(node).Start == start)
                return typed.File.Get(node, MotionModule.P_Expr_Constant);
        throw new InvalidOperationException($"no node of kind {kind} at '{needle}'");
    }

    [Fact]
    public void Constants_fold_and_references_do_not()
    {
        using var typed = new MotionTypingTests.Typed(MotionTypingTests.Skill(MotionTypingTests.Track("(1 + 2) * 0.5 - gain")));
        Assert.Equal(1.5f, ConstantAt(typed, MotionKinds.Mul, "(1 + 2)"));
        Assert.Null(ConstantAt(typed, MotionKinds.Sub, "(1 + 2)"));
        using var degrees = new MotionTypingTests.Typed(MotionTypingTests.Skill(MotionTypingTests.Track("90deg")));
        Assert.Equal(90f * (MathF.PI / 180f), ConstantAt(degrees, MotionKinds.Num, "90deg"));
    }

    static string Phases(string second) =>
        "source main: phases {\n" +
        "        phase a { pose { biped.a = 0.1 } }\n" +
        "        phase b { " + second + " pose { biped.a = 0.2 } }\n" +
        "    }";

    static string[] Codes(string text)
    {
        using var typed = new MotionTypingTests.Typed(text);
        return typed.Codes;
    }

    static string[] Split(string codes) => codes.Length == 0 ? [] : codes.Split(' ');

    [Theory]
    [InlineData("during 0s..1s", "")]
    [InlineData("during time..1s", "MV0001")]
    [InlineData("during 1s..0.5s", "MV0003")]
    [InlineData("during -1s..1s", "MV0003")]
    [InlineData("during true..1s", "MT0001")]
    public void Track_times(string during, string codes) =>
        Assert.Equal(Split(codes), Codes(MotionTypingTests.Skill(MotionTypingTests.Track("gain", during["during ".Length..]))));

    [Theory]
    [InlineData("gain / 0", "MV0001")]
    [InlineData("gain / 2", "")]
    public void Division_by_a_constant_zero(string to, string codes) =>
        Assert.Equal(Split(codes), Codes(MotionTypingTests.Skill(MotionTypingTests.Track(to))));

    [Theory]
    [InlineData("length 1", "")]
    [InlineData("length time", "MV0001")]
    [InlineData("length -1", "MV0002")]
    public void Skill_length(string length, string codes) =>
        Assert.Equal(Split(codes), Codes(MotionTypingTests.Skill(MotionTypingTests.Track("gain")).Replace("length 1", length)));

    [Theory]
    [InlineData("complete when flag for 1s", "")]
    [InlineData("complete when flag for time", "MV0001")]
    [InlineData("complete when flag for -1s", "MV0002")]
    [InlineData("timeout -1s then fail", "MV0002")]
    [InlineData("timeout gain then fail", "MV0001")]
    public void Completion_and_timeout(string item, string codes) =>
        Assert.Equal(Split(codes), Codes(MotionTypingTests.Skill(MotionTypingTests.Track("gain") + "\n    " + item)));

    [Theory]
    [InlineData("transition 1 easing linear", "")]
    [InlineData("transition time easing linear", "MV0001")]
    [InlineData("transition -1 easing linear", "MV0002")]
    [InlineData("transition 1 easing linear hold -1", "MV0002")]
    [InlineData("transition 1 easing linear override biped.a { delay -1 }", "MV0002")]
    [InlineData("transition 1 easing linear override biped.a { transition time }", "MV0001")]
    public void Phase_times(string second, string codes) =>
        Assert.Equal(Split(codes), Codes(MotionTypingTests.Skill(Phases(second))));

    [Theory]
    [InlineData("        gain: float = 1 range(0..2)\n", "")]
    [InlineData("        gain: float = 1 range(2..0)\n", "MV0004")]
    [InlineData("        gain: float = 5 range(0..2)\n", "MV0004")]
    [InlineData("        gain: float = 1 range(time..2)\n", "MV0001")]
    [InlineData("        gain: float = time range(0..2)\n", "")]
    public void Ranges_and_defaults(string declaration, string codes) =>
        Assert.Equal(Split(codes), Codes(MotionTypingTests.Skill(MotionTypingTests.Track("gain")).Replace("        gain: float = 1\n", declaration)));
}
