using Xunit;

namespace Nitrogen.Tests;

/// <summary>MV0010 on hand-written skills (issue 240, Plan 3). Every case also runs through the compiler.</summary>
public class MotionGraphChecksTests
{
    internal const string Angle =
        "    source angle: tracks {\n        track biped.a.target_angle {\n            from rest to 0.1 during 0s..1s easing linear\n        }\n    }\n";
    internal const string Offset =
        "    source offset: tracks {\n        track biped.a.angle_offset {\n            from 0 to 0.1 during 0s..1s easing linear\n        }\n    }\n";
    internal const string Pose =
        "    source held: phases {\n        phase a { pose {\n            biped.a = 0.1\n        } }\n    }\n";

    /// <summary>A skill named <paramref name="name"/> with <paramref name="sources"/>; <paramref name="output"/> lists the output's children, one per line.</summary>
    internal static string GraphSkill(string sources, string output, string name = "probe") =>
        "skill " + name + " {\n    lifecycle continuous\n    length 1\n    requires motors {\n        biped.a\n    }\n" +
        sources +
        "    output blend {\n        " + output.Replace("\n", "\n        ") + "\n    }\n}\n";

    static string Blend(string name, string children) =>
        "    source " + name + ": blend {\n        " + children.Replace("\n", "\n        ") + "\n    }\n";

    static string Nest(string name, string skill) => "    source " + name + ": nested " + skill + " {\n    }\n";

    /// <summary>The exact codes typing reports, and the compiler rejects the text exactly when there is one.</summary>
    static void Expect(string codes, string text)
    {
        using var typed = new MotionTypingTests.Typed(text);
        Assert.Equal(codes.Length == 0 ? Array.Empty<string>() : codes.Split(' '), typed.Codes);
        var (_, failed, message) = MotionValueOracleTests.Compile(text);
        Assert.True(failed == codes.Length > 0, failed ? $"the compiler rejects it: {message}" : "the compiler accepts it");
    }

    public static TheoryData<string, string> Modes => new()
    {
        { "", GraphSkill(Angle, "angle priority 1") },
        { "", GraphSkill(Angle, "angle priority -2") },
        { "MV0010", GraphSkill(Angle, "angle priority 0.5") },
        { "MV0010", GraphSkill(Angle, "angle priority time") },
        { "MT0001", GraphSkill(Angle, "angle priority true") },
        { "", GraphSkill(Angle, "angle weighted weight 0.5") },
        { "MV0010", GraphSkill(Angle, "angle weighted weight time") },
        { "", GraphSkill(Angle, "angle parameter_weighted weight time") },
        { "", GraphSkill(Offset, "offset additive weight 1") },
        { "MV0010", GraphSkill(Offset, "offset additive weight time") },
    };

    public static TheoryData<string, string> Additive => new()
    {
        { "MV0010", GraphSkill(Angle, "angle additive weight 1") },
        { "MV0010", GraphSkill(Pose, "held additive weight 1") },
        { "MV0010", GraphSkill(Angle + Offset + Blend("mix", "offset priority 1\nangle priority 2"), "mix additive weight 1") },
        { "", GraphSkill(Angle + Offset + Blend("mix", "offset priority 1"), "mix additive weight 1\nangle priority 1") },
    };

    public static TheoryData<string, string> Cycles => new()
    {
        { "MV0010", GraphSkill(Angle + Blend("loop", "angle priority 1\nloop priority 2"), "loop priority 1") },
        { "MV0010", GraphSkill(Angle + Blend("ping", "pong priority 1") + Blend("pong", "ping priority 1"), "ping priority 1") },
        {
            "",
            GraphSkill(Angle + Blend("left", "angle priority 1") + Blend("right", "angle priority 1") + Blend("top", "left priority 1\nright priority 2"),
                "top priority 1")
        },
        { "MV0010", GraphSkill(Angle + Nest("me", "probe"), "angle priority 1") },
        { "MV0010", GraphSkill(Angle + Nest("inner", "b"), "angle priority 1", "a") + GraphSkill(Angle + Nest("back", "a"), "angle priority 1", "b") },
        { "", GraphSkill(Angle + Nest("inner", "b"), "angle priority 1", "a") + GraphSkill(Angle, "angle priority 1", "b") },
        {
            "",
            GraphSkill(Angle + Nest("left", "b") + Nest("right", "c"), "angle priority 1", "a") + GraphSkill(Angle + Nest("down", "c"), "angle priority 1", "b")
                + GraphSkill(Angle, "angle priority 1", "c")
        },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Priorities_and_weights(string codes, string text) => Expect(codes, text);

    [Theory]
    [MemberData(nameof(Additive))]
    public void Additive_sources_write_only_relative_channels(string codes, string text) => Expect(codes, text);

    [Theory]
    [MemberData(nameof(Cycles))]
    public void Source_and_nested_skill_cycles(string codes, string text) => Expect(codes, text);

    [Fact]
    public void A_cycle_is_reported_once_with_the_compilers_path()
    {
        using var typed = new MotionTypingTests.Typed(
            GraphSkill(Angle + Blend("ping", "pong priority 1") + Blend("pong", "ping priority 1"), "ping priority 1"));
        var cycle = Assert.Single(typed.File.Diagnostics(), d => d.Code == "MV0010");
        Assert.Equal("source cycle: ping -> pong -> ping", cycle.Message);
    }
}
