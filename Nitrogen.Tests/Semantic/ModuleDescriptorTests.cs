using Nitrogen.Semantic;
using Nitrogen.Tests.Uses;
using Xunit;

namespace Nitrogen.Tests;

public sealed class ModuleDescriptorTests
{
    [Fact]
    public void Descriptor_copies_caller_collections()
    {
        var names = new List<string> { "Doc" };
        var required = new List<OperationSignature> { new("Test.Read", SemanticTypes.Scalar) };
        var descriptor = new ModuleDescriptor("Test", UsesModule.Instance, null, names, required);

        names.Add("Other");
        required.Clear();

        Assert.Equal(["Doc"], descriptor.StartRules);
        Assert.Equal("Test.Read", Assert.Single(descriptor.RequiredOperations).Id);
    }

    [Fact]
    public void Descriptor_requires_identity_and_a_module_part()
    {
        Assert.Throws<ArgumentException>(() => new ModuleDescriptor(" ", UsesModule.Instance, null, [], []));
        Assert.Throws<ArgumentException>(() => new ModuleDescriptor("Empty", null, null, [], []));
        Assert.Throws<ArgumentException>(() => new ModuleDescriptor("Bad", null,
            new SemanticModule("Bad", [], [], []), ["Doc"], []));
    }

    [Fact]
    public void Semantic_only_descriptor_and_nonnull_handler_are_supported()
    {
        var semantics = new SemanticModule("Units", [], [SemanticTypes.Angle], []);
        var descriptor = new ModuleDescriptor("Units", null, semantics, [], []);
        var signature = new OperationSignature("Units.Identity", SemanticTypes.Angle, SemanticTypes.Angle);
        Func<object?> handler = () => null;
        var binding = new HostOperationBinding(signature, handler);

        Assert.Same(semantics, descriptor.Semantics);
        Assert.Empty(descriptor.StartRules);
        Assert.Same(signature, binding.Signature);
        Assert.Same(handler, binding.Handler);
        Assert.Throws<ArgumentNullException>(() => new HostOperationBinding(signature, null!));
    }

    [Fact]
    public void Descriptor_rejects_null_or_blank_entries()
    {
        Assert.Throws<ArgumentException>(() => new ModuleDescriptor("Test", UsesModule.Instance, null, [" "], []));
        Assert.Throws<ArgumentException>(() => new ModuleDescriptor("Test", UsesModule.Instance, null, [], [null!]));
    }
}
