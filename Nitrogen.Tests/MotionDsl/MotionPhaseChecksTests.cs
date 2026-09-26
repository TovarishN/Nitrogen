using Xunit;
using static Nitrogen.Tests.MotionPhaseValuesTests;

namespace Nitrogen.Tests;

/// <summary>MV0005–MV0009 on hand-written skills (issue 240, Plan 2). Every case also runs through the compiler.</summary>
public class MotionPhaseChecksTests
{
    const string Header = "lifecycle continuous\n    length 10";
    static readonly string First = Phase("a", "");
    static readonly string Second = Phase("b", Transition);

    /// <summary>The exact codes typing reports, and the compiler rejects the text exactly when one is an MV code.</summary>
    internal static void Expect(string codes, string text)
    {
        using var typed = new MotionTypingTests.Typed(text);
        Assert.Equal(codes.Length == 0 ? Array.Empty<string>() : codes.Split(' '), typed.Codes);
        var (_, failed, message) = MotionValueOracleTests.Compile(text);
        Assert.True(failed == codes.Contains("MV", StringComparison.Ordinal), failed ? $"the compiler rejects it: {message}" : "the compiler accepts it");
    }

    public static TheoryData<string, string> Structure => new()
    {
        { "", PhaseSkill(Header, First, Second) },
        { "MV0006", PhaseSkill(Header) },
        { "MV0006", PhaseSkill(Header, Phase("a", Transition), Second) },
        { "MV0006", PhaseSkill(Header, Phase("a", "override biped.a { delay 0s }"), Second) },
        { "MV0006", PhaseSkill(Header, First, Phase("b", "")) },
    };

    public static TheoryData<string, string> Poses => new()
    {
        { "MV0007", PhaseSkill(Header, Phase("a", "", ""), Phase("b", Transition, "")) },
        { "MV0007", PhaseSkill(Header, Phase("a", "", "biped.a = 0.1\n            biped.a = 0.2"), Second) },
        { "MV0007", PhaseSkill(Header, First, Phase("b", Transition, "biped.a = 0.3")) },
        { "MV0007", PhaseSkill(Header, First, Phase("b", Transition, "biped.a = 0.3\n            biped.c = 0.4")) },
        { "MV0007", PhaseSkill(Header, First, Phase("b", Transition, "biped.a = 0.3\n            biped.a = 0.4\n            biped.b = 0.1")) },
    };

    public static TheoryData<string, string> Targets => new()
    {
        { "MV0008", PhaseSkill(Header, First, Phase("b", Transition, "biped.a = rest\n            biped.b = 0.2")) },
        { "MV0008", PhaseSkill(Header, Phase("a", "", "biped.a = 0.1 + 0\n            biped.b = 0.2"), Second) },
        { "MV0008", PhaseSkill(Header, Phase("a", "", "biped.a = abs(0.1)\n            biped.b = 0.2"), Second) },
        { "", PhaseSkill(Header, Phase("a", "", "biped.a = -(0.1)\n            biped.b = (0.2)"), Second) },
        { "", PhaseSkill(Header, Phase("a", "", "biped.a = rest\n            biped.b = 10deg"), Second) },
    };

    public static TheoryData<string, string> Overrides => new()
    {
        { "", PhaseSkill(Header, First, Phase("b", Transition + " override biped.a { transition 0.5s easing cubic_out }")) },
        { "", PhaseSkill(Header, First, Phase("b", Transition + " override biped.a { delay 0.5s transition 0.5s }")) },
        { "MV0009", PhaseSkill(Header, First, Phase("b", Transition + " override biped.c { delay 0s }")) },
        { "MV0009", PhaseSkill(Header, First, Phase("b", Transition + " override biped.a { delay 0s } override biped.a { easing linear }")) },
        { "MV0009", PhaseSkill(Header, First, Phase("b", Transition + " override biped.a { transition 2s }")) },
        { "MV0009", PhaseSkill(Header, First, Phase("b", Transition + " override biped.a { delay 0.75s transition 0.5s }")) },
        { "MV0009", PhaseSkill(Header, First, Phase("b", Transition + " override biped.a { delay 0.125s }")) },
    };

    [Theory]
    [MemberData(nameof(Structure))]
    public void Phase_structure(string codes, string text) => Expect(codes, text);

    [Theory]
    [MemberData(nameof(Poses))]
    public void Pose_motors(string codes, string text) => Expect(codes, text);

    [Theory]
    [MemberData(nameof(Targets))]
    public void Phase_targets(string codes, string text) => Expect(codes, text);

    [Theory]
    [MemberData(nameof(Overrides))]
    public void Override_windows(string codes, string text) => Expect(codes, text);

    public static TheoryData<string, string> Lengths => new()
    {
        { "", "lifecycle continuous\n    length 2" },
        { "", "lifecycle continuous\n    length 1.75" },
        { "MV0005", "lifecycle continuous\n    length 1.5" },
        { "MV0005", "lifecycle continuous\n    length 0" },
        { "MV0002", "lifecycle continuous\n    length -1" },
        { "MV0005", "lifecycle finite duration 2s\n    length 3" },
        { "", "lifecycle finite duration 3s\n    length 3" },
        { "MV0005", "lifecycle looping period 2s\n    length 3" },
        { "", "lifecycle looping period 2s\n    length 2" },
        { "MV0005", "lifecycle finite duration 1s" },
        { "", "lifecycle finite duration 2s" },
        { "MV0005", "lifecycle looping period 1s" },
        { "", "lifecycle continuous" },
    };

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Skill_length_covers_the_phases(string codes, string header) =>
        Expect(codes, PhaseSkill(header, Phase("a", "hold 0.5s"), Phase("b", Transition + " hold 0.25s")));
}
