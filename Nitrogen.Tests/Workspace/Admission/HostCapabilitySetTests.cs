using Gravity.RagdollEditor;
using Nitrogen.Geometry;
using Nitrogen.Semantic;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class HostCapabilitySetTests
{
    static HostCapability Box() => new(BoxMeshModule.BoxSignature,
        GeometryBoxMeshHost.Binding, CapabilityEffect.Pure);

    [Fact]
    public void Grant_copies_entries_and_resolves_the_exact_box_binding()
    {
        var source = new List<HostCapability> { Box() };
        var set = new HostCapabilitySet(source);
        source.Clear();
        Assert.True(set.TryGet("Geometry.BoxMesh", out var grant));
        Assert.Equal(BoxMeshModule.BoxSignature, grant.Signature);
        Assert.Same(GeometryBoxMeshHost.Binding, grant.Binding);
        Assert.False(set.TryGet("Geometry.Unknown", out _));
    }

    [Fact]
    public void Duplicate_malformed_or_effectful_grants_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new HostCapabilitySet([Box(), Box()]));
        Assert.Throws<ArgumentException>(() => new HostCapabilitySet([null!]));
        var changedInputs = new OperationSignature("Geometry.BoxMesh", BoxMeshModule.MeshType,
            SemanticTypes.Scalar);
        Assert.Throws<ArgumentException>(() => new HostCapabilitySet(
            [new HostCapability(changedInputs, GeometryBoxMeshHost.Binding, CapabilityEffect.Pure)]));
        var changedId = new OperationSignature("Geometry.Other", BoxMeshModule.MeshType,
            SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
        Assert.Throws<ArgumentException>(() => new HostCapabilitySet(
            [new HostCapability(changedId, GeometryBoxMeshHost.Binding, CapabilityEffect.Pure)]));
        Assert.Throws<ArgumentException>(() => new HostCapabilitySet(
            [new HostCapability(BoxMeshModule.BoxSignature, GeometryBoxMeshHost.Binding, (CapabilityEffect)1)]));
    }
}
