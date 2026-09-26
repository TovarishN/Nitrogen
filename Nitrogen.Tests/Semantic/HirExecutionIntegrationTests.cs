using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

public sealed class HirExecutionIntegrationTests
{
    [Fact]
    public void Dynamic_motion_angle_track_lowers_binds_and_executes()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"));
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        Assert.True(parsed.Success);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        var lowered = HirLowering.Lower(new ProjectSemantics(project)["a.skill"],
            NitrogenMotionParser.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots,
            node => node is HirOperation operation && operation.Signature.Id == "Motion.AngleSlot"));
        var reference = Assert.IsType<HirSymbolRef>(Assert.Single(HirTraversal.PreOrder(root),
            node => node is HirSymbolRef));
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> add = values =>
            new(SemanticTypes.Angle, values[0].Number + values[1].Number);
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> slot = values => values[0];
        var registry = HostOperationRegistry.Bind(NitrogenMotionParser.Language.SemanticCatalog,
            [new HostOperationBinding(MotionExpressionLowerer.AddScalarAngle, add),
                new HostOperationBinding(MotionHirLowerer.Signature, slot)]);
        var inputs = new Dictionary<Symbol, ExecutionValue>
        {
            [reference.Symbol.Binding] = new(SemanticTypes.Scalar, 0.5f)
        };

        var result = HirEvaluator.Evaluate(root, registry, inputs);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(SemanticTypes.Angle, result.Value!.Type);
        Assert.InRange(MathF.Abs(result.Value.Number - (0.5f + MathF.PI / 180f)), 0f, 1e-5f);
        Assert.Equal(root.Origins, result.Origins);
    }

    [Fact]
    public void Policy_clamp_constant_lowers_binds_and_executes()
    {
        using var parsed = NitrogenPolicyParser.Language.Parse(PolicyValueChecksTests.Reference,
            PolicyModule.PolicyDocument);
        Assert.True(parsed.Success);
        var project = new Project(NitrogenPolicyParser.Language);
        project.Set("a.policy", parsed.Tree);
        var lowered = HirLowering.Lower(new ProjectSemantics(project)["a.policy"],
            NitrogenPolicyParser.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots,
            node => node is HirOperation operation && operation.Signature.Id == "Policy.Clamp"));
        var registry = HostOperationRegistry.Bind(NitrogenPolicyParser.Language.SemanticCatalog,
            [new HostOperationBinding(PolicyHirLowerer.Signature,
                (Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)(values => values[0]))]);

        var result = HirEvaluator.Evaluate(root, registry, new Dictionary<Symbol, ExecutionValue>());

        Assert.Empty(result.Diagnostics);
        Assert.Equal(SemanticTypes.Angle, result.Value!.Type);
        Assert.Equal(0.2f, result.Value.Number);
        Assert.Equal(root.Origins, result.Origins);
    }
}
