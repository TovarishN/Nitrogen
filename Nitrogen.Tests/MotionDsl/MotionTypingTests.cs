using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Motion.ngr's semantics on hand-written skills (issue 239).</summary>
public class MotionTypingTests
{
    /// <summary>A skill with a float and an enum argument, a bool input, and <paramref name="items"/>.</summary>
    internal static string Skill(string items) =>
        "skill probe {\n" +
        "    lifecycle continuous\n" +
        "    length 1\n" +
        "    arguments {\n" +
        "        gain: float = 1\n" +
        "        mode: enum(walk, run) = walk\n" +
        "        other: enum(a, b) = a\n" +
        "    }\n" +
        "    inputs {\n" +
        "        flag: bool = false\n" +
        "    }\n" +
        "    requires motors {\n" +
        "        biped.a\n" +
        "    }\n" +
        "    " + items + "\n" +
        "    output blend { main priority 1 }\n" +
        "}\n";

    internal static string Track(string to, string during = "0s..1s") =>
        "source main: tracks {\n        track biped.a.target_angle {\n            from rest to " + to + " during " + during + " easing linear\n        }\n    }";

    internal sealed class Typed : IDisposable
    {
        readonly ParseResult _parsed;

        public Typed(string text)
        {
            Text = text;
            _parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
            Assert.True(_parsed.Success, _parsed.Success ? "" : _parsed.FormatMessage(_parsed.Diagnostics[0]));
            Project = new Project(NitrogenMotionParser.Language);
            Project.Set("a.skill", _parsed.Tree);
            File = new ProjectSemantics(Project)["a.skill"];
        }

        public string Text { get; }

        public Project Project { get; }

        public FileSemantics File { get; }

        public string[] Codes => File.Diagnostics().Select(d => d.Code).ToArray();

        public SkillType TypeAt(int kind, string needle)
        {
            int start = Text.IndexOf(needle, StringComparison.Ordinal);
            for (int node = 0; node < File.Tree.NodeCount; node++)
                if (File.Tree.Kind(node) == kind && File.Tree.Span(node).Start == start) return File.Get(node, MotionModule.P_Expr_Type);
            throw new InvalidOperationException($"no node of kind {kind} at '{needle}'");
        }

        public void Dispose() => _parsed.Dispose();
    }

    [Fact]
    public void A_well_typed_skill_is_silent_and_types_flow_through_expressions()
    {
        using var typed = new Typed(Skill(Track("clamp(gain, 0, 1) + 0.5deg")));
        Assert.Empty(typed.Codes);
        Assert.Empty(typed.Project.Diagnostics("a.skill"));
        Assert.Equal(SkillType.Angle, typed.TypeAt(MotionKinds.Add, "clamp"));
        Assert.Equal("enum(walk, run)", typed.TypeAt(MotionKinds.Ref, "walk\n").ToString());
    }

    [Theory]
    [InlineData("to true", "MT0001")]
    [InlineData("to abs(gain, 1)", "MT0003")]
    [InlineData("to not gain", "MT0001")]
    [InlineData("to gain + flag", "MT0001")]
    [InlineData("to flag ? 1 : 2", "")]
    [InlineData("to gain ? 1 : 2", "MT0001")]
    [InlineData("to flag ? 1 : false", "MT0001")]
    [InlineData("to 1s", "MT0002")]
    [InlineData("to 1deg + 1s", "MT0002")]
    [InlineData("during 0s..1deg", "MT0002")]
    [InlineData("to sin(1s)", "MT0002")]
    [InlineData("to sin(time * 3)", "")]
    public void Track_values_are_checked_once(string change, string codes)
    {
        string text = change.StartsWith("during", StringComparison.Ordinal)
            ? Skill(Track("gain", change["during ".Length..]))
            : Skill(Track(change["to ".Length..]));
        using var typed = new Typed(text);
        Assert.Equal(codes.Length == 0 ? Array.Empty<string>() : codes.Split(' '), typed.Codes);
    }

    [Theory]
    [InlineData("complete when flag", "")]
    [InlineData("complete when gain", "MT0001")]
    [InlineData("complete when mode == run", "")]
    [InlineData("complete when mode == a", "MT0004")]
    [InlineData("complete when gain == flag", "MT0001")]
    [InlineData("complete when flag for 1s", "")]
    [InlineData("complete when flag for 1deg", "MT0002")]
    [InlineData("timeout true then fail", "MT0001")]
    public void Skill_slots_are_checked(string item, string codes)
    {
        using var typed = new Typed(Skill(Track("gain") + "\n    " + item));
        Assert.Equal(codes.Length == 0 ? Array.Empty<string>() : codes.Split(' '), typed.Codes);
    }

    [Theory]
    [InlineData("argument gain = 2", "")]
    [InlineData("argument gain = true", "MT0001")]
    [InlineData("argument flag = true", "MT0005")]
    [InlineData("input gain = 1", "MT0005")]
    [InlineData("input flag = true", "")]
    public void Nested_mappings_take_the_called_skills_types_and_sections(string mapping, string codes)
    {
        using var typed = new Typed(MotionQualifiedTests.Child + MotionQualifiedTests.Parent(mapping));
        Assert.Equal(codes.Length == 0 ? Array.Empty<string>() : codes.Split(' '), typed.Codes);
    }

    [Theory]
    [InlineData("        gain: float = true\n", "MT0001")]
    [InlineData("        mode2: enum(walk2, run2) = a\n", "MT0004")]
    [InlineData("        flag2: bool = true range(0..1)\n", "MT0001")]
    public void Declarations_are_checked(string declaration, string codes)
    {
        string text = Skill(Track("gain")).Replace("        gain: float = 1\n", declaration.StartsWith("        gain", StringComparison.Ordinal) ? declaration : "        gain: float = 1\n" + declaration);
        using var typed = new Typed(text);
        Assert.Equal(codes.Split(' '), typed.Codes);
    }
}
