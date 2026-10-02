using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class RepeatProjectionTests
{
    static readonly SourceOrigin Origin = new("repeat.test", Guid.NewGuid(), 0, new TextSpan(0, 1));
    static SemanticSymbol Iterator(string name)
    {
        using var parsed = FileBindingTests.Scopes.Parse("unit a { let " + name + " = 0; }", ScopesModule.File);
        var binding = FileBinding.Bind("repeat.test", parsed.Tree);
        return SemanticSymbol.From(Assert.Single(binding.Declarations, symbol => symbol.Kind == "value"), "Test", SemanticTypes.Scalar);
    }
    static HirRepeat Repeat(float count, SemanticSymbol iterator, params HirNode[] items) =>
        new(new HirConstant(count, SemanticTypes.Scalar, Origin), iterator,
            new HirSequence(SemanticTypes.Scalar, items, Origin), Origin);
    static ProjectionRegistry Registry() => new(new LanguageBuilder().Build().SemanticCatalog, []);

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2.9f, 2)]
    [InlineData(-2, 0)]
    [InlineData(0, 0)]
    public void Counted_projection_binds_numeric_indices_and_retains_iteration_groups(float count, int length)
    {
        var iterator = Iterator("i");
        var root = Repeat(count, iterator, new HirSymbolRef(iterator, Origin));
        var result = HirProjector.Project(root, Registry());
        Assert.Empty(result.Diagnostics);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<ProjectedValue>>(result.Value!.Value);
        Assert.Equal(length, groups.Count);
        for (int i = 0; i < length; i++)
        {
            var value = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ProjectedValue>>(groups[i].Value));
            Assert.Equal((float)i, value.Value);
            Assert.Equal(Origin, Assert.Single(value.Origins));
        }
    }

    [Fact]
    public void Bound_iterator_does_not_escape_its_template()
    {
        var iterator = Iterator("i");
        var repeat = Repeat(1, iterator, new HirSymbolRef(iterator, Origin));
        var signature = new OperationSignature("Test.Use", SemanticTypes.Scalar, repeat.Type, SemanticTypes.Scalar);
        var catalog = new LanguageBuilder().AddSemantic(new SemanticModule("Test", [], [], [signature])).Build().SemanticCatalog;
        int calls = 0;
        var registry = new ProjectionRegistry(catalog, [new ProjectionHandler(signature, _ =>
        { calls++; return new ProjectedValue(SemanticTypes.Scalar, 0f); })]);
        var result = HirProjector.Project(new HirOperation(signature, [repeat, new HirSymbolRef(iterator, Origin)], [Origin]), registry);
        Assert.Equal("NP0003", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Zero_count_still_preflights_template_handlers()
    {
        var signature = new OperationSignature("Test.Missing", SemanticTypes.Scalar);
        var catalog = new LanguageBuilder().AddSemantic(new SemanticModule("Test", [], [], [signature])).Build().SemanticCatalog;
        var root = Repeat(0, Iterator("i"), new HirOperation(signature, [], [Origin]));
        Assert.Equal("NP0002", Assert.Single(HirProjector.Project(root, new ProjectionRegistry(catalog, [])).Diagnostics).Code);
    }

    [Fact]
    public void Traversal_and_rewrite_visit_the_count_and_template()
    {
        var root = Repeat(1, Iterator("i"), new HirConstant(10, SemanticTypes.Scalar, Origin));
        Assert.Equal(4, HirTraversal.PreOrder(root).Count());
        var rewritten = Assert.IsType<HirRepeat>(HirTraversal.Rewrite(root, node =>
            node is HirConstant { Value: 10 } ? new HirConstant(20, SemanticTypes.Scalar, Origin) : node));
        Assert.Equal(20f, Assert.IsType<HirConstant>(rewritten.Template.Items[0]).Value);
    }

    [Fact]
    public void Nested_count_can_use_an_outer_iterator_without_leaking_inner_bindings()
    {
        var outer = Iterator("i");
        var inner = Iterator("j");
        var nested = new HirRepeat(new HirSymbolRef(outer, Origin), inner,
            new HirSequence(SemanticTypes.Scalar, [new HirSymbolRef(inner, Origin), new HirSymbolRef(outer, Origin)], Origin), Origin);
        var root = new HirRepeat(new HirConstant(3, SemanticTypes.Scalar, Origin), outer,
            new HirSequence(nested.Type, [nested], Origin), Origin);
        var projected = HirProjector.Project(root, Registry());
        Assert.Empty(projected.Diagnostics);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<ProjectedValue>>(projected.Value!.Value);
        for (int i = 0; i < 3; i++)
        {
            var repeated = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ProjectedValue>>(groups[i].Value));
            var innerGroups = Assert.IsAssignableFrom<IReadOnlyList<ProjectedValue>>(repeated.Value);
            Assert.Equal(i, innerGroups.Count);
            for (int j = 0; j < i; j++)
                Assert.Equal(new[] { (float)j, (float)i }, ((IReadOnlyList<ProjectedValue>)innerGroups[j].Value).Select(value => (float)value.Value));
        }
    }

    [Theory]
    [InlineData(float.NaN, "NP0001")]
    [InlineData(float.PositiveInfinity, "NP0001")]
    [InlineData(2147483648f, "NP0007")]
    public void Invalid_counts_fail_preflight_at_the_count_origin(float count, string code)
    {
        var root = Repeat(count, Iterator("i"));
        var result = HirProjector.Project(root, Registry());
        Assert.Null(result.Value);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(code, error.Code);
        Assert.Equal(Origin, error.Origin);
    }
}
