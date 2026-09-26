using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Xunit;
using static Nitrogen.Tests.PolicyValueChecksTests;

namespace Nitrogen.Tests;

/// <summary>PV0001 and PV0004–PV0007 on hand-written policies (issue 241, Plan 2). Every case also runs through the hand pipeline.</summary>
public class PolicyStructureChecksTests
{
    [Fact]
    public void A_goal_reads_as_the_mapper_maps_it()
    {
        string text = Mutate(Stepper, "families { velocity 1 }", "families { velocity 0.5 stand 0.25 } families { stand 0.5 }");
        using var typed = new PolicyValueChecksTests.Typed("p.policy", text);
        var facts = PolicyValues.Goal(typed.Semantics, typed.Nodes(PolicyKinds.Goal).Single());
        Assert.Equal("g", facts.Name);
        Assert.True(facts.StepClock);
        Assert.Equal(new[] { "v_forward" }, facts.Channels);
        Assert.Equal(new (string, float?)[] { ("velocity", 0.5f), ("stand", 0.5f) }, facts.Families);
        Assert.True(facts.Command >= 0 && facts.Reward >= 0 && facts.Start >= 0);

        using var reference = new PolicyValueChecksTests.Typed("p.policy", Reference);
        var goal = PolicyValues.Goal(reference.Semantics, reference.Nodes(PolicyKinds.Goal).Single());
        Assert.False(goal.StepClock);
        Assert.Equal(-1, goal.Command);
        Assert.Contains("walk", goal.References);
    }

