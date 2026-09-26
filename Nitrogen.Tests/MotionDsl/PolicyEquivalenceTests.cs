using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>NitrogenPolicyParser must produce PolicyParser's AST exactly, and fail where it fails.</summary>
public class PolicyEquivalenceTests
{
    static PolicyParser Hand(string text) => new(new MotionLexer(text).Tokenize());

    static void AssertSamePolicy(string text) =>
        Assert.Equal(AstDump.Dump(Hand(text).ParsePolicy()), AstDump.Dump(NitrogenPolicyParser.ParsePolicy(text)));

    static void AssertSameCompose(string text) =>
        Assert.Equal(AstDump.Dump(Hand(text).ParseCompose()), AstDump.Dump(NitrogenPolicyParser.ParseCompose(text)));

    // The corpus is live (trainers load it), so it is held strict: PolicyParser must accept every file.
    [Theory]
    [MemberData(nameof(PolicyGrammarTests.PolicyCorpusFiles), MemberType = typeof(PolicyGrammarTests))]
    public void Every_policy_file_maps_to_the_hand_written_ast(string path)
    {
        if (path.Length == 0) return;
        AssertSamePolicy(File.ReadAllText(path));
    }

    [Theory]
    [MemberData(nameof(PolicyGrammarTests.ComposeCorpusFiles), MemberType = typeof(PolicyGrammarTests))]
    public void Every_compose_file_maps_to_the_hand_written_ast(string path)
    {
        if (path.Length == 0) return;
        AssertSameCompose(File.ReadAllText(path));
    }

    internal const string Minimal = """
        policy p {
            requires rig "r.motion"
            requires scene "s.scene"
            observe { gravity }
            goal g {
                clock steps 100
                reward { alive 1 }
                start { standing 1 }
                terminate { nonfinite }
                success tracking { error < 0.1 } at end
            }
            train {
                algorithm ppo worlds 1 seed 1 timesteps 10k network [8]
                learning_rate 1e-3 hold 0.5 then decay 0.5
                entropy 0.01 to 0 from 0.5
                std_cap 1 to 0.1 from 0
                normalize observations clip 10 rewards clip 10
            }
            evaluate { episodes 1 start standing report success }
        }
        """;

    // Every construct the corpus never uses, repeated blocks, and the accumulate / last-wins rules.
    const string Synthetic = """
        policy synth {
            requires rig "a.motion"
            requires scene "a.scene"
            requires scene "b.scene"
            reference r1: skill "r1.skill"
            reference r2: skill "r2.skill" on scene "other.scene"
            actuate { rate_limit 0.05 scale 0.5 smoothing 0.25 clamp abductor 0.1 clamp knee -0.2 }
            observe {
                pelvis height velocity angular
                gravity
                clock r1
                joints angle velocity previous_action
                reference_delta r2
                contacts feet air_time
                com offset velocity
                command
                state
                noise { joint_angle 0.01 gravity 2e-2 }
                noise { }
                latency 1..3
            }
            goal first {
                clock r1 time_driven hold 30
                reward {
                    track r1 pose 0.5 sharpness 5 weights { torso 5 arm -1 } rms anchored xyz
                    track r1 root 0.2 sharpness 4 anchored xz anchored xyz weights { leg 2 }
                    track r2 rotation 0.1 sharpness 1
                    action_rate -0.01 when moving
                    torque -0.0001
                    joint_limit -1 margin 0.05 when bare_stand
                    goal_track velocity 1.0 halving 0.5 when goal_met
                    goal_hit 2
                    knee_scaffold 0.5 full_flex 1.2 ankle_full_flex 0.4
                    arm_pose -1 free 0.3 when height_commanded
                    stand_still 1 sigma 0.2 sigma 0.4
                    leg_pose -0.5
                    leg_parallel -0.25 target 0.1
                    com_balance 0.75 gate_sigma_sq 0.5
                }
                start {
                    supine 0.3
                    spawn 0.2 { settle 10 joint_noise 0.05 settle 12 }
                    crumple 0.2 { cut 0.75 shove 1..5 settle 60 joint_noise 0.02 }
                    crumple 0.1 { }
                    standing 0.1
                    reference 0.05 r1 uniform
                    reference 0.05 r2 range 0.2..0.8
                }
                terminate {
                    nonfinite
                    height < 0.3
                    tilt > 1.2
                    forbidden_contact_steps >= 4
                    height_error > 0.5
                    rotation_error > 1
                }
                perturb { push 0.5 every 1.0..3.0 }
                success standing { height 1.2 tolerance 0.1 upright > 0.9 } at end
            }
            goal second {
                clock steps 600
                clock steps 800
                reward { alive 1 }
                start { standing 1 }
                terminate { nonfinite }
                command {
                    channels v_forward yaw_rate
                    channels v_forward v_lateral height
                    families { velocity 0.6 posture 0.4 from 0.5 velocity 0.7 }
                    families { stand 0.1 }
                    box { v_forward -0.2..0.4 to -0.5..1.2 height 1.0..1.1 }
                    box { v_lateral 0..0 }
                    resample every 2..4
                    standing_height 1.24
                }
                randomize { friction 0.5..1.5 gain 0.9..1.1 torque_scale 0.8..1 latency 0..2 pose_jitter 0.05 velocity_jitter 0.1 friction 0.6..1.4 }
                success tracking { error < 0.2 } at end
                success supine { facing < -0.8 } at end
            }
            train {
                algorithm ppo worlds 4 seed 7 timesteps 60M
                network [256 128] bounded
                network [64 64]
                learning_rate 3e-4 hold 0.5 then decay 0.1
                entropy 0.01 to 0 from 0.5
                std_cap 1 to 0.2 from 0.25 over 500k
                command_box 0 to 1 from 0 over 15M
                family_mix 0 to 1 from 0 over 40000
                step_length 0.1 to 0.4 from 0
                posture_depth 0 to 1 from 0.1 over 2M
                push_scale 0 to 1 from 0.125 over 7500k
                normalize observations clip 5 rewards clip 10
                timesteps 1000
            }
            evaluate {
                episodes 10 seeds 3 start standing noise on perturb off noise off deterministic
                randomize { friction 0.8..0.8 }
                scenarios cruise stop scenarios push
                report success survival tracking_per_phase falls tracking_error
            }
        }
        trailing 1.5 .. >= "x" { } $ // after the document
        """;

