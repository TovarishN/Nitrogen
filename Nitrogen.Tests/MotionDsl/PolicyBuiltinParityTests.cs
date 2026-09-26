using System.Reflection;
using Gravity.MotionDSL.Compiler;
using Nitrogen.MotionDsl.PolicySyntax;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Policy.ngr's built-in names against the policy and compose compilers' tables (issue 237).</summary>
public class PolicyBuiltinParityTests
{
    static string[] Builtins(string kind) =>
        PolicyModule.Instance.Builtins.Where(b => b.Kind == kind).SelectMany(b => b.Names).Order().ToArray();

    /// <summary>PolicyCompiler keeps its tables private; the names are the language, so the test reads them.</summary>
    static string[] Table(string field) =>
        ((IEnumerable<string>)typeof(PolicyCompiler).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
        .Order().ToArray();

    [Theory]
    [InlineData("goal_channel", "KnownChannels")]
    [InlineData("family", "KnownFamilies")]
    [InlineData("scenario", "KnownScenarios")]
    [InlineData("noise", "NoiseChannels")]
    public void Builtins_are_the_policy_compilers_table(string kind, string field) =>
        Assert.Equal(Table(field), Builtins(kind));

    [Fact]
    public void Pose_builtins_are_the_compose_compilers_poses() =>
        Assert.Equal(ComposeCompiler.KnownPoses.Order().ToArray(), Builtins("pose"));
}
