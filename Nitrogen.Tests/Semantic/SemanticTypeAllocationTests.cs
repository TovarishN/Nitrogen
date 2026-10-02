using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class SemanticTypeAllocationTests
{
    [Fact]
    public void RepeatedIdentityReadsDoNotAllocateIdentifierStrings()
    {
        var left = SemanticType.Named("Rig", "Sequence", SemanticType.Named("Rig", "Part"));
        var right = SemanticType.Named("Rig", "Sequence", SemanticType.Named("Rig", "Part"));
        for (var i = 0; i < 100; i++) ReadIdentity(left, right);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var valid = true;
        for (var i = 0; i < 1_000; i++) valid &= ReadIdentity(left, right);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(valid);
        Assert.True(allocated <= 1_024, $"Repeated semantic type identity reads allocated {allocated:N0} bytes.");
    }

    static bool ReadIdentity(SemanticType left, SemanticType right) =>
        left.Id == "Rig.Sequence" && left.Equals(right) && left.GetHashCode() == right.GetHashCode();

    [Fact]
    public void IdentityRetainsQualifiedNameAndOrderedArgumentSemantics()
    {
        var left = SemanticType.Named("Rig", "Pair", SemanticTypes.Scalar, SemanticTypes.Angle);
        var equal = SemanticType.Named("Rig", "Pair", SemanticType.Named("Core", "Scalar"), SemanticType.Named("Units", "Angle"));
        Assert.Equal(left, equal);
        Assert.Equal(left.GetHashCode(), equal.GetHashCode());
        Assert.Equal("Rig.Pair<Core.Scalar, Units.Angle>", left.ToString());
        Assert.NotEqual(left, SemanticType.Named("Rig", "Pair", SemanticTypes.Angle, SemanticTypes.Scalar));
        Assert.NotEqual(left, SemanticType.Named("Other", "Pair", SemanticTypes.Scalar, SemanticTypes.Angle));
        Assert.False(left.Equals(null));
        Assert.True(left.Equals(left));
        // Existing identity is the complete qualified string, including dots in either component.
        Assert.Equal(SemanticType.Named("A.B", "C"), SemanticType.Named("A", "B.C"));
    }
}
