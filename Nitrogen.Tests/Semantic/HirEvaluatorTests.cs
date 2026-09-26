using Nitrogen.Binding;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class HirEvaluatorTests
{
    static readonly SourceOrigin RootOrigin = new("a.skill", Guid.NewGuid(), 1, new TextSpan(1, 3));
    static readonly SourceOrigin ChildOrigin = new("a.skill", RootOrigin.SnapshotId, 2, new TextSpan(5, 3));
    static readonly OperationSignature Identity = new("Test.Identity", SemanticTypes.Angle, SemanticTypes.Angle);
    static readonly OperationSignature Add = new("Test.Add", SemanticTypes.Angle,
        SemanticTypes.Angle, SemanticTypes.Angle);
    static readonly SemanticCatalog Catalog = SemanticCatalog.Compose(
        [new SemanticModule("Test", [], [], [Identity, Add])], out _)!;
    static readonly IReadOnlyDictionary<Symbol, ExecutionValue> NoInputs = new Dictionary<Symbol, ExecutionValue>();

    [Fact]
    public void Constant_returns_typed_value_and_root_origin()
    {
        var result = HirEvaluator.Evaluate(new HirConstant(0.25f, SemanticTypes.Angle, RootOrigin), Registry(), NoInputs);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(SemanticTypes.Angle, result.Value!.Type);
        Assert.Equal(0.25f, result.Value.Number);
        Assert.Equal([RootOrigin], result.Origins);
    }

    [Fact]
    public void Nested_operations_keep_argument_order_and_offer_read_only_arguments()
    {
        var calls = new List<string>();
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> identity = values =>
        {
            calls.Add("identity");
            Assert.Throws<NotSupportedException>(() => ((IList<ExecutionValue>)values)[0] = values[0]);
            return values[0];
        };
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> add = values =>
        {
            calls.Add("add");
            Assert.Equal(0.2f, values[0].Number);
            Assert.Equal(0.3f, values[1].Number);
            return new(SemanticTypes.Angle, values[0].Number + values[1].Number);
        };
        var root = new HirOperation(Add,
            [new HirOperation(Identity, [new HirConstant(0.2f, SemanticTypes.Angle, ChildOrigin)], [ChildOrigin]),
                new HirConstant(0.3f, SemanticTypes.Angle, ChildOrigin)], [RootOrigin]);

        var result = HirEvaluator.Evaluate(root, Registry((Identity, identity), (Add, add)), NoInputs);

        Assert.Empty(result.Diagnostics);
        Assert.InRange(result.Value!.Number, 0.499999f, 0.500001f);
        Assert.Equal(["identity", "add"], calls);
    }

    [Fact]
    public void Preflight_failure_stops_all_handlers_even_when_nested()
    {
        var calls = 0;
        var root = new HirOperation(Add,
            [new HirOperation(Identity, [new HirConstant(0.2f, SemanticTypes.Angle, ChildOrigin)], [ChildOrigin]),
                new HirConstant(0.3f, SemanticTypes.Angle, ChildOrigin)], [RootOrigin]);
        var registry = Registry((Add, _ => { calls++; return new(SemanticTypes.Angle, 1); }));

        var result = HirEvaluator.Evaluate(root, registry, NoInputs);

        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, error => error.Code == "NE0005" && error.Origin == ChildOrigin);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Rewritten_root_keeps_all_origins()
    {
        var root = new HirOperation(Identity, [new HirConstant(0.2f, SemanticTypes.Angle, ChildOrigin)], [RootOrigin]);
        var rewritten = HirTraversal.Rewrite(root, node => node is HirConstant
            ? new HirConstant(0.4f, SemanticTypes.Angle, ChildOrigin)
            : node);

        var result = HirEvaluator.Evaluate(rewritten, Registry((Identity, values => values[0])), NoInputs);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(0.4f, result.Value!.Number);
        Assert.Equal(rewritten.Origins, result.Origins);
        Assert.Contains(RootOrigin, result.Origins);
        Assert.Contains(ChildOrigin, result.Origins);
    }

    [Fact]
    public void Handler_exceptions_null_and_wrong_type_become_source_diagnostics()
    {
        var root = new HirOperation(Identity, [new HirConstant(0.2f, SemanticTypes.Angle, ChildOrigin)], [RootOrigin]);
        var thrown = HirEvaluator.Evaluate(root, Registry((Identity, _ => throw new InvalidOperationException("broken"))), NoInputs);
        var nonfinite = HirEvaluator.Evaluate(root,
            Registry((Identity, _ => new ExecutionValue(SemanticTypes.Angle, float.PositiveInfinity))), NoInputs);
        var nullValue = HirEvaluator.Evaluate(root, Registry((Identity, _ => null!)), NoInputs);
        var wrongType = HirEvaluator.Evaluate(root, Registry((Identity, _ => new(SemanticTypes.Scalar, 1))), NoInputs);

        Assert.Equal(("NE0006", RootOrigin), (Assert.Single(thrown.Diagnostics).Code, thrown.Diagnostics[0].Origin));
        Assert.Equal(("NE0006", RootOrigin), (Assert.Single(nonfinite.Diagnostics).Code, nonfinite.Diagnostics[0].Origin));
        Assert.Equal(("NE0007", RootOrigin), (Assert.Single(nullValue.Diagnostics).Code, nullValue.Diagnostics[0].Origin));
        Assert.Equal(("NE0007", RootOrigin), (Assert.Single(wrongType.Diagnostics).Code, wrongType.Diagnostics[0].Origin));
        Assert.All([thrown, nonfinite, nullValue, wrongType], result => Assert.Null(result.Value));
    }

    [Fact]
    public void Failed_child_prevents_parent_handler()
    {
        var parentCalls = 0;
        var root = new HirOperation(Add,
            [new HirOperation(Identity, [new HirConstant(1, SemanticTypes.Angle, ChildOrigin)], [ChildOrigin]),
                new HirConstant(2, SemanticTypes.Angle, ChildOrigin)], [RootOrigin]);
        var result = HirEvaluator.Evaluate(root, Registry(
            (Identity, _ => throw new InvalidOperationException("child")),
            (Add, _ => { parentCalls++; return new(SemanticTypes.Angle, 3); })), NoInputs);

        Assert.Equal(0, parentCalls);
        Assert.Equal(("NE0006", ChildOrigin), (Assert.Single(result.Diagnostics).Code, result.Diagnostics[0].Origin));
    }

    static HostOperationRegistry Registry(params (OperationSignature Signature,
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> Handler)[] bindings) =>
        HostOperationRegistry.Bind(Catalog, bindings.Select(binding =>
            new HostOperationBinding(binding.Signature, binding.Handler)));
}
