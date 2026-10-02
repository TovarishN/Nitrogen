using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class ProjectionNumericSafetyTests
{
    static readonly SourceOrigin Origin = new("numeric.test", Guid.NewGuid(), 0, new TextSpan(7, 4));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Nonfinite_operation_results_stop_before_downstream_handlers(float value)
    {
        var time = SemanticType.Named("Units", "Time");
        var source = new OperationSignature("Test.Source", time);
        var consume = new OperationSignature("Test.Consume", SemanticTypes.Scalar, time);
        var catalog = new LanguageBuilder().AddSemantic(new SemanticModule("Units", [], [time], []))
            .AddSemantic(new SemanticModule("Test", ["Units"], [], [source, consume])).Build().SemanticCatalog;
        int calls = 0;
        var registry = new ProjectionRegistry(catalog,
        [
            new ProjectionHandler(source, _ => new ProjectedValue(time, value)),
            new ProjectionHandler(consume, _ => { calls++; return new ProjectedValue(SemanticTypes.Scalar, 1f); }),
        ]);
        var root = new HirOperation(consume, [new HirOperation(source, [], [Origin])], [Origin with { Span = new TextSpan(0, 20) }]);
        var result = HirProjector.Project(root, registry);
        Assert.Null(result.Value);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("NP0001", error.Code);
        Assert.Equal(Origin, error.Origin);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Nonfinite_inputs_fail_preflight_before_any_handler_runs(float value)
    {
        using var parsed = FileBindingTests.Scopes.Parse("unit a { let input = 0; }", ScopesModule.File);
        var symbol = SemanticSymbol.From(Assert.Single(FileBinding.Bind("numeric.test", parsed.Tree).Declarations,
            symbol => symbol.Kind == "value"), "Test", SemanticTypes.Scalar);
        var consume = new OperationSignature("Test.Consume", SemanticTypes.Scalar, SemanticTypes.Scalar);
        var catalog = new LanguageBuilder().AddSemantic(new SemanticModule("Test", [], [], [consume])).Build().SemanticCatalog;
        int calls = 0;
        var registry = new ProjectionRegistry(catalog, [new ProjectionHandler(consume, _ =>
        { calls++; return new ProjectedValue(SemanticTypes.Scalar, 1f); })]);
        var root = new HirOperation(consume, [new HirSymbolRef(symbol, Origin)], [Origin]);
        var inputs = new Dictionary<Symbol, ProjectedValue> { [symbol.Binding] = new(SemanticTypes.Scalar, value) };
        Assert.Equal("NP0001", Assert.Single(HirProjector.Preflight(root, registry, inputs)).Code);
        Assert.Null(HirProjector.Project(root, registry, inputs).Value);
        Assert.Equal(0, calls);
    }
}
