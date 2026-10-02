using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class StructuredHirTests
{
    static readonly SourceOrigin Origin = new("rig.motion", Guid.NewGuid(), 7, new TextSpan(3, 4));

    [Fact]
    public void Text_constant_retains_its_declared_type_and_origin()
    {
        var name = new HirText("left_hip", Origin);

        Assert.Equal(SemanticTypes.Text, name.Type);
        Assert.Equal("left_hip", name.Value);
        Assert.Equal(Origin, Assert.Single(name.Origins));
    }

    [Fact]
    public void Sequence_preserves_item_order_and_rejects_a_wrong_item_type()
    {
        var first = new HirText("left", Origin);
        var second = new HirText("right", Origin);
        var inputs = new HirNode[] { first, second };

        var sequence = new HirSequence(SemanticTypes.Text, inputs, Origin);
        inputs[0] = new HirText("changed", Origin);

        Assert.Equal(SemanticTypes.SequenceOf(SemanticTypes.Text), sequence.Type);
        Assert.Equal(["left", "right"], sequence.Items.Cast<HirText>().Select(item => item.Value));
        Assert.Throws<ArgumentException>(() => new HirSequence(SemanticTypes.Text,
            [new HirConstant(1f, SemanticTypes.Scalar, Origin)], Origin));
    }

    [Fact]
    public void Traversal_and_rewrite_visit_sequence_items_with_their_origins()
    {
        var sequence = new HirSequence(SemanticTypes.Text,
            [new HirText("left", Origin), new HirText("right", Origin)], Origin);

        Assert.Equal([sequence, .. sequence.Items], HirTraversal.PreOrder(sequence));
        var changed = Assert.IsType<HirSequence>(HirTraversal.Rewrite(sequence,
            node => node is HirText text && text.Value == "left" ? new HirText("front", Origin) : node));

        Assert.Equal(["front", "right"], changed.Items.Cast<HirText>().Select(item => item.Value));
        Assert.Equal(Origin, Assert.Single(changed.Origins));
    }

    [Fact]
    public void Optional_traversal_and_rewrite_visit_a_present_value()
    {
        var optional = new HirOptional(SemanticTypes.Text, new HirText("left", Origin), Origin);

        Assert.Equal(2, HirTraversal.PreOrder(optional).Count());
        var changed = Assert.IsType<HirOptional>(HirTraversal.Rewrite(optional,
            node => node is HirText ? new HirText("right", Origin) : node));

        Assert.Equal(SemanticTypes.OptionalOf(SemanticTypes.Text), changed.Type);
        Assert.Equal("right", Assert.IsType<HirText>(changed.Value).Value);
        Assert.Empty(HirTraversal.PreOrder(new HirOptional(SemanticTypes.Text, null, Origin)).Skip(1));
    }
}
