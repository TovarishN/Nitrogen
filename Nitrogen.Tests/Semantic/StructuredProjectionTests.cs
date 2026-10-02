using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Structured;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class StructuredProjectionTests
{
    [Fact]
    public void Generic_projection_emits_ordered_domain_values_without_a_syntax_walker()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var makePart = new OperationSignature("Test.MakePart", part, SemanticTypes.Text);
        var makeRig = new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part));
        var language = new LanguageBuilder().Add(StructuredModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [part, rig], [makePart, makeRig])).Build();
        using var parsed = language.Parse("rig part left; part right;", StructuredModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("rig.structured", parsed.Tree);
        var file = new ProjectSemantics(project)["rig.structured"];
        var root = Assert.Single(HirLowering.Lower(file, language.SemanticCatalog).Roots);
        var registry = new ProjectionRegistry(language.SemanticCatalog,
        [
            new ProjectionHandler(makePart, args => new ProjectedValue(part, (string)args[0].Value)),
            new ProjectionHandler(makeRig, args => new ProjectedValue(rig,
                ((IReadOnlyList<ProjectedValue>)args[0].Value).Select(value => (string)value.Value).ToArray())),
        ]);

        var result = HirProjector.Project(root, registry);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["left", "right"], Assert.IsType<string[]>(result.Value!.Value));
    }

    [Fact]
    public void Missing_handler_reports_the_operation_source()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var makePart = new OperationSignature("Test.MakePart", part, SemanticTypes.Text);
        var makeRig = new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part));
        var language = new LanguageBuilder().Add(StructuredModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [part, rig], [makePart, makeRig])).Build();
        using var parsed = language.Parse("rig part left;", StructuredModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("rig.structured", parsed.Tree);
        var file = new ProjectSemantics(project)["rig.structured"];
        var root = Assert.Single(HirLowering.Lower(file, language.SemanticCatalog).Roots);
        var registry = new ProjectionRegistry(language.SemanticCatalog,
        [new ProjectionHandler(makeRig, _ => new ProjectedValue(rig, Array.Empty<string>()))]);

        var result = HirProjector.Project(root, registry);

        Assert.Null(result.Value);
        Assert.Equal("NP0002", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("rig.structured", result.Diagnostics[0].Origin.Path);
        Assert.Equal(4, result.Diagnostics[0].Origin.Span.Start);
    }

    [Fact]
    public void Preflight_finds_a_later_missing_handler_before_any_handler_runs()
    {
        var part = SemanticType.Named("Test", "Part");
        var rig = SemanticType.Named("Test", "Rig");
        var first = new OperationSignature("Test.First", part);
        var second = new OperationSignature("Test.Second", part);
        var makeRig = new OperationSignature("Test.MakeRig", rig, SemanticTypes.SequenceOf(part));
        var catalog = new LanguageBuilder().AddSemantic(
            new SemanticModule("Test", [], [part, rig], [first, second, makeRig])).Build().SemanticCatalog;
        var origin = new SourceOrigin("parts.test", Guid.NewGuid(), 0, new TextSpan(0, 1));
        var root = new HirOperation(makeRig,
        [
            new HirSequence(part,
            [
                new HirOperation(first, [], [origin]),
                new HirOperation(second, [], [origin]),
            ], origin),
        ], [origin]);
        int calls = 0;
        var registry = new ProjectionRegistry(catalog,
        [
            new ProjectionHandler(first, _ =>
            {
                calls++;
                return new ProjectedValue(part, "first");
            }),
            new ProjectionHandler(makeRig, _ => new ProjectedValue(rig, "rig")),
        ]);

        var result = HirProjector.Project(root, registry);

        Assert.Null(result.Value);
        Assert.Equal("NP0002", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, calls);
    }
}
