using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

public sealed class HirPreflightTests
{
    static readonly SourceOrigin Outer = new("a.skill", Guid.NewGuid(), 1, new TextSpan(1, 4));
    static readonly SourceOrigin Inner = new("a.skill", Outer.SnapshotId, 2, new TextSpan(6, 4));
    static readonly OperationSignature Identity = new("Test.Identity", SemanticTypes.Angle, SemanticTypes.Angle);
    static readonly OperationSignature InnerOp = new("Test.Inner", SemanticTypes.Angle, SemanticTypes.Angle);
    static readonly SemanticCatalog Catalog = SemanticCatalog.Compose(
        [new SemanticModule("Test", [], [], [Identity, InnerOp])], out _)!;

    [Fact]
    public void Missing_nested_handler_prevents_outer_handler_invocation()
    {
        var calls = 0;
        var registry = Registry(Identity, _ => { calls++; return new(SemanticTypes.Angle, 1); });
        var root = new HirOperation(Identity,
            [new HirOperation(InnerOp, [new HirConstant(1, SemanticTypes.Angle, Inner)], [Inner])], [Outer]);

        var errors = HirPreflight.Check(root, registry, new Dictionary<Symbol, ExecutionValue>());

        Assert.Contains(errors, error => error.Code == "NE0005" && error.Origin == Inner);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Catalog_mismatch_nonfinite_constant_and_unknown_node_have_precise_origins()
    {
        var registry = Registry(Identity, _ => new(SemanticTypes.Angle, 1));
        var mismatch = new OperationSignature(Identity.Id, SemanticTypes.Angle,
            SemanticTypes.Angle, SemanticTypes.Angle);
        var root = new HirOperation(mismatch,
            [new HirConstant(float.NaN, SemanticTypes.Angle, Inner), new UnknownNode(Inner)], [Outer]);

        var errors = HirPreflight.Check(root, registry, new Dictionary<Symbol, ExecutionValue>());

        Assert.Contains(errors, error => error.Code == "NE0004" && error.Origin == Outer);
        Assert.Equal(2, errors.Count(error => error.Code == "NE0001" && error.Origin == Inner));
    }

    [Fact]
    public void Symbol_requires_exact_binding_object_and_type()
    {
        var first = MotionReference();
        var second = MotionReference();
        Assert.Equal(first.Symbol.Binding.Name, second.Symbol.Binding.Name);
        Assert.NotSame(first.Symbol.Binding, second.Symbol.Binding);
        var registry = HostOperationRegistry.Bind(NitrogenMotionParser.Language.SemanticCatalog, []);

        var missing = HirPreflight.Check(second, registry, new Dictionary<Symbol, ExecutionValue>
        {
            [first.Symbol.Binding] = new(SemanticTypes.Scalar, 0.5f)
        });
        var wrong = HirPreflight.Check(second, registry, new Dictionary<Symbol, ExecutionValue>
        {
            [second.Symbol.Binding] = new(SemanticTypes.Angle, 0.5f)
        });
        var valid = HirPreflight.Check(second, registry, new Dictionary<Symbol, ExecutionValue>
        {
            [second.Symbol.Binding] = new(SemanticTypes.Scalar, 0.5f)
        });

        Assert.Contains(missing, error => error.Code == "NE0002" && error.Origin == second.Origins[0]);
        Assert.Contains(wrong, error => error.Code == "NE0003" && error.Origin == second.Origins[0]);
        Assert.Empty(valid);
    }

    [Fact]
    public void Custom_name_comparer_cannot_substitute_a_symbol_from_another_parse()
    {
        var first = MotionReference();
        var second = MotionReference();
        var inputs = new Dictionary<Symbol, ExecutionValue>(new SymbolNameComparer())
        {
            [first.Symbol.Binding] = new(SemanticTypes.Scalar, 0.5f)
        };
        var registry = HostOperationRegistry.Bind(NitrogenMotionParser.Language.SemanticCatalog, []);

        var preflight = HirPreflight.Check(second, registry, inputs);
        var evaluation = HirEvaluator.Evaluate(second, registry, inputs);

        Assert.Equal("NE0002", Assert.Single(preflight).Code);
        Assert.Equal(second.Origins[0], preflight[0].Origin);
        Assert.Null(evaluation.Value);
        Assert.Equal("NE0002", Assert.Single(evaluation.Diagnostics).Code);
    }

    [Fact]
    public void Valid_constant_and_bound_operation_have_no_errors()
    {
        var registry = Registry(Identity, values => values[0]);
        var root = new HirOperation(Identity, [new HirConstant(0.2f, SemanticTypes.Angle, Inner)], [Outer]);
        Assert.Empty(HirPreflight.Check(root, registry, new Dictionary<Symbol, ExecutionValue>()));
    }

    static HostOperationRegistry Registry(OperationSignature signature,
        Func<IReadOnlyList<ExecutionValue>, ExecutionValue> handler) =>
        HostOperationRegistry.Bind(Catalog, [new HostOperationBinding(signature, handler)]);

    static HirSymbolRef MotionReference()
    {
        var text = MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"));
        using var parsed = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
        var project = new Project(NitrogenMotionParser.Language);
        project.Set("a.skill", parsed.Tree);
        var result = HirLowering.Lower(new ProjectSemantics(project)["a.skill"],
            NitrogenMotionParser.Language.SemanticCatalog);
        Assert.Empty(result.Diagnostics);
        return Assert.IsType<HirSymbolRef>(Assert.Single(result.Roots.SelectMany(HirTraversal.PreOrder),
            node => node is HirSymbolRef));
    }

    sealed class UnknownNode(SourceOrigin origin) : HirNode(SemanticTypes.Angle, [origin]);

    sealed class SymbolNameComparer : IEqualityComparer<Symbol>
    {
        public bool Equals(Symbol? x, Symbol? y) => StringComparer.Ordinal.Equals(x?.Name, y?.Name);
        public int GetHashCode(Symbol obj) => StringComparer.Ordinal.GetHashCode(obj.Name);
    }
}
