using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryModuleTests
{
    static ModuleComposition Compose() => ModuleComposer.Compose([BoxMeshModule.Descriptor],
        [new HostOperationBinding(BoxMeshModule.BoxSignature, (Func<object?>)(() => null))]);

    [Fact]
    public void Box_has_exact_composed_signature_and_valid_semantics()
    {
        var composition = Compose();
        Assert.Equal("Geometry.BoxMesh", BoxMeshModule.BoxSignature.Id);
        Assert.Equal(BoxMeshModule.MeshType, BoxMeshModule.BoxSignature.Result);
        Assert.Equal([SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar],
            BoxMeshModule.BoxSignature.Inputs);
        using var parsed = composition.Language.Parse("box 1 2 3;", composition.StartRules[("Geometry", "Document")]);
        Assert.True(parsed.Success);
        var project = new Project(composition.Language);
        project.Set("sample.geom", parsed.Tree);
        var file = new ProjectSemantics(project)["sample.geom"];
        Assert.Empty(file.Binding.Diagnostics);
        Assert.Empty(file.Diagnostics());
    }

    [Theory]
    [InlineData("box 0 2 3;", "0")]
    [InlineData("box -1 2 3;", "-1")]
    [InlineData("box 1e999 2 3;", "1e999")]
    [InlineData("box 1 2 0;", "0")]
    public void Invalid_dimensions_report_literal_spans(string source, string literal)
    {
        var composition = Compose();
        using var parsed = composition.Language.Parse(source, composition.StartRules[("Geometry", "Document")]);
        Assert.True(parsed.Success);
        var project = new Project(composition.Language);
        project.Set("sample.geom", parsed.Tree);
        var file = new ProjectSemantics(project)["sample.geom"];
        var diagnostic = Assert.Single(file.Diagnostics(), diagnostic => diagnostic.Code == "GE0001");
        Assert.Equal(literal, source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }

    [Fact]
    public void Malformed_dimension_does_not_parse_cleanly()
    {
        var composition = Compose();
        const string source = "box 1 nope 3;";
        using var parsed = composition.Language.Parse(source, composition.StartRules[("Geometry", "Document")]);
        Assert.False(parsed.Success);
        Assert.Contains(parsed.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Span.Start == source.IndexOf("nope", StringComparison.Ordinal));
    }

    [Fact]
    public void Geometry_types_and_lowers_through_declarative_rules()
    {
        var rules = Nitrogen.Geometry.Syntax.GeometryModule.Instance.DeclarativeRules.ToDictionary(rule => rule.LocalKind);
        var box = rules[Nitrogen.Geometry.Syntax.GeometryModule.LBox];
        Assert.Equal(DeclarativeForm.Operation, box.Form);
        Assert.Equal("Geometry.BoxMesh", box.Target);
        Assert.Equal([1, 2, 3], box.Arguments);
        var num = rules[Nitrogen.Geometry.Syntax.GeometryModule.LNum];
        Assert.Equal(DeclarativeForm.Literal, num.Form);
        Assert.Equal("Core.Scalar", num.Target);
        Assert.Equal([-1], num.Arguments);
        Assert.Equal(2, rules[Nitrogen.Geometry.Syntax.GeometryModule.LParameter].DeclaredTypeChild);
        var definition = rules[Nitrogen.Geometry.Syntax.GeometryModule.LDefinition];
        Assert.Equal(DeclarativeForm.Template, definition.Form);
        Assert.Equal(2, definition.SequenceStride);
        Assert.Equal(DeclarativeForm.Expand, rules[Nitrogen.Geometry.Syntax.GeometryModule.LMake].Form);
        Assert.Empty(BoxMeshModule.Descriptor.Semantics!.Lowerers); // no C# lowering left
    }
}