    // MotionLexer keywords as names: PolicyParser matches words by text.
    const string KeywordNames = """
        policy motion {
            requires rig "r.motion"
            requires scene "s.scene"
            reference track: skill "t.skill"
            reference s: skill "s.skill"
            observe { clock track reference_delta s }
            goal hold {
                clock for time_driven
                reward { track track pose 1 sharpness 1 weights { skill 1 } }
                start { reference 1 s uniform }
                terminate { nonfinite }
                success supine { facing < 0 } at end
            }
            train {
                algorithm skill worlds 1 seed 1 timesteps 10 network [8]
                learning_rate 1e-3 hold 0.5 then decay 0.5
                entropy 0.01 to 0 from 0.5
                std_cap 1 to 0.1 from 0
                normalize observations clip 10 rewards clip 10
            }
            evaluate { seeds 2 start for report falls }
        }
        """;

    [Theory]
    [InlineData(Minimal)]
    [InlineData(Synthetic)]
    [InlineData(KeywordNames)]
    public void Every_policy_form_maps_to_the_hand_written_ast(string text) => AssertSamePolicy(text);

    [Theory]
    [InlineData("""
        // leading comment
        compose synth {
            requires scene "a.scene"
            requires scene "b.scene"
            walk "w1.policy"
            walk "dir/w2.policy"
            getup "dir/g1.policy" when supine
            getup "g2.policy" when prone onside
            rules { stand_to_walk 2 retries 5 }
            rules { hand_over 0.25 settle_cap 3 retries 2.0 }
            commands { go { v_forward 0.5 yaw_rate -0.1 } }
            commands { halt { v_forward 0 } }
        }
        trailing 1.5 .. >= "x" { } $
        """)]
    [InlineData("compose c { requires scene \"s\" walk \"w\" getup \"g\" when a } \"open")]
    public void Every_compose_form_maps_to_the_hand_written_ast(string text) => AssertSameCompose(text);

