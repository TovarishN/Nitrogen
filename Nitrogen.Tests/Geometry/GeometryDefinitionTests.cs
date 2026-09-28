using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryDefinitionTests
{
    static ModuleComposition Compose() => ModuleComposer.Compose([BoxMeshModule.Descriptor],
        [new HostOperationBinding(BoxMeshModule.BoxSignature, (Func<object?>)(() => null))]);

    [Fact]
    public void Call_in_another_file_expands_to_the_box_operation()
    {
        var composition = Compose();
        var start = composition.StartRules[("Geometry", "Document")];
        using var definitions = composition.Language.Parse("def cube(w: Scalar, h: Scalar, d: Scalar) = box w h d;", start);
        using var call = composition.Language.Parse("make cube(1, 2, 3);", start);
        Assert.True(definitions.Success);
        Assert.True(call.Success);
        var project = new Project(composition.Language);
        project.Set("defs.geom", definitions.Tree);
        project.Set("use.geom", call.Tree);
        var semantics = new ProjectSemantics(project);
        Assert.Empty(semantics["defs.geom"].Diagnostics());
        var lowered = HirLowering.Lower(semantics["use.geom"], composition.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal(BoxMeshModule.BoxSignature, root.Signature);
        Assert.Equal([1f, 2f, 3f], root.Arguments.Select(argument => Assert.IsType<HirConstant>(argument).Value));
        Assert.All(root.Arguments, argument => Assert.Equal(SemanticTypes.Scalar, argument.Type));
    }

    [Fact]
    public void Mesh_parameter_is_GD0001()
    {
        var composition = Compose();
        using var parsed = composition.Language.Parse("def bad(w: Mesh, h: Scalar, d: Scalar) = box 1 h d;",
            composition.StartRules[("Geometry", "Document")]);
        var project = new Project(composition.Language);
        project.Set("defs.geom", parsed.Tree);
        Assert.Contains(new ProjectSemantics(project)["defs.geom"].Diagnostics(), diagnostic => diagnostic.Code == "GD0001");
    }

    [Fact]
    public void Hover_shows_mesh_and_scalar_types()
    {
        var composition = Compose();
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("geometry", composition.Language,
            new Dictionary<string, Rule> { [".geom"] = composition.StartRules[("Geometry", "Document")] }));
        using var service = new NitrogenLanguageService(registry);
        service.Open("file:///w/a.geom", 1, "box 1 2 3;");
        Assert.Contains("`Geometry.Mesh`", service.Hover("file:///w/a.geom", new DocumentPosition(0, 1))!.Markdown);
        Assert.Contains("`Core.Scalar` = 1", service.Hover("file:///w/a.geom", new DocumentPosition(0, 4))!.Markdown);
    }
}
