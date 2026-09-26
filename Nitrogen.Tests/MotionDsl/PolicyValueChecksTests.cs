using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>PV codes on hand-written policies (issue 241). Every case also runs through the hand pipeline.</summary>
public class PolicyValueChecksTests
{
    /// <summary>A reference-clock policy that compiles: every local rule of that side has a line to break.</summary>
    internal const string Reference = """
        policy probe {
            requires rig "r.motion"
            requires scene "s.scene"
            reference walk: skill "w.skill"
            actuate { rate_limit 0.5 scale 0.5 smoothing 0.25 clamp knee 0.2 }
            observe {
                gravity
                noise { joint_angle 0.01 }
                latency 0..2
            }
            goal g {
                clock walk time_driven hold 3
                reward {
                    track walk pose 1 sharpness 2 weights { knee 2 }
                    joint_limit -0.1 margin 0.1
                    action_rate -0.01
                }
                start {
                    reference 0.5 walk range 0.1..0.9
                    crumple 0.25 { cut 0.5 shove 0..1 settle 10 joint_noise 0.1 }
                    supine 0.25 { settle 5 joint_noise 0.05 }
                }
                terminate { nonfinite height_error > 0.3 }
                success standing { height 1 tolerance 0.1 upright > 0.9 } at end
                perturb { push 50 every 1..3 }
            }
            train {
                algorithm ppo worlds 4 seed 1 timesteps 10k network [64 64]
                learning_rate 1e-3 hold 0.5 then decay 0.5
                entropy 0.01 to 0 from 0.5
                std_cap 1 to 0.1 from 0
                normalize observations clip 10 rewards clip 10
            }
            evaluate { episodes 4 start reference report success }
        }
        """;

    /// <summary>A step-clock policy that compiles: the command side's local rules.</summary>
    internal const string Stepper = """
        policy stepper {
            requires rig "r.motion"
            requires scene "s.scene"
            observe { gravity command }
            goal g {
                clock steps 100
                reward {
                    goal_track velocity 1 halving 0.5
                    goal_hit 0.5
                    alive 0.1
                }
                start { standing 1 }
                terminate { nonfinite height < 0.5 tilt > 1 forbidden_contact_steps >= 5 }
                success tracking { error < 0.1 } at end
                command {
                    channels v_forward
                    families { velocity 1 }
                    resample every 1..3
                    standing_height 1
                }
                randomize { friction 0.5..1.5 latency 0..2 pose_jitter 0.1 }
            }
            train {
                algorithm ppo worlds 4 seed 1 timesteps 10k network [64]
                learning_rate 1e-3 hold 0.5 then decay 0.5
                entropy 0.01 to 0 from 0.5
                std_cap 1 to 0.1 from 0
                command_box 0 to 1 from 0.2 over 1M
                normalize observations clip 10 rewards clip 10
            }
            evaluate { episodes 4 start standing report success }
        }
        """;

    /// <summary>A policy or compose document with its semantics.</summary>
    internal sealed class Typed : IDisposable
    {
        readonly ParseResult _parsed;

        public Typed(string path, string text)
        {
            _parsed = NitrogenPolicyParser.Language.Parse(text,
                path.EndsWith(".compose", StringComparison.Ordinal) ? PolicyModule.ComposeDocument : PolicyModule.PolicyDocument);
            Assert.True(_parsed.Success, _parsed.Success ? "" : _parsed.FormatMessage(_parsed.Diagnostics[0]));
            var project = new Project(NitrogenPolicyParser.Language);
            project.Set(path, _parsed.Tree);
            Semantics = new ProjectSemantics(project)[path];
        }

        public FileSemantics Semantics { get; }

        public IEnumerable<int> Nodes(int kind)
        {
            for (int node = 0; node < Semantics.Tree.NodeCount; node++)
                if (Semantics.Tree.Kind(node) == kind) yield return node;
        }

        public void Dispose() => _parsed.Dispose();
    }

    /// <summary>The exact codes, and the pipeline rejects the text exactly when there is one, with a message the editor also shows.</summary>
    internal static void Expect(string codes, string text, string path = "p.policy")
    {
        using var typed = new Typed(path, text);
        var diagnostics = typed.Semantics.Diagnostics();
        Assert.Equal(codes.Length == 0 ? Array.Empty<string>() : codes.Split(' '), diagnostics.Select(d => d.Code));
        var (failed, message) = PolicyValueOracleTests.Pipeline(path, text);
        Assert.True(failed == codes.Length > 0, failed ? $"the pipeline rejects it: {message}" : "the pipeline accepts it");
        if (failed) Assert.Contains(message, diagnostics.Select(d => d.Message));
    }

