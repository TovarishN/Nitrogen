using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Skill blocks: NitrogenMotionParser must produce MotionParser's AST exactly, and fail where it fails.</summary>
public class SkillEquivalenceTests
{
    static object Hand(string text) => new MotionParser(new MotionLexer(text).Tokenize()).ParseFile();

    [Theory]
    [MemberData(nameof(MotionGrammarTests.SkillCorpusFiles), MemberType = typeof(MotionGrammarTests))]
    public void Every_skill_file_maps_to_the_hand_written_ast(string path)
    {
        if (path.Length == 0) return;
        string text = File.ReadAllText(path);
        object expected;
        try
        {
            expected = Hand(text);
        }
        catch (Exception handError) when (handError is ParseException or InvalidOperationException)
        {
            Assert.Throws<ParseException>(() => NitrogenMotionParser.ParseFile(text));
            return;
        }
        Assert.Equal(AstDump.Dump(expected), AstDump.Dump(NitrogenMotionParser.ParseFile(text)));
    }

    // Strict: every form must parse in MotionParser, so a broken case cannot pass by failing in both.
    [Theory]
    [InlineData("skill idle { lifecycle continuous requires motors { } output blend { } }")]
    [InlineData("""
        skill wave {
          lifecycle finite duration 2 fade_in 0.1 fade_out 0.2
          arguments { amplitude: float = 0.5 range (0..1) side: enum(port, starboard) = port }
          inputs { active: bool = true }
          requires motors { arm.shoulder arm.elbow }
          source main: tracks {
            persistent paused
            track arm[left].shoulder.target_angle { from rest to 0.5 during 0..1 easing linear release }
            track arm.elbow.kp { from 0.1 to amplitude * 2 during 0.5..1.5 easing ease_in }
            track arm.elbow.max_torque { from 1 to 2 during 0..2 easing linear }
          }
          output blend { main priority 1 }
        }
        """)]
    [InlineData("""
        skill walk {
          lifecycle looping period 1.2
          requires motors { hip }
          source gait: phases {
            phase lift {
              transition 0.2 easing ease_in_out
              pose { hip = 0.4  knee.left = rest }
              override hip { delay 0.05 transition 0.1 easing linear }
              hold 0.3
              root_position_offset (0, -0.1, 0.25)
              root_rotation_offset (0, 0, 0, 2)
            }
            phase plant { pose { hip = -0.2 } root_rotation_offset (0, 0.7071068, 0, 0.7071068) }
            phase swing { root_position_offset (1e-3, 2.5E1, -0) pose { } }
          }
          output blend { gait weighted weight 1 }
        }
        """)]
    [InlineData("""
        skill reach {
          lifecycle finite fade_in 0.1
          length 3
          requires motors { arm }
          source ctl: mpc "reach.v2" { persistent input goal = 0.3 input speed_hint = 2 }
          source sub: nested wave { persistent argument amplitude = 0.2 input active = true }
          source mix: blend { ctl additive weight 0.5 sub parameter_weighted weight blend_w }
          output blend { mix priority 2 ctl priority 1 }
          complete when arm.error < 0.01 for 0.2
          timeout 5 then fail
        }
        """)]
    [InlineData("""
        skill channels {
          lifecycle looping length 2
          requires motors { wrist }
          source t: tracks {
            track arm.wrist.angle_offset { from 0 to 1 during 0..1 easing linear }
            track arm.wrist.kd { from 0 to 1 during 0..1 easing linear }
            track arm.wrist.feedforward_torque { from 0 to 1 during 0..1 easing linear }
            track arm.wrist.max_speed { from 0 to 1 during 0..1 easing linear }
            track rig[a][b].x.target_angle { from rest to -1 during 1..2 easing ease_out }
          }
          output blend { t priority 1 }
          complete when done_flag == true
        }
        """)]
    [InlineData("skill tail { lifecycle continuous output blend { } timeout 2 then complete }")]
    [InlineData("skill one { lifecycle continuous output blend { } }\nskill two { lifecycle looping period 1 output blend { } }")]
    public void Every_skill_form_maps_to_the_hand_written_ast(string text) =>
        Assert.Equal(AstDump.Dump(Hand(text)), AstDump.Dump(NitrogenMotionParser.ParseFile(text)));

    [Theory]
    [InlineData("skill a { output blend { } }")]
    [InlineData("skill a { lifecycle finite output blend { } }")]
    [InlineData("skill a { lifecycle looping output blend { } }")]
    [InlineData("skill a { lifecycle finite duration 1 period 1 output blend { } }")]
    [InlineData("skill a { lifecycle looping period 1 duration 1 output blend { } }")]
    [InlineData("skill a { lifecycle continuous duration 1 output blend { } }")]
    [InlineData("skill a { lifecycle continuous fade_in 1 fade_in 2 output blend { } }")]
    [InlineData("skill a { lifecycle continuous }")]
    [InlineData("skill a { lifecycle continuous output blend { } output blend { } }")]
    [InlineData("skill a { lifecycle finite duration 1 output blend { } complete when x > 1 }")]
    [InlineData("skill a { lifecycle continuous arguments { x: float } inputs { x: bool } output blend { } }")]
    [InlineData("skill a { lifecycle continuous arguments { x: float } arguments { y: float } output blend { } }")]
    [InlineData("skill a { lifecycle continuous inputs { } inputs { } output blend { } }")]
    [InlineData("skill a { lifecycle continuous requires motors { m m } output blend { } }")]
    [InlineData("skill a { lifecycle continuous requires motors { } requires motors { } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source t: blend { } source t: blend { } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { hold 1 } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } pose { } } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } override hip { } } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } override hip { delay 1 delay 2 } } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } root_rotation_offset (0, 0, 0, 0) } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } root_position_offset (1, 2) } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } root_position_offset (1 deg, 0, 0) } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } root_position_offset (1e39, 0, 0) } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source p: phases { phase x { pose { } hold 1 hold 2 } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous output blend { m weight 1 } }")]
    [InlineData("skill a { lifecycle continuous source t: tracks { track arm.elbow { from 0 to 1 during 0..1 easing linear } } output blend { } }")]
    [InlineData("skill a { lifecycle continuous source t: blend { persistent } output blend { } }")]
    [InlineData("skill a { lifecycle continuous output blend { } timeout 1 then complete timeout 2 then fail }")]
    [InlineData("skill a { lifecycle continuous output blend { } complete when x complete when y }")]
    public void Invalid_skills_fail_in_both_parsers(string text)
    {
        Assert.ThrowsAny<Exception>(() => Hand(text));
        Assert.Throws<ParseException>(() => NitrogenMotionParser.ParseFile(text));
    }
}
