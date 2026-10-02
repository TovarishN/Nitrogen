using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Inspection finds the innermost lowered node at a position through EVERY composite HIR
/// node — sequence items, an optional's value, a repeat's template — not only operation arguments.
/// A sequence-lowered block (Gravity's policy `actuate { clamp knee 1 }`) lost hover otherwise.</summary>
public class HirInspectionTests
{
    const string Uri = "file:///w/a.txt";
    static readonly Guid Snapshot = Guid.NewGuid();
    static SourceOrigin At(int start, int length) => new(Uri, Snapshot, 0, new TextSpan(start, length));

    static readonly SemanticType Item = SemanticType.Named("T", "Item");
    static readonly OperationSignature Clamp = new("T.Clamp", Item, SemanticTypes.Angle);
    static readonly OperationSignature Block = new("T.Block", Item, SemanticTypes.SequenceOf(Item));
    static readonly OperationSignature Maybe = new("T.Maybe", Item, SemanticTypes.OptionalOf(Item));

    static HirOperation ClampAt(int start) =>
        new(Clamp, [new HirConstant(1f, SemanticTypes.Angle, At(start + 11, 1))], [At(start, 12)]);

    [Fact]
    public void Finds_a_constant_inside_a_sequence_item()
    {
        var root = new HirOperation(Block, [new HirSequence(Item, [ClampAt(10)], At(8, 20))], [At(0, 40)]);

        var found = HirInspection.Find([root], Uri, 21);

        Assert.NotNull(found);
        Assert.IsType<HirConstant>(found!.Value.Node);
        Assert.Same(root, found.Value.Root);
        Assert.Equal(SemanticTypes.Angle, found.Value.Node.Type);
    }

    [Fact]
    public void Finds_a_constant_inside_an_optional_value()
    {
        var root = new HirOperation(Maybe, [new HirOptional(Item, ClampAt(10), At(8, 20))], [At(0, 40)]);

        var found = HirInspection.Find([root], Uri, 21);

        Assert.IsType<HirConstant>(found!.Value.Node);
    }

    [Fact]
    public void Outside_every_origin_finds_nothing() =>
        Assert.Null(HirInspection.Find([ClampAt(10)], Uri, 100));
}
