using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class HirNodeTests
{
    static readonly SourceOrigin Old = new("a.skill", new Guid("11111111-1111-1111-1111-111111111111"), 7, new TextSpan(12, 5));
    static readonly SourceOrigin New = new("a.skill", new Guid("22222222-2222-2222-2222-222222222222"), 7, new TextSpan(12, 5));

    [Fact]
    public void Origins_distinguish_reparsed_snapshots_with_the_same_node_index()
    {
        Assert.NotEqual(Old, New);
        Assert.Equal(Old.Span, New.Span);
    }

    [Fact]
    public void Operation_validates_argument_type_count_and_origins()
    {
        var angle = new HirConstant(0.5f, SemanticTypes.Angle, Old);
        var signature = new OperationSignature("Motion.AngleSlot", SemanticTypes.Angle, SemanticTypes.Angle);

        Assert.Equal(SemanticTypes.Angle, new HirOperation(signature, [angle], [Old]).Type);
        Assert.Throws<ArgumentException>(() => new HirOperation(signature,
            [new HirConstant(1f, SemanticTypes.Bool, Old)], [Old]));
        Assert.Throws<ArgumentException>(() => new HirOperation(signature, [], [Old]));
        Assert.Throws<ArgumentException>(() => new HirOperation(signature, [angle], []));
    }

    [Fact]
    public void Hir_copies_mutable_input_collections()
    {
        var original = new HirConstant(0.5f, SemanticTypes.Angle, Old);
        var replacement = new HirConstant(1f, SemanticTypes.Angle, New);
        var children = new HirNode[] { original };
        var origins = new[] { Old };
        var signature = new OperationSignature("Motion.AngleSlot", SemanticTypes.Angle, SemanticTypes.Angle);

        var operation = new HirOperation(signature, children, origins);
        children[0] = replacement;
        origins[0] = New;

        Assert.Same(original, Assert.Single(operation.Arguments));
        Assert.Equal(Old, Assert.Single(operation.Origins));
    }

    [Fact]
    public void Rewrite_preserves_old_and_new_source_origins()
    {
        var original = new HirConstant(0.5f, SemanticTypes.Angle, Old);
        var signature = new OperationSignature("Motion.AngleSlot", SemanticTypes.Angle, SemanticTypes.Angle);
        var root = new HirOperation(signature, [original], [Old]);

        var rewritten = Assert.IsType<HirOperation>(HirTraversal.Rewrite(root,
            node => node is HirConstant ? new HirConstant(1f, SemanticTypes.Angle, New) : node));

        Assert.Equal(new HirNode[] { rewritten, rewritten.Arguments[0] }, HirTraversal.PreOrder(rewritten));
        Assert.Equal(new[] { Old, New }, rewritten.Origins);
        Assert.Equal(1f, Assert.IsType<HirConstant>(rewritten.Arguments[0]).Value);
    }

    [Fact]
    public void Rewrite_rejects_a_child_that_breaks_the_signature()
    {
        var original = new HirConstant(0.5f, SemanticTypes.Angle, Old);
        var signature = new OperationSignature("Motion.AngleSlot", SemanticTypes.Angle, SemanticTypes.Angle);
        var root = new HirOperation(signature, [original], [Old]);

        Assert.Throws<ArgumentException>(() => HirTraversal.Rewrite(root,
            node => node is HirConstant ? new HirConstant(1f, SemanticTypes.Bool, New) : node));
    }

    [Fact]
    public void Rewrite_of_a_root_preserves_both_original_and_replacement_origins()
    {
        var original = new HirConstant(0.5f, SemanticTypes.Angle, Old);
        var rewritten = Assert.IsType<HirConstant>(HirTraversal.Rewrite(original,
            _ => new HirConstant(1f, SemanticTypes.Angle, New)));
        Assert.Equal(new[] { Old, New }, rewritten.Origins);
        Assert.Throws<ArgumentException>(() => HirTraversal.Rewrite(original,
            _ => new HirConstant(1f, SemanticTypes.Bool, New)));
    }

    [Fact]
    public void Symbol_reference_keeps_existing_binder_identity()
    {
        string source = MotionTypingTests.Skill(MotionTypingTests.Track("1deg"));
        using var parsed = NitrogenMotionParser.Language.Parse(source, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        var symbol = Assert.Single(project.Set("a.skill", parsed.Tree).Declarations, item => item.Name == "probe");
        var typed = SemanticSymbol.From(symbol, "Motion", SemanticTypes.Angle);

        var reference = new HirSymbolRef(typed, Old);

        Assert.Same(symbol, reference.Symbol.Binding);
        Assert.Equal(SemanticTypes.Angle, reference.Type);
        Assert.Equal(Old, Assert.Single(reference.Origins));
    }
}
