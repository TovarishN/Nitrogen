using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeferredProjectionTests
{
    static readonly SourceOrigin Origin = new("deferred.test", Guid.NewGuid(), 0, new TextSpan(5, 2));
    static readonly OperationSignature Select = new("Test.Select", SemanticTypes.Scalar,
        SemanticTypes.Bool, SemanticTypes.Scalar, SemanticTypes.Scalar);
    static readonly OperationSignature Source = new("Test.Source", SemanticTypes.Scalar);

    [Theory]
    [InlineData(1f, 7f)]
    [InlineData(0f, 9f)]
    public void Deferred_handler_evaluates_only_the_selected_argument(float condition, float expected)
    {
        var catalog = Catalog();
        int calls = 0;
        var registry = new ProjectionRegistry(catalog,
        [
            ProjectionHandler.Deferred(Select, get => get((float)get(0)!.Value > 0 ? 1 : 2)),
            new ProjectionHandler(Source, _ => { calls++; return new(SemanticTypes.Scalar, 7f); }),
        ]);
        var result = HirProjector.Project(Root(condition), registry);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected, result.Value!.Value);
        Assert.Equal(condition > 0 ? 1 : 0, calls);
    }

    [Fact]
    public void Repeated_argument_requests_evaluate_once_and_expired_access_is_rejected()
    {
        int calls = 0;
        Func<int, ProjectedValue?>? saved = null;
        var registry = new ProjectionRegistry(Catalog(),
        [
            ProjectionHandler.Deferred(Select, get => { saved = get; get(1); return get(1); }),
            new ProjectionHandler(Source, _ => { calls++; return new(SemanticTypes.Scalar, 7f); }),
        ]);
        Assert.Empty(HirProjector.Project(Root(1), registry).Diagnostics);
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => saved!(1));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void Nonfinite_computed_branch_fails_only_when_requested(float condition)
    {
        var registry = new ProjectionRegistry(Catalog(),
        [
            ProjectionHandler.Deferred(Select, get => get((float)get(0)!.Value > 0 ? 1 : 2)),
            new ProjectionHandler(Source, _ => new(SemanticTypes.Scalar, float.PositiveInfinity)),
        ]);
        var result = HirProjector.Project(Root(condition), registry);
        if (condition == 0) { Assert.Empty(result.Diagnostics); Assert.Equal(9f, result.Value!.Value); }
        else { Assert.Null(result.Value); Assert.Equal("NP0001", Assert.Single(result.Diagnostics).Code); }
    }

    [Fact]
    public void Preflight_checks_unselected_branches_before_the_deferred_handler_runs()
    {
        int calls = 0;
        var registry = new ProjectionRegistry(Catalog(),
        [ProjectionHandler.Deferred(Select, get => { calls++; return get(2); })]);
        var result = HirProjector.Project(Root(0), registry);
        Assert.Null(result.Value);
        Assert.Equal("NP0002", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Handler_cannot_hide_a_failed_argument_evaluation()
    {
        var registry = new ProjectionRegistry(Catalog(),
        [
            ProjectionHandler.Deferred(Select, get => { get(1); return new(SemanticTypes.Scalar, 3f); }),
            new ProjectionHandler(Source, _ => new(SemanticTypes.Scalar, float.NaN)),
        ]);
        var result = HirProjector.Project(Root(1), registry);
        Assert.Null(result.Value);
        Assert.Equal("NP0001", Assert.Single(result.Diagnostics).Code);
    }

    static SemanticCatalog Catalog() => new LanguageBuilder()
        .AddSemantic(new SemanticModule("Test", [], [], [Select, Source])).Build().SemanticCatalog;

    [Theory]
    [InlineData(false, float.NaN, "NP0001")]
    [InlineData(false, float.PositiveInfinity, "NP0001")]
    [InlineData(true, 1f, "NP0004")]
    public void Deferred_handler_results_keep_the_same_type_and_numeric_checks(bool wrongType, float value, string code)
    {
        var registry = new ProjectionRegistry(Catalog(),
        [
            ProjectionHandler.Deferred(Select, _ => new(wrongType ? SemanticTypes.Bool : SemanticTypes.Scalar, value)),
            new ProjectionHandler(Source, _ => new(SemanticTypes.Scalar, 7f)),
        ]);
        var result = HirProjector.Project(Root(1), registry);
        Assert.Null(result.Value);
        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Failure_remains_the_argument_diagnostic_if_handler_dereferences_it()
    {
        var registry = new ProjectionRegistry(Catalog(),
        [
            ProjectionHandler.Deferred(Select, get => new(SemanticTypes.Scalar, (float)get(1)!.Value)),
            new ProjectionHandler(Source, _ => new(SemanticTypes.Scalar, float.NaN)),
        ]);
        var result = HirProjector.Project(Root(1), registry);
        Assert.Null(result.Value);
        Assert.Equal("NP0001", Assert.Single(result.Diagnostics).Code);
    }

    static HirNode Root(float condition) => new HirOperation(Select,
        [new HirConstant(condition, SemanticTypes.Bool, Origin), new HirOperation(Source, [], [Origin]),
         new HirConstant(9f, SemanticTypes.Scalar, Origin)], [Origin]);
}