    internal static string Mutate(string source, string find, string replace)
    {
        if (find.Length == 0) return source;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, System.Text.RegularExpressions.Regex.Escape(find)));
        return source.Replace(find, replace);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("scale 0.5", "scale 1.5", "PV0004")]
    [InlineData("rate_limit 0.5", "rate_limit 2", "PV0004")]
    [InlineData("smoothing 0.25", "smoothing 1", "PV0004")]
    [InlineData("clamp knee 0.2", "clamp knee 0", "PV0004")]
    [InlineData("joint_angle 0.01", "joint_angle -0.01", "PV0004")]
    [InlineData("latency 0..2", "latency 2..1", "PV0004")]
    [InlineData("latency 0..2", "latency 0..1.5", "PV0002")]
    [InlineData("hold 3", "hold -1", "PV0004")]
    [InlineData("hold 3", "hold 2.5", "PV0002")]
    [InlineData("time_driven", "timed", "PV0003")]
    [InlineData("pose 1 sharpness 2", "pose 0 sharpness 2", "PV0004")]
    [InlineData("pose 1 sharpness 2", "pose -1 sharpness 2", "PV0004")]
    [InlineData("sharpness 2", "sharpness 0", "PV0004")]
    [InlineData("knee 2 }", "knee 0 }", "PV0004")]
    [InlineData("joint_limit -0.1", "joint_limit 0.1", "PV0004")]
    [InlineData("joint_limit -0.1", "joint_limit 0", "PV0004")]
    [InlineData("margin 0.1", "margin 0.5", "PV0004")]
    [InlineData("action_rate -0.01", "action_rate 0.01", "PV0004")]
    [InlineData("reference 0.5 walk", "reference 1.5 walk", "PV0007 PV0004")]
    [InlineData("range 0.1..0.9", "range 0.9..0.1", "PV0004")]
    [InlineData("cut 0.5", "cut 0", "PV0004")]
    [InlineData("shove 0..1", "shove 1..0", "PV0004")]
    [InlineData("settle 10", "settle -1", "PV0004")]
    [InlineData("settle 10", "settle 2.5", "PV0002")]
    [InlineData("joint_noise 0.05", "joint_noise -0.05", "PV0004")]
    [InlineData("height_error > 0.3", "height_error > 0", "PV0004")]
    [InlineData("tolerance 0.1", "tolerance 0", "PV0004")]
    [InlineData("upright > 0.9", "upright > 1.5", "PV0004")]
    [InlineData("success standing { height 1 tolerance 0.1 upright > 0.9 } at end", "success supine { facing < 0.5 } at end", "PV0004")]
    [InlineData("success standing { height 1 tolerance 0.1 upright > 0.9 } at end", "success supine { facing < -0.5 } at end", "")]
    [InlineData("push 50", "push 0", "PV0004")]
    [InlineData("every 1..3", "every 3..1", "PV0004")]
    [InlineData("worlds 4", "worlds 0", "PV0004")]
    [InlineData("worlds 4", "worlds 4.5", "PV0002")]
    [InlineData("timesteps 10k", "timesteps 10G", "PV0002")]
    [InlineData("timesteps 10k", "timesteps 0", "PV0004")]
    [InlineData("network [64 64]", "network [64 0]", "PV0004")]
    [InlineData("network [64 64]", "network [64 1.5]", "PV0002")]
    [InlineData("algorithm ppo", "algorithm sac", "PV0003")]
    [InlineData("hold 0.5 then", "hold 1.5 then", "PV0004")]
    [InlineData("from 0.5", "from 1.5", "PV0004")]
    [InlineData("observations clip 10", "observations clip 0", "PV0004")]
    [InlineData("start reference report", "start sideways report", "PV0003")]
    [InlineData("episodes 4", "episodes 2.5", "PV0002")]
    public void Reference_policy(string find, string replace, string codes) => Expect(codes, Mutate(Reference, find, replace));

    [Theory]
    [InlineData("", "", "")]
    [InlineData("clock steps 100", "clock steps 0", "PV0004")]
    [InlineData("clock steps 100", "clock steps 1.5", "PV0002")]
    [InlineData("goal_track velocity 1", "goal_track velocity -1", "PV0004")]
    [InlineData("halving 0.5", "halving 0", "PV0004")]
    [InlineData("goal_hit 0.5", "goal_hit -0.5", "PV0004")]
    [InlineData("alive 0.1", "alive 0", "PV0004")]
    [InlineData("height < 0.5", "height < 1", "PV0004")]
    [InlineData("height < 0.5", "height < 0", "PV0004")]
    [InlineData("tilt > 1", "tilt > 0", "PV0004")]
    [InlineData("forbidden_contact_steps >= 5", "forbidden_contact_steps >= 0", "PV0004")]
    [InlineData("forbidden_contact_steps >= 5", "forbidden_contact_steps >= 2.5", "PV0002")]
    [InlineData("error < 0.1", "error < 0", "PV0004")]
    [InlineData("friction 0.5..1.5", "friction 1.5..0.5", "PV0004")]
    [InlineData("latency 0..2 pose", "latency 2..1 pose", "PV0004")]
    [InlineData("latency 0..2 pose", "latency 0..1.5 pose", "PV0002")]
    [InlineData("pose_jitter 0.1", "pose_jitter -0.1", "PV0004")]
    [InlineData("command_box 0 to 1", "command_box 0.5 to 0.2", "PV0004")]
    [InlineData("from 0.2 over", "from 1.2 over", "PV0004")]
    [InlineData("over 1M", "over 1G", "PV0002")]
    public void Step_policy(string find, string replace, string codes) => Expect(codes, Mutate(Stepper, find, replace));

    [Fact]
    public void Checks_sit_on_the_offending_number()
    {
        string text = Mutate(Reference, "scale 0.5", "scale 1.5");
        using var typed = new Typed("p.policy", text);
        var diagnostic = Assert.Single(typed.Semantics.Diagnostics());
        Assert.Equal((text.IndexOf("1.5", StringComparison.Ordinal), 3), (diagnostic.Span.Start, diagnostic.Span.Length));
    }
}
