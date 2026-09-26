using System.Text.RegularExpressions;
using Gravity.MotionDSL.Compiler;
using Gravity.MotionDSL.IR;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl.Syntax;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// Motion.ngr's built-in names against the hand compiler's tables (issue 237), so the two cannot
/// drift. Where the compiler keeps a name list inside a switch, a compiled probe checks each name.
/// </summary>
public class MotionBuiltinParityTests
{
    const string Probe = """
        skill probe {
            lifecycle finite duration 2
            requires motors {
                biped.knee
            }
            source authored: phases {
                phase a {
                    pose {
                        biped.knee = rest
                    }
                    hold 0.5
                }
                phase b {
                    transition 0.5 easing EASING
                    pose {
                        biped.knee = 0.1
                    }
                }
            }
            output blend {
                authored priority 1
            }
            complete when EXPR
            timeout 5 then fail
        }
        """;

    static string[] Builtins(string kind) =>
        MotionModule.Instance.Builtins.Where(b => b.Kind == kind).SelectMany(b => b.Names).ToArray();

    static string Snake(string pascal) => Regex.Replace(pascal, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    static SkillCompileException? CompileProbe(string easing, string condition)
    {
        string text = Probe.Replace("EASING", easing).Replace("EXPR", condition);
        try
        {
            MotionCompiler.Compile(new MotionParser(new MotionLexer(text).Tokenize()).ParseFile());
            return null;
        }
        catch (SkillCompileException error)
        {
            return error;
        }
    }

    [Fact]
    public void The_probe_compiles_and_catches_unknown_names()
    {
        Assert.Null(CompileProbe("linear", "time > 1"));
        Assert.Equal(SkillDiagnosticCode.UnknownEasing, CompileProbe("wobbly", "time > 1")?.Code);
        Assert.Equal(SkillDiagnosticCode.UnknownSymbol, CompileProbe("linear", "nowhere > 1")?.Code);
        Assert.Equal(SkillDiagnosticCode.UnknownSymbol, CompileProbe("linear", "nothing(1) > 1")?.Code);
    }

    [Fact]
    public void Reward_builtins_are_the_reward_functions() =>
        Assert.Equal(Enum.GetNames<RewardFunction>().Select(Snake).Order(), Builtins("reward").Order());

    [Fact]
    public void Easing_builtins_are_the_easings_the_compiler_accepts()
    {
        Assert.Equal(Enum.GetValues<EasingKind>().Length, Builtins("easing").Length);
        Assert.All(Builtins("easing"), easing => Assert.Null(CompileProbe(easing, "time > 1")));
    }

    [Fact]
    public void Value_builtins_are_the_observations()
    {
        Assert.Equal(SkillObservationScalars.Count, Builtins("value").Length);
        Assert.All(Builtins("value"), name =>
            Assert.NotEqual(SkillDiagnosticCode.UnknownSymbol, CompileProbe("linear", name + " > 0")?.Code));
    }

    [Fact]
    public void Function_builtins_are_functions_the_compiler_knows()
    {
        Assert.NotEmpty(Builtins("function"));
        Assert.All(Builtins("function"), function =>
            Assert.NotEqual(SkillDiagnosticCode.UnknownSymbol, CompileProbe("linear", function + "(1) > 0")?.Code));
    }

    [Fact]
    public void Axis_builtins_are_the_three_axes() => Assert.Equal(new[] { "x", "y", "z" }, Builtins("axis"));

    /// <summary>Subjects have no compiler table either: the compiler keeps them unchecked as a PartRef.</summary>
    [Fact]
    public void Subject_builtins_are_the_behavior_subjects() => Assert.Equal(new[] { "torso", "com", "target" }, Builtins("subject"));
}
