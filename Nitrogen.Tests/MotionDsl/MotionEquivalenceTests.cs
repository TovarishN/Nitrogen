using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>NitrogenMotionParser must produce MotionParser's AST exactly, and fail where it fails.</summary>
public class MotionEquivalenceTests
{
    static object Hand(string text) => new MotionParser(new MotionLexer(text).Tokenize()).ParseFile();

    static void AssertSameAst(string text)
    {
        object expected;
        try
        {
            expected = Hand(text);
        }
        catch (Exception handError) when (handError is ParseException or InvalidOperationException)
        {
            // The hand-written parser rejects it: Nitrogen must reject it too.
            Assert.Throws<ParseException>(() => NitrogenMotionParser.ParseFile(text));
            return;
        }
        Assert.Equal(AstDump.Dump(expected), AstDump.Dump(NitrogenMotionParser.ParseFile(text)));
    }

    [Theory]
    [MemberData(nameof(MotionGrammarTests.CorpusFiles), MemberType = typeof(MotionGrammarTests))]
    public void Every_motion_file_maps_to_the_hand_written_ast(string path)
    {
        if (path.Length == 0) return;
        AssertSameAst(File.ReadAllText(path));
    }

    [Theory]
    [InlineData("body b { part root box(1, 2, 3) }")]
    [InlineData("body b { let w = 2 + 3 * 4\n part root box(w, 1, 1) mass=w / 2 color=(1, 0, 0) at=(0, 1, 0) rot=(0, 90, 0) mass=3 }")]
    [InlineData("body b { part root box(1,1,1) { part arm capsule(0.1, 1) { joint revolute axis=(0, 0, 1) range=(-90 deg..45 deg) motor spring torque=10 force=2 speed=3 length=0.5 hertz=4 damping=0.7 anchor=(0,0,0) anchor_self=(1,0,0) linear_speed=(1,2,3) angular_axis=(0,1,0) kp=100 kd=5 max_speed=12 pose=0.25 torque=11 } } }")]
    [InlineData("body b { part root box(1,1,1) { joint motor axis=y part a box(1,1,1) { joint weld anchor=(0,1,0) } joint hinge axis=z } }")]
    [InlineData("body b { motor_bindings { legs.left.hip = hip_motor\n arm = ${side}_arm } part root box(1,1,1) }")]
    [InlineData("body b { part ${side}_leg box(1,1,1) { part leg_${i}_l box(1,1,1) part motor box(1,1,1) part a${x}b${y}c box(1,1,1) part ${only}_x box(1,1,1) } }")]
    [InlineData("body b { part root box(1,1,1) { repeat 3 as i { part seg_${i}_n box(1,1,1) } for side in [left, right] { part ${side}_arm box(1,1,1) { for f in [a] { part ${f}x box(1,1,1) } } } } }")]
    [InlineData("behavior walk { inherit base model \"m.onnx\" reward { alive * 1.0 + energy * -0.001 } done when torso.height < 0.5 or not upright(torso) inherit other }")]
    [InlineData("motion wave { at 0 pose { arm.angle = 45 deg, leg.angle = -10 deg } hold 0.5 s at 1.5 s pose { arm.angle = 0 } }")]
    [InlineData("body b { let a = 1 < 2 == true\n let c = x ? y : z ? u : v\n let d = f() + g(1, h(2)).value\n let e = (1 + 2) * -3.5e-2 / 4\n let n = not a and b or c\n let m = a <= b != c >= d\n let p = a + b ? c : d\n let q = -a.b.c\n part root box(1,1,1) }")]
    [InlineData("body b { let t = 2e3 + 2E+3 + 1.5 + 0.5s + 3deg + false\n part root box(1,1,1) }")]
    [InlineData("// header\nbody b { // c\n part root box(1,1,1) // end\n }\nbehavior x { }\nmotion m { }")]
    [InlineData("body anchors { part root box(1,1,1) }")]
    [InlineData("")]
    public void Every_language_form_maps_to_the_hand_written_ast(string text) =>
        // Strict: every form must parse in MotionParser, so a case that accidentally fails in both
        // parsers cannot pass silently.
        Assert.Equal(AstDump.Dump(Hand(text)), AstDump.Dump(NitrogenMotionParser.ParseFile(text)));

    [Theory]
    [InlineData("body b { }")]
    [InlineData("body let { part root box(1,1,1) }")]
    [InlineData("body b { part root box() }")]
    [InlineData("body b { part root box(1,1,1) { joint revolute axis=w } }")]
    [InlineData("body b { part phase box(1,1,1) }")]
    [InlineData("body b { part true box(1,1,1) }")]
    [InlineData("behavior b { reward { 1 + } }")]
    [InlineData("body b { part root box(1,1,1) } junk")]
    [InlineData("motion m { at 1 pose { arm = 1 } }")]
    [InlineData("body b { part root box(1,1,1) # }")]
    [InlineData("body b { part root box(1,1,1) { repeat 2 as i { joint weld } } }")]
    [InlineData("body b { let s = 1\n part root box(1,1,1) }")]
    public void Invalid_input_fails_in_both_parsers(string text)
    {
        Assert.ThrowsAny<Exception>(() => Hand(text));
        Assert.Throws<ParseException>(() => NitrogenMotionParser.ParseFile(text));
    }
}
