using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>A nested source's argument and input names resolve in the skill it names (issue 239).</summary>
public class MotionQualifiedTests
{
    internal const string Child =
        "skill child {\n" +
        "    lifecycle continuous\n" +
        "    length 1\n" +
        "    arguments {\n" +
        "        gain: float = 1 range(0..2)\n" +
        "        mode: enum(walk, run) = walk\n" +
        "    }\n" +
        "    inputs {\n" +
        "        flag: bool = false\n" +
        "    }\n" +
        "    requires motors {\n" +
        "        biped.a\n" +
        "    }\n" +
        "    source main: tracks {\n" +
        "        track biped.a.target_angle {\n" +
        "            from rest to gain during 0s..1s easing linear\n" +
        "        }\n" +
        "    }\n" +
        "    output blend { main priority 1 }\n" +
        "}\n";

    internal static string Parent(string mappings, string skill = "child") =>
        "skill parent {\n" +
        "    lifecycle continuous\n" +
        "    length 1\n" +
        "    requires motors {\n" +
        "        biped.a\n" +
        "    }\n" +
        "    source n: nested " + skill + " {\n" +
        "        " + mappings + "\n" +
        "    }\n" +
        "    output blend { n priority 1 }\n" +
        "}\n";

    static (ParseResult Parsed, Project Project) Bind(string text)
    {
        var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        Assert.True(parsed.Success, parsed.Success ? "" : parsed.FormatMessage(parsed.Diagnostics[0]));
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        return (parsed, project);
    }

    [Fact]
    public void An_argument_name_resolves_to_the_called_skills_declaration()
    {
        string text = Child + Parent("argument gain = 2");
        var (parsed, project) = Bind(text);
        using var _ = parsed;
        var mapping = project["a.skill"].References.Single(r => r.Name == "gain" && parsed.Tree.Kind(r.Node) == MotionKinds.ArgumentMapping);
        var symbol = Assert.Single(project.Resolve(mapping));
        Assert.Equal(text.IndexOf("gain:", StringComparison.Ordinal), symbol.NameSpan.Start);
        Assert.Contains(mapping, project.ReferencesTo(symbol));
        Assert.Empty(project.Diagnostics("a.skill"));
    }

    [Fact]
    public void An_unknown_argument_is_unresolved_in_the_called_skill()
    {
        var (parsed, project) = Bind(Child + Parent("argument nope = 2"));
        using var _ = parsed;
        var diagnostic = Assert.Single(project.Diagnostics("a.skill"));
        Assert.Equal((BindingCodes.Unresolved, "unresolved value 'nope' in skill 'child'"), (diagnostic.Code, diagnostic.Message));
    }

    [Fact]
    public void Under_an_unknown_skill_the_names_are_unknown_not_errors()
    {
        var (parsed, project) = Bind(Child + Parent("argument gain = 2", skill: "nothing"));
        using var _ = parsed;
        var diagnostic = Assert.Single(project.Diagnostics("a.skill"));
        Assert.Contains("'nothing'", diagnostic.Message);
    }

    [Fact]
    public void Candidates_for_a_qualified_name_or_hole_are_the_called_skills_values()
    {
        var (parsed, project) = Bind(Child + Parent("argument ga = 2"));
        using var _ = parsed;
        var mapping = project["a.skill"].References.Single(r => r.Name == "ga");
        var names = project.CandidatesFor(mapping).Select(s => s.Name).ToList();
        Assert.Contains("gain", names);
        Assert.Contains("flag", names);
        Assert.DoesNotContain("time", names); // a skill's built-in, not a declaration of child

        using var holed = NitrogenMotionParser.Language.Parse(Child + Parent("argument = 2"), MotionModule.File); // recovers: the name is Missing
        var holeProject = new Project(NitrogenMotionParser.Language);
        holeProject.Set("a.skill", holed.Tree);
        var hole = Assert.Single(holeProject["a.skill"].Holes);
        Assert.Equal("skill", hole.Qualifier);
        Assert.Contains("gain", holeProject.CandidatesFor(hole).Select(s => s.Name));
    }
}
