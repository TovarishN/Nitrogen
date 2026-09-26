using Gravity.RagdollEditor;
using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryDefinitionSyntaxTests
{
    const string Definition = "def crate(width: Scalar, height: Scalar, depth: Scalar) = box width height depth;";
    const string Calls = "make crate(1, 2, 3); make crate(3, 2, 1);";

    static ModuleComposition Compose() => ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);

    [Fact]
    public void Definition_exports_one_shape_and_calls_bind_across_files()
    {
        var composed = Compose();
        using var definition = composed.Language.Parse(Definition, composed.StartRules[("Geometry", "Document")]);
        using var scene = composed.Language.Parse(Calls, composed.StartRules[("Geometry", "Document")]);
        Assert.True(definition.Success);
        Assert.True(scene.Success);
        var project = new Project(composed.Language);
        project.Set("definitions.geom", definition.Tree);
        project.Set("scene.geom", scene.Tree);
        Assert.Empty(project.Diagnostics("definitions.geom"));
        Assert.Empty(project.Diagnostics("scene.geom"));
        Assert.Single(project["definitions.geom"].Declarations, symbol => symbol.Kind == "shape" && symbol.IsExported);
        Assert.Equal(3, project["definitions.geom"].Declarations.Count(symbol => symbol.Kind == "parameter"));
        var references = project["scene.geom"].References.Where(reference => reference.Kinds.Contains("shape")).ToArray();
        Assert.Equal(2, references.Length);
        Assert.Same(Assert.Single(project.Resolve(references[0])), Assert.Single(project.Resolve(references[1])));
        Assert.Empty(new ProjectSemantics(project)["definitions.geom"].Diagnostics());
    }

    [Fact]
    public void Duplicate_parameters_and_exports_are_binding_errors()
    {
        var composed = Compose();
        const string duplicateParameters = "def crate(width: Scalar, width: Scalar, depth: Scalar) = box width width depth;";
        using var first = composed.Language.Parse(duplicateParameters, composed.StartRules[("Geometry", "Document")]);
        using var second = composed.Language.Parse(Definition, composed.StartRules[("Geometry", "Document")]);
        Assert.True(first.Success);
        Assert.True(second.Success);
        var project = new Project(composed.Language);
        project.Set("first.geom", first.Tree);
        Assert.Contains(project.Diagnostics("first.geom"), diagnostic => diagnostic.Code == "NB0002");
        project.Set("second.geom", second.Tree);
        Assert.Contains(project.Diagnostics("first.geom"), diagnostic => diagnostic.Code == "NB0003");
        Assert.Contains(project.Diagnostics("second.geom"), diagnostic => diagnostic.Code == "NB0003");
    }

    [Fact]
    public void Unresolved_call_is_a_binding_error()
    {
        var composed = Compose();
        using var parsed = composed.Language.Parse("make absent(1,2,3);", composed.StartRules[("Geometry", "Document")]);
        Assert.True(parsed.Success);
        var project = new Project(composed.Language);
        project.Set("scene.geom", parsed.Tree);
        Assert.Contains(project.Diagnostics("scene.geom"), diagnostic => diagnostic.Code == "NB0001");
    }

    [Theory]
    [InlineData("make crate(0,2,3);", "0")]
    [InlineData("make crate(-1,2,3);", "-1")]
    [InlineData("make crate(1e999,2,3);", "1e999")]
    [InlineData("def crate(width: Scalar, height: Scalar, depth: Scalar) = box width 0 depth;", "0")]
    public void Invalid_numeric_dimensions_are_at_the_literal(string source, string literal)
    {
        var composed = Compose();
        using var parsed = composed.Language.Parse(source, composed.StartRules[("Geometry", "Document")]);
        Assert.True(parsed.Success);
        var project = new Project(composed.Language);
        project.Set("sample.geom", parsed.Tree);
        var diagnostic = Assert.Single(new ProjectSemantics(project)["sample.geom"].Diagnostics(),
            diagnostic => diagnostic.Code == "GE0001");
        Assert.Equal(literal, source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }

    [Fact]
    public void Invalid_annotation_and_call_arity_are_semantic_diagnostics()
    {
        var composed = Compose();
        const string source = "def crate(width: Mesh, height: Scalar, depth: Scalar) = box width height depth; make crate(1,2);";
        using var parsed = composed.Language.Parse(source, composed.StartRules[("Geometry", "Document")]);
        Assert.True(parsed.Success);
        var project = new Project(composed.Language);
        project.Set("sample.geom", parsed.Tree);
        var diagnostics = new ProjectSemantics(project)["sample.geom"].Diagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GD0001" &&
            source.Substring(diagnostic.Span.Start, diagnostic.Span.Length) == "Mesh");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GD0003" &&
            source.Substring(diagnostic.Span.Start, diagnostic.Span.Length) == "crate");
    }
}
