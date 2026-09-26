using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

public sealed class AngleProofTests
{
    [Fact]
    public void Caller_owned_snapshot_identity_stamps_the_lowered_tree()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("1deg"));
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        var file = new ProjectSemantics(project)["a.skill"];
        var catalog = NitrogenMotionParser.Language.SemanticCatalog;
        var id = Guid.NewGuid();

        var result = HirLowering.Lower(file, catalog, id);

        Assert.All(result.Roots.SelectMany(HirTraversal.PreOrder), node =>
            Assert.All(node.Origins, origin => Assert.Equal(id, origin.SnapshotId)));
        Assert.NotEmpty(result.Roots);
        Assert.Throws<ArgumentException>(() => HirLowering.Lower(file, catalog, Guid.Empty));
        var first = HirLowering.Lower(file, catalog);
        var second = HirLowering.Lower(file, catalog);
        Assert.NotEqual(first.Roots[0].Origins[0].SnapshotId, second.Roots[0].Origins[0].SnapshotId);
    }

    static LoweringResult Motion(string to, string channel = "target_angle")
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track(to).Replace("target_angle", channel, StringComparison.Ordinal));
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        return HirLowering.Lower(new ProjectSemantics(project)["a.skill"], NitrogenMotionParser.Language.SemanticCatalog);
    }

    static LoweringResult Policy(string radians)
    {
        var text = PolicyValueChecksTests.Reference.Replace("clamp knee 0.2", "clamp knee " + radians, StringComparison.Ordinal);
        using var parsed = NitrogenPolicyParser.Language.Parse(text, PolicyModule.PolicyDocument);
        var project = new Project(NitrogenPolicyParser.Language);
        project.Set("a.policy", parsed.Tree);
        return HirLowering.Lower(new ProjectSemantics(project)["a.policy"], NitrogenPolicyParser.Language.SemanticCatalog);
    }

    [Fact]
    public void Motion_degree_and_policy_radian_share_one_angle_value()
    {
        var motionText = MotionTypingTests.Skill(MotionTypingTests.Track("57.29578deg"));
        using var motionParsed = NitrogenMotionParser.Language.Parse(motionText, MotionModule.File);
        var motionProject = new Project(NitrogenMotionParser.Language);
        motionProject.Set("a.skill", motionParsed.Tree);
        var motion = HirLowering.Lower(new ProjectSemantics(motionProject)["a.skill"], NitrogenMotionParser.Language.SemanticCatalog);

        var policyText = PolicyValueChecksTests.Reference.Replace("clamp knee 0.2", "clamp knee 1", StringComparison.Ordinal);
        using var policyParsed = NitrogenPolicyParser.Language.Parse(policyText, PolicyModule.PolicyDocument);
        var policyProject = new Project(NitrogenPolicyParser.Language);
        policyProject.Set("a.policy", policyParsed.Tree);
        var policy = HirLowering.Lower(new ProjectSemantics(policyProject)["a.policy"], NitrogenPolicyParser.Language.SemanticCatalog);

        Assert.Empty(motion.Diagnostics);
        Assert.Empty(policy.Diagnostics);
        var motionRoot = Assert.Single(motion.Roots, root => root is HirOperation op && op.Signature.Id == "Motion.AngleSlot");
        var policyRoot = Assert.Single(policy.Roots, root => root is HirOperation op && op.Signature.Id == "Policy.Clamp");
        var motionAngle = Assert.IsType<HirConstant>(Assert.Single(((HirOperation)motionRoot).Arguments));
        var policyAngle = Assert.IsType<HirConstant>(Assert.Single(((HirOperation)policyRoot).Arguments));
        Assert.Equal(SemanticTypes.Angle, motionAngle.Type);
        Assert.Equal(motionAngle.Type, policyAngle.Type);
        Assert.InRange(MathF.Abs(motionAngle.Value - policyAngle.Value), 0f, 1e-5f);
        Assert.Equal("57.29578deg", motionText.Substring(motionAngle.Origins[0].Span.Start, motionAngle.Origins[0].Span.Length));
        Assert.Equal("1", policyText.Substring(policyAngle.Origins[0].Span.Start, policyAngle.Origins[0].Span.Length));
    }

    [Fact]
    public void Skill_type_mapping_preserves_domains()
    {
        Assert.Equal(SemanticTypes.Scalar, MotionSemanticTypes.Map(SkillType.Float));
        Assert.Equal(SemanticTypes.Bool, MotionSemanticTypes.Map(SkillType.Bool));
        Assert.Equal(SemanticTypes.Angle, MotionSemanticTypes.Map(SkillType.Angle));
        Assert.Equal(SemanticType.Named("Units", "Time"), MotionSemanticTypes.Map(SkillType.Time));
        Assert.Equal(SemanticTypes.Error, MotionSemanticTypes.Map(SkillType.Error));
        Assert.NotEqual(MotionSemanticTypes.Map(SkillType.EnumOf("walk", "walk")),
            MotionSemanticTypes.Map(SkillType.EnumOf("run", "run")));
        Assert.NotEqual(SemanticTypes.Scalar, MotionSemanticTypes.Map(SkillType.EntityOf("part")));
        Assert.Throws<NotSupportedException>(() => MotionSemanticTypes.Map(new SkillType(SkillTypeKind.Float, Dimension.Mixed)));
    }

    [Fact]
    public void Valid_non_angle_track_does_not_produce_angle_root()
    {
        var nonAngle = Motion("1", "kp");
        Assert.Empty(nonAngle.Roots);
        Assert.Empty(nonAngle.Diagnostics);
    }

    [Theory]
    [InlineData("gain + 1deg", "Motion.AddScalarAngle", true)]
    [InlineData("1deg + gain", "Motion.AddAngleScalar", false)]
    public void Dynamic_angle_addition_keeps_order_types_and_exact_spans(string expression, string signature, bool scalarFirst)
    {
        var result = Motion(expression);
        Assert.Empty(result.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(result.Roots));
        Assert.Equal("Motion.AngleSlot", root.Signature.Id);
        var add = Assert.IsType<HirOperation>(Assert.Single(root.Arguments));
        Assert.Equal(signature, add.Signature.Id);
        Assert.Equal(SemanticTypes.Angle, add.Type);
        var reference = Assert.IsType<HirSymbolRef>(add.Arguments[scalarFirst ? 0 : 1]);
        Assert.Equal(SemanticTypes.Scalar, reference.Type);
        Assert.Equal("gain", expression.Substring(reference.Origins[0].Span.Start -
            (MotionTypingTests.Skill(MotionTypingTests.Track(expression)).IndexOf(expression, StringComparison.Ordinal)),
            reference.Origins[0].Span.Length));
        var angle = Assert.IsType<HirConstant>(add.Arguments[scalarFirst ? 1 : 0]);
        Assert.Equal(SemanticTypes.Angle, angle.Type);
        Assert.InRange(MathF.Abs(angle.Value - MathF.PI / 180f), 0f, 1e-6f);
    }

    [Fact]
    public void Dynamic_scalar_addition_has_explicit_angle_slot_conversion()
    {
        var result = Motion("gain + 2");
        Assert.Empty(result.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(result.Roots));
        var conversion = Assert.IsType<HirOperation>(Assert.Single(root.Arguments));
        Assert.Equal("Motion.ContextualAngle", conversion.Signature.Id);
        Assert.Equal(SemanticTypes.Scalar, Assert.Single(conversion.Arguments).Type);
    }

    [Fact]
    public void Parentheses_keep_their_own_origin()
    {
        var result = Motion("(gain) + 1deg");
        Assert.Empty(result.Diagnostics);
        var add = Assert.IsType<HirOperation>(Assert.Single(Assert.IsType<HirOperation>(Assert.Single(result.Roots)).Arguments));
        var group = Assert.IsType<HirOperation>(add.Arguments[0]);
        Assert.Equal("Motion.GroupScalar", group.Signature.Id);
        Assert.Equal(6, group.Origins[0].Span.Length);
        Assert.IsType<HirSymbolRef>(Assert.Single(group.Arguments));
    }

    [Fact]
    public void Dynamic_reference_uses_the_existing_binder_symbol()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"));
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        var file = new ProjectSemantics(project)["a.skill"];
        var referenceNode = Assert.Single(file.Binding.References,
            r => text.Substring(r.NameSpan.Start, r.NameSpan.Length) == "gain").Node;
        var bound = file.SymbolOf(referenceNode);

        var result = HirLowering.Lower(file, NitrogenMotionParser.Language.SemanticCatalog);

        var add = Assert.IsType<HirOperation>(Assert.Single(Assert.IsType<HirOperation>(Assert.Single(result.Roots)).Arguments));
        var reference = Assert.IsType<HirSymbolRef>(add.Arguments[0]);
        Assert.Same(bound, reference.Symbol.Binding);
        Assert.Equal("gain", text.Substring(reference.Origins[0].Span.Start, reference.Origins[0].Span.Length));
        Assert.Equal("gain + 1deg", text.Substring(add.Origins[0].Span.Start, add.Origins[0].Span.Length));
    }

    [Fact]
    public void Unsupported_valid_expression_does_not_hide_a_supported_track()
    {
        var text = TwoTracks("gain * 2 + 1deg", "gain + 1deg");
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        var result = HirLowering.Lower(new ProjectSemantics(project)["a.skill"], NitrogenMotionParser.Language.SemanticCatalog);
        Assert.Empty(result.Diagnostics);
        var root = Assert.Single(result.Roots);
        Assert.Equal("gain + 1deg", text.Substring(Assert.IsType<HirOperation>(Assert.Single(Assert.IsType<HirOperation>(root).Arguments)).Origins[0].Span.Start, 11));
    }

    [Theory]
    [InlineData("missing + 1deg")]
    [InlineData("true + 1deg")]
    [InlineData("gain +")]
    [InlineData("1e999deg")]
    public void Invalid_track_does_not_poison_an_independent_valid_track(string invalid)
    {
        var text = TwoTracks(invalid, "gain + 1deg");
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        var result = HirLowering.Lower(new ProjectSemantics(project)["a.skill"], NitrogenMotionParser.Language.SemanticCatalog);
        Assert.Single(result.Roots);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Origin.Span.Start < text.IndexOf("gain + 1deg", StringComparison.Ordinal));
    }

    [Fact]
    public void Nonfinite_literal_inside_dynamic_add_has_a_literal_located_lowering_diagnostic()
    {
        var text = TwoTracks("gain + 1e999deg", "gain + 1deg");
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);

        var result = HirLowering.Lower(new ProjectSemantics(project)["a.skill"], NitrogenMotionParser.Language.SemanticCatalog);

        Assert.Single(result.Roots);
        Assert.Contains(result.Diagnostics, diagnostic =>
            text.Substring(diagnostic.Origin.Span.Start, diagnostic.Origin.Span.Length) == "1e999deg");
    }

    static string TwoTracks(string first, string second)
    {
        var source = MotionTypingTests.Track(first);
        var closingSource = source.LastIndexOf("\n    }", StringComparison.Ordinal);
        var nextTrack = $"\n        track biped.a.target_angle {{\n            from rest to {second} during 0s..1s easing linear\n        }}";
        return MotionTypingTests.Skill(source.Insert(closingSource, nextTrack));
    }

    [Fact]
    public void Constant_angle_expression_lowers_at_the_expression_span()
    {
        var result = Motion("0.5deg + 0.5deg");
        Assert.Empty(result.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(result.Roots));
        Assert.InRange(MathF.Abs(Assert.IsType<HirConstant>(Assert.Single(root.Arguments)).Value - MathF.PI / 180f), 0f, 1e-6f);
    }

    [Fact]
    public void Bare_motion_number_takes_angle_type_from_its_target_slot()
    {
        var result = Motion("1");
        Assert.Empty(result.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(result.Roots));
        var value = Assert.IsType<HirConstant>(Assert.Single(root.Arguments));
        Assert.Equal(SemanticTypes.Angle, value.Type);
        Assert.Equal(1f, value.Value);
    }

    [Fact]
    public void Invalid_angle_values_do_not_produce_roots()
    {
        var motion = Motion("1s");
        var policy = Policy("-1");
        var motionOverflow = Motion("1e999deg");
        var policyOverflow = Policy("1e999");
        Assert.Empty(motion.Roots);
        Assert.Empty(policy.Roots);
        Assert.Empty(motionOverflow.Roots);
        Assert.Empty(policyOverflow.Roots);
        Assert.NotEmpty(motion.Diagnostics);
        Assert.NotEmpty(policy.Diagnostics);
        Assert.Contains(motionOverflow.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
        Assert.Contains(policyOverflow.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
        Assert.Equal(2, policy.Diagnostics[0].Origin.Span.Length);
        Assert.Equal(5, policyOverflow.Diagnostics[0].Origin.Span.Length);
    }
}