    /// <summary>Removes the block that starts at <paramref name="header"/>, through its matching brace.</summary>
    internal static string Without(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, header);
        Assert.Equal(start, source.LastIndexOf(header, StringComparison.Ordinal));
        int open = source.IndexOf('{', start), depth = 0, end = open;
        for (; end < source.Length; end++)
        {
            if (source[end] == '{') depth++;
            else if (source[end] == '}' && --depth == 0) break;
        }
        return source[..start] + source[(end + 1)..];
    }

    /// <summary><c>find -> replace</c> edits separated by <c> ;; </c>; <c>-header</c> removes a block.</summary>
    static string Edit(string source, string edits)
    {
        if (edits.Length == 0) return source;
        foreach (string edit in edits.Split(" ;; "))
        {
            if (edit.StartsWith('-')) { source = Without(source, edit[1..]); continue; }
            // " ;; " strips the space after a trailing " -> ", so split at " ->" and drop one space.
            int arrow = edit.IndexOf(" ->", StringComparison.Ordinal);
            string replace = edit[(arrow + 3)..];
            if (replace.StartsWith(' ')) replace = replace[1..];
            source = Mutate(source, edit[..arrow], replace.Replace("\\n", "\n"));
        }
        return source;
    }

    [Theory]
    // PV0001: structure
    [InlineData("R", "requires rig \"r.motion\" -> ", "PV0001")]
    [InlineData("R", "-observe {", "PV0001")]
    [InlineData("R", "-train {", "PV0001")]
    [InlineData("R", "-evaluate {", "PV0001")]
    [InlineData("R", "-goal g {", "PV0001")]
    [InlineData("R", "-reward {", "PV0001")]
    [InlineData("R", "-start {", "PV0001")]
    [InlineData("R", "-terminate {", "PV0001")]
    [InlineData("R", "success standing { height 1 tolerance 0.1 upright > 0.9 } at end -> ", "PV0001")]
    [InlineData("R", "clock walk time_driven hold 3 -> ", "PV0001")]
    [InlineData("R", "actuate { rate_limit 0.5 scale 0.5 smoothing 0.25 clamp knee 0.2 } -> actuate { }", "PV0001")]
    [InlineData("R", "perturb { push 50 every 1..3 } -> perturb { }", "PV0001")]
    [InlineData("R", "perturb { push 50 every 1..3 } -> perturb { push 50 every 1..3 push 20 every 1..2 }", "PV0001")]
    [InlineData("R", "train { -> goal h { }\\n    train {", "PV0001 PV0001 PV0001 PV0001 PV0001 PV0001")]
    [InlineData("R", "entropy 0.01 to 0 from 0.5 -> ", "PV0001")]
    [InlineData("R", "episodes 4  -> ", "PV0001")]
    [InlineData("R", "report success -> ", "PV0001")]
    [InlineData("R", "report success } -> scenarios report success }", "PV0001")]
    [InlineData("S", "channels v_forward -> ", "PV0001")]
    [InlineData("S", "families { velocity 1 } -> ", "PV0001")]
    [InlineData("S", "resample every 1..3 -> ", "PV0001")]
    [InlineData("S", "standing_height 1 -> standing_height 0", "PV0001")]
    // PV0005: clock mode
    [InlineData("R", "action_rate -0.01 -> goal_hit 0.5", "PV0005")]
    [InlineData("R", "action_rate -0.01 -> alive 0.1", "PV0005")]
    [InlineData("R", "action_rate -0.01 -> goal_track velocity 1 halving 0.5", "PV0005")]
    [InlineData("R", "perturb { push 50 every 1..3 } -> perturb { push 50 every 1..3 } command { channels v_forward families { velocity 1 } resample every 1..3 standing_height 1 }", "PV0005")]
    [InlineData("R", "reference 0.5 walk range 0.1..0.9 -> standing 0.5", "PV0005 PV0008")]
    [InlineData("R", "height_error > 0.3 -> tilt > 0.3", "PV0005")]
    [InlineData("R", "success standing { height 1 tolerance 0.1 upright > 0.9 } at end -> success tracking { error < 0.1 } at end", "PV0005")]
    [InlineData("R", "perturb { push 50 every 1..3 } -> perturb { push 50 every 1..3 } randomize { pose_jitter 0.1 }", "PV0005")]
    [InlineData("R", "gravity -> gravity command", "PV0005")]
    [InlineData("S", "observe { -> reference walk: skill \"w.skill\"\\n    observe { ;; alive 0.1 -> alive 0.1 track walk pose 1 sharpness 2", "PV0005")]
    [InlineData("S", "observe { -> reference walk: skill \"w.skill\"\\n    observe { ;; gravity command -> gravity command reference_delta walk", "PV0005")]
    [InlineData("S", "-command {", "PV0005 PV0006 PV0007")]
    // PV0006: reward structure
    [InlineData("S", "goal_track velocity 1 halving 0.5 -> ", "PV0006")]
    [InlineData("R", "track walk pose 1 sharpness 2 weights { knee 2 } -> ", "PV0006")]
    [InlineData("S", "goal_hit 0.5 -> goal_hit 0.5 goal_hit 0.25", "PV0006")]
    [InlineData("S", "alive 0.1 -> stride 0.1", "PV0006")]
    [InlineData("S", "alive 0.1 -> alive 0.1 sigma 1", "PV0006")]
    [InlineData("S", "alive 0.1 -> stride 0.1 sigma 1 gate_sigma_sq 0.5", "")]
    [InlineData("S", "alive 0.1 -> alive 0.1 when height_commanded", "PV0006")]
    [InlineData("S", "alive 0.1 -> stand_still 0.1", "")]
    [InlineData("S", "channels v_forward -> channels height ;; alive 0.1 -> stand_still 0.1", "PV0006")]
    [InlineData("S", "goal_track velocity 1 -> goal_track stand 1", "PV0006")]
    [InlineData("S", "standing 1 -> standing 0.5 crumple 0.5 { cut 0.5 }", "PV0006")]
    // PV0007: sums and totals, and the command block's PV0004
    [InlineData("R", "supine 0.25 -> supine 0.5", "PV0007")]
    [InlineData("S", "families { velocity 1 } -> families { velocity 0.5 }", "PV0007")]
    [InlineData("S", "families { velocity 1 } -> families { stand 1 }", "PV0006 PV0007")]
    [InlineData("S", "families { velocity 1 } -> families { velocity 0.5 stand 0.5 from 0.2 }", "")]
    [InlineData("S", "families { velocity 1 } -> families { velocity 0.5 from 0.1 stand 0.5 }", "PV0007")]
    [InlineData("S", "families { velocity 1 } -> families { velocity 1 stand 0 }", "PV0004")]
    [InlineData("S", "families { velocity 1 } -> families { velocity 0.5 stand 0.5 from 1 }", "PV0004")]
    [InlineData("S", "resample every 1..3 -> box { v_forward 0..1 }\\n            resample every 1..3", "")]
    [InlineData("S", "resample every 1..3 -> box { v_forward 1..0 }\\n            resample every 1..3", "PV0004")]
    [InlineData("S", "resample every 1..3 -> resample every 3..1", "PV0004")]
    [InlineData("R", "rewards clip 10 -> rewards clip 0.5", "PV0007")]
    [InlineData("R", "std_cap 1 to 0.1 from 0 -> std_cap 1 to 0.1 from 0\\n        command_box 0 to 1 from 0", "PV0007")]
    public void Structure(string base_, string edits, string codes) => Expect(codes, Edit(base_ == "R" ? Reference : Stepper, edits));
}