    // Each pair edits Minimal into a document PolicyParser rejects.
    [Theory]
    [InlineData("requires rig \"r.motion\"", "")]
    [InlineData("requires scene \"s.scene\"", "")]
    [InlineData("requires rig", "requires skin")]
    [InlineData("observe { gravity }", "")]
    [InlineData("observe { gravity }", "observe { gravity velocity }")]
    [InlineData("observe { gravity }", "observe { pelvis }")]
    [InlineData("observe { gravity }", "observe { pelvis angle }")]
    [InlineData("observe { gravity }", "observe { latency 0.5..1 }")]
    [InlineData("observe { gravity }", "observe { clock }")]
    [InlineData("observe { gravity }", "observe { gravity } simulate { }")]
    [InlineData("observe { gravity }", "observe { gravity } reference r: skill \"x\" on stage \"y\"")]
    [InlineData("clock steps 100", "clock steps time_driven")]
    [InlineData("clock steps 100", "clock steps 1.5")]
    [InlineData("clock steps 100", "")]
    [InlineData("reward { alive 1 }", "")]
    [InlineData("reward { alive 1 }", "reward { alive 1 sigma }")]
    [InlineData("reward { alive 1 }", "reward { alive 1 when sometimes }")]
    [InlineData("reward { alive 1 }", "reward { track r spin 1 sharpness 1 }")]
    [InlineData("reward { alive 1 }", "reward { track r pose 1 sharpness 1 anchored xy }")]
    [InlineData("reward { alive 1 }", "reward { wiggle 1 }")]
    [InlineData("start { standing 1 }", "")]
    [InlineData("start { standing 1 }", "start { reference 1 r }")]
    [InlineData("start { standing 1 }", "start { crumple 1 }")]
    [InlineData("start { standing 1 }", "start { spawn 1 { settle 1.5 } }")]
    [InlineData("start { standing 1 }", "start { spawn 1 { cut 1 } }")]
    [InlineData("terminate { nonfinite }", "")]
    [InlineData("terminate { nonfinite }", "terminate { tilt >= 1 }")]
    [InlineData("terminate { nonfinite }", "terminate { forbidden_contact_steps > 1 }")]
    [InlineData("terminate { nonfinite }", "terminate { height <= 1 }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } perturb { }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } perturb { push 1 every 1..2 push 1 every 1..2 }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } command { families { a 1 } resample every 1..2 standing_height 1 }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } command { channels a resample every 1..2 standing_height 1 }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } command { channels a families { a 1 } standing_height 1 }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } command { channels a families { a 1 } resample every 1..2 }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } command { channels families { a 1 } resample every 1..2 standing_height 1 }")]
    [InlineData("terminate { nonfinite }", "terminate { nonfinite } randomize { latency 0..1.5 }")]
    [InlineData("success tracking { error < 0.1 } at end", "")]
    [InlineData("success tracking { error < 0.1 } at end", "success tracking { error < 0.1 }")]
    [InlineData("success tracking { error < 0.1 } at end", "success standing { height 1 tolerance 0.1 upright 0.9 } at end")]
    [InlineData("algorithm ppo ", "")]
    [InlineData("timesteps 10k", "timesteps 10G")]
    [InlineData("timesteps 10k", "timesteps 10kx")]
    [InlineData("timesteps 10k", "timesteps 1.5k")]
    [InlineData("timesteps 10k", "timesteps 10 k")]
    [InlineData("network [8]", "network [8.5]")]
    [InlineData("normalize observations clip 10 rewards clip 10", "normalize observations clip 10")]
    [InlineData("std_cap 1 to 0.1 from 0", "std_cap 1 to 0.1 from 0 over x")]
    [InlineData("std_cap 1 to 0.1 from 0", "std_cap 1 to 0.1 from 0 over 5s")]
    [InlineData("episodes 1 ", "")]
    [InlineData("start standing ", "")]
    [InlineData("report success", "report")]
    [InlineData("report success", "report success scenarios")]
    [InlineData("episodes 1", "episodes 1 noise maybe")]
    [InlineData("episodes 1", "episodes 1 scenarios report")]
    public void Invalid_policies_fail_in_both_parsers(string find, string replace)
    {
        Assert.Contains(find, Minimal);
        string text = Minimal.Replace(find, replace);
        Assert.ThrowsAny<Exception>(() => Hand(text).ParsePolicy());
        Assert.Throws<ParseException>(() => NitrogenPolicyParser.ParsePolicy(text));
    }

    [Theory]
    [InlineData("compose c { walk \"w\" getup \"g\" when a }")]
    [InlineData("compose c { requires scene \"s\" getup \"g\" when a }")]
    [InlineData("compose c { requires scene \"s\" walk \"w\" }")]
    [InlineData("compose c { requires scene \"s\" walk \"w\" getup \"g\" }")]
    [InlineData("compose c { requires scene \"s\" walk \"w\" getup \"g\" when rules { } }")]
    [InlineData("compose c { requires scene \"s\" walk \"w\" getup \"g\" when a commands { x { } } }")]
    [InlineData("compose c { requires scene \"s\" walk \"w\" getup \"g\" when a rules { retries 1.5 } }")]
    [InlineData("compose c { requires scene \"s\" walk \"w\" getup \"g\" when a rules { speed 1 } }")]
    [InlineData("compose c { requires rig \"s\" walk \"w\" getup \"g\" when a }")]
    [InlineData("compose c { requires scene \"s\" walk \"w\" getup \"g\" when a")]
    public void Invalid_composes_fail_in_both_parsers(string text)
    {
        Assert.ThrowsAny<Exception>(() => Hand(text).ParseCompose());
        Assert.Throws<ParseException>(() => NitrogenPolicyParser.ParseCompose(text));
    }

    [Theory]
    [InlineData(" %")]
    [InlineData(" !")]
    [InlineData(" #")]
    [InlineData(" @")]
    [InlineData(" `")]
    public void Trailing_characters_MotionLexer_rejects_fail_in_both(string tail)
    {
        string text = Minimal + tail;
        Assert.ThrowsAny<Exception>(() => Hand(text).ParsePolicy());
        Assert.Throws<ParseException>(() => NitrogenPolicyParser.ParsePolicy(text));
    }
}
