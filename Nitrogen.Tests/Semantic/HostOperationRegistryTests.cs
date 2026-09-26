using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class HostOperationRegistryTests
{
    static readonly OperationSignature Signature = new("Test.Add", SemanticTypes.Angle,
        SemanticTypes.Scalar, SemanticTypes.Angle);
    static readonly SemanticCatalog Catalog = SemanticCatalog.Compose(
        [new SemanticModule("Test", [], [], [Signature])], out _)!;

    [Fact]
    public void Exact_typed_binding_is_frozen_without_calling_handler()
    {
        var calls = 0;
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> handler = values =>
        {
            calls++;
            return new(SemanticTypes.Angle, values[0].Number + values[1].Number);
        };
        var binding = new HostOperationBinding(Signature, handler);

        Assert.True(HostOperationRegistry.TryBind(Catalog, [binding, binding], out var registry, out var errors));
        Assert.Empty(errors);
        Assert.NotNull(registry);
        Assert.Same(Catalog, registry.Catalog);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Missing_export_reordered_inputs_changed_result_and_wrong_delegate_are_rejected()
    {
        var handler = (Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)(_ => new(SemanticTypes.Angle, 1));
        var absent = new HostOperationBinding(new OperationSignature("Test.Absent", SemanticTypes.Angle), handler);
        var reversed = new HostOperationBinding(new OperationSignature(Signature.Id, SemanticTypes.Angle,
            SemanticTypes.Angle, SemanticTypes.Scalar), handler);
        var result = new HostOperationBinding(new OperationSignature(Signature.Id, SemanticTypes.Scalar,
            SemanticTypes.Scalar, SemanticTypes.Angle), handler);
        var wrongShape = new HostOperationBinding(Signature, (Func<object?>)(() => null));

        AssertError(absent, "NR0001");
        AssertError(reversed, "NR0002");
        AssertError(result, "NR0002");
        AssertError(wrongShape, "NR0003");
    }

    [Fact]
    public void Duplicate_handler_instances_are_rejected_but_same_instance_coalesces()
    {
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> one = _ => new(SemanticTypes.Angle, 1);
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> two = _ => new(SemanticTypes.Angle, 2);
        var first = new HostOperationBinding(Signature, one);
        var second = new HostOperationBinding(new OperationSignature(Signature.Id, SemanticTypes.Angle,
            SemanticTypes.Scalar, SemanticTypes.Angle), two);
        var equivalent = new HostOperationBinding(new OperationSignature(Signature.Id, SemanticTypes.Angle,
            SemanticTypes.Scalar, SemanticTypes.Angle), one);

        AssertError([first, second], "NR0004");
        Assert.True(HostOperationRegistry.TryBind(Catalog, [first, equivalent], out _, out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void Diagnostic_order_is_stable_and_throwing_bind_preserves_diagnostics()
    {
        var bad = new HostOperationBinding(Signature, (Func<object?>)(() => null));
        var absent = new HostOperationBinding(new OperationSignature("Test.Absent", SemanticTypes.Angle),
            (Func<object?>)(() => null));
        Assert.False(HostOperationRegistry.TryBind(Catalog, [bad, absent], out var registry, out var forward));
        Assert.Null(registry);
        Assert.False(HostOperationRegistry.TryBind(Catalog, [absent, bad], out _, out var reverse));
        Assert.Equal(forward.Select(d => (d.Code, d.Message)), reverse.Select(d => (d.Code, d.Message)));
        var thrown = Assert.Throws<SemanticCompositionException>(() => HostOperationRegistry.Bind(Catalog, [bad, absent]));
        Assert.Equal(forward.Select(d => (d.Code, d.Message)),
            thrown.Diagnostics.Select(d => (d.Code, d.Message)));
    }

    static void AssertError(HostOperationBinding binding, string code) => AssertError([binding], code);

    static void AssertError(IEnumerable<HostOperationBinding> bindings, string code)
    {
        Assert.False(HostOperationRegistry.TryBind(Catalog, bindings, out var registry, out var errors));
        Assert.Null(registry);
        Assert.Contains(errors, error => error.Code == code);
    }
}
