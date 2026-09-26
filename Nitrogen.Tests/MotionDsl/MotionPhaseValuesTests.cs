using Gravity.MotionDSL.Compiler;
using Gravity.MotionDSL.IR;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>MotionValues.Timeline against SkillPhaseCompiler's authored end (issue 240, Plan 2).</summary>
public class MotionPhaseValuesTests
{
    internal const string Transition = "transition 1s easing linear";

    /// <summary>A skill whose one phase source <c>main</c> holds <paramref name="phases"/>; it requires motors a, b and c.</summary>
    internal static string PhaseSkill(string header, params string[] phases) =>
        "skill probe {\n" +
        "    " + header + "\n" +
        "    requires motors {\n        biped.a\n        biped.b\n        biped.c\n    }\n" +
        "    source main: phases {\n" +
        string.Concat(phases.Select(p => "        " + p + "\n")) +
        "    }\n" +
        "    output blend { main priority 1 }\n" +
        "}\n";

    /// <summary>A phase whose <paramref name="items"/> come before its pose; the pose sets a and b unless <paramref name="pose"/> says otherwise.</summary>
    internal static string Phase(string name, string items, string pose = "biped.a = 0.1\n            biped.b = 0.2") =>
        "phase " + name + " { " + items + " pose {\n            " + pose + "\n        } }";

    internal static IEnumerable<int> Nodes(Nitrogen.SyntaxTree tree, int kind)
    {
        for (int node = 0; node < tree.NodeCount; node++)
            if (tree.Kind(node) == kind) yield return node;
    }

    /// <summary>The compiler's authored end: its last track ends there (a final hold adds a hold track).</summary>
    internal static float CompiledEnd(string text, string skill, string source)
    {
        var result = MotionCompiler.Compile(new MotionParser(new MotionLexer(text).Tokenize()).ParseFile());
        return result.Skills[skill].Sources.OfType<SkillTrackSourceIR>().Single(s => s.Name == source).Tracks.Max(t => t.EndSeconds);
    }

    public static TheoryData<string, float> Timelines => new()
    {
        { PhaseSkill("lifecycle continuous", Phase("a", "")), 0f },
        { PhaseSkill("lifecycle continuous", Phase("a", "hold 0.5s")), 0.5f },
        { PhaseSkill("lifecycle continuous", Phase("a", "hold 0.5s"), Phase("b", Transition + " hold 0.25s")), 1.75f },
        {
            PhaseSkill("lifecycle continuous", Phase("a", ""),
                Phase("b", Transition + " override biped.a { delay 0.25s transition 0.5s }"),
                Phase("c", "transition 0.25s easing smoothstep")),
            1.25f
        },
        { PhaseSkill("lifecycle continuous", Phase("a", "hold 0.125s"), Phase("b", "transition 0.25s easing linear hold 0.5s")), 0.875f },
    };

    [Theory]
    [MemberData(nameof(Timelines))]
    public void The_timeline_ends_where_the_compiler_ends_it(string text, float expected)
    {
        using var typed = new MotionTypingTests.Typed(text);
        float? timeline = MotionValues.Timeline(typed.File, Nodes(typed.File.Tree, MotionKinds.Phases).Single());
        Assert.Equal(expected, timeline);
        Assert.Equal(CompiledEnd(text, "probe", "main"), timeline);
    }

    [Theory]
    [InlineData("transition time easing linear")]
    [InlineData("transition -1s easing linear")]
    [InlineData("")]
    public void A_timeline_with_an_invalid_transition_is_unknown(string items)
    {
        using var typed = new MotionTypingTests.Typed(PhaseSkill("lifecycle continuous", Phase("a", ""), Phase("b", items)));
        Assert.Null(MotionValues.Timeline(typed.File, Nodes(typed.File.Tree, MotionKinds.Phases).Single()));
    }

    [Fact]
    public void Every_corpus_phase_source_ends_where_the_compiler_ends_it()
    {
        int compared = 0;
        foreach (string path in MotionCorpus.SkillFiles())
        {
            string text = File.ReadAllText(path);
            using var typed = new MotionTypingTests.Typed(text);
            var tree = typed.File.Tree;
            foreach (int phases in Nodes(tree, MotionKinds.Phases))
            {
                int source = typed.File.ParentOf(phases);
                int skill = source;
                while (tree.Kind(skill) != MotionKinds.Skill) skill = tree.Parent(skill);
                string skillName = new SkillNode(tree, skill).Name.ToString();
                string sourceName = new SourceNode(tree, source).Name.ToString();
                Assert.Equal(CompiledEnd(text, skillName, sourceName), MotionValues.Timeline(typed.File, phases));
                compared++;
            }
        }
        if (MotionCorpus.SkillFiles().Count > 0) Assert.True(compared >= 10, $"only {compared} phase sources compared");
    }
}
