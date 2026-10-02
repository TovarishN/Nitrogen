using Nitrogen.Binding;
using Nitrogen.Semantics;
using Nitrogen.Semantic;
using Nitrogen.Tests.InitializedRefs;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class ReferenceInitializerTests
{
    static readonly OperationSignature Emit = new("Init.Emit", SemanticTypes.Scalar, SemanticTypes.Scalar);
    static readonly OperationSignature Add = new("Init.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    static readonly Language Language = new LanguageBuilder().Add(InitializedRefsModule.Instance)
        .AddSemantic(new SemanticModule("Init", ["Core"], [], [Emit, Add])).Build();
    static readonly ProjectionRegistry Registry = new(Language.SemanticCatalog,
    [
        new(Emit, arguments => new ProjectedValue(SemanticTypes.Scalar, arguments[0].Value)),
        new(Add, arguments => new ProjectedValue(SemanticTypes.Scalar, (float)arguments[0].Value + (float)arguments[1].Value)),
    ]);

    [Theory]
    [InlineData("let a = 40; emit a + 2", 42f)]
    [InlineData("let a = 40; let b = a + 1; emit b + 1", 42f)]
    public void Bound_initializers_lower_and_project_without_external_inputs(string source, float expected)
    {
        using var parsed = Language.Parse(source, InitializedRefsModule.File);
        Assert.True(parsed.Success);
        var project = new Project(Language);
        project.Set("initializer.init", parsed.Tree);
        var file = new ProjectSemantics(project)["initializer.init"];
        Assert.Empty(file.Diagnostics());
        int root = Enumerable.Range(0, file.Tree.NodeCount).Single(node => file.Tree.Kind(node) == InitializedRefsKinds.File);
        var hir = Assert.IsType<HirOperation>(HirLowering.LowerNested(new LoweringContext(file, Guid.NewGuid()), root));

        Assert.DoesNotContain(HirTraversal.PreOrder(hir), node => node is HirSymbolRef);
        var result = HirProjector.Project(hir, Registry);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected, result.Value!.Value);
        var literal = Assert.Single(HirTraversal.PreOrder(hir).OfType<HirConstant>(), node => node.Value == 40f);
        Assert.Equal(source.IndexOf("40", StringComparison.Ordinal), literal.Origins[0].Span.Start);
    }

    [Fact]
    public void An_uninitialized_symbol_still_requires_a_supplied_input()
    {
        const string source = "input a; emit a";
        using var parsed = Language.Parse(source, InitializedRefsModule.File);
        var project = new Project(Language);
        project.Set("external.init", parsed.Tree);
        var file = new ProjectSemantics(project)["external.init"];
        int root = Enumerable.Range(0, file.Tree.NodeCount).Single(node => file.Tree.Kind(node) == InitializedRefsKinds.File);
        var hir = Assert.IsType<HirOperation>(HirLowering.LowerNested(new LoweringContext(file, Guid.NewGuid()), root));
        var reference = Assert.IsType<HirSymbolRef>(Assert.Single(hir.Arguments));
        Assert.Equal("NP0003", Assert.Single(HirProjector.Preflight(hir, Registry)).Code);
        var result = HirProjector.Project(hir, Registry, new Dictionary<Symbol, ProjectedValue>
        {
            [reference.Symbol.Binding] = new(SemanticTypes.Scalar, 42f),
        });
        Assert.Empty(result.Diagnostics);
        Assert.Equal(42f, result.Value!.Value);
    }

    [Theory]
    [InlineData("let a = a; emit a")]
    [InlineData("let a = b; let b = a; emit a")]
    public void Cyclic_initializers_fail_with_a_source_linked_diagnostic(string source)
    {
        using var parsed = Language.Parse(source, InitializedRefsModule.File);
        var project = new Project(Language);
        project.Set("cycle.init", parsed.Tree);
        var file = new ProjectSemantics(project)["cycle.init"];
        var result = HirLowering.Lower(file, Language.SemanticCatalog);
        Assert.Empty(result.Roots);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("NH0005", error.Code);
        Assert.Equal("cycle.init", error.Origin.Path);
    }
}
