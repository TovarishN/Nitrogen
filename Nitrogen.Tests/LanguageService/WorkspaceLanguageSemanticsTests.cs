using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// Geometry served from its own nitrogen.json, as a generated editor plugin serves it: the semantic
/// module in its helper sources types and lowers documents, including cross-file definition calls.
/// </summary>
public sealed class WorkspaceLanguageSemanticsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-geometry-").FullName;

    public WorkspaceLanguageSemanticsTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "GeometryLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("Geometry.ngr"), 1, File.ReadAllText(Path.Combine(_root, "Geometry.ngr")));
        Assert.Empty(service.Diagnostics(Uri("Geometry.ngr")));
        return service;
    }

    [Fact]
    public void A_box_hovers_with_its_mesh_and_scalar_types()
    {
        using var service = Service();
        service.Open(Uri("a.geom"), 1, "box 1 2 3;");
        Assert.Empty(service.Diagnostics(Uri("a.geom")));
        Assert.Contains("`Geometry.Mesh`", service.Hover(Uri("a.geom"), new DocumentPosition(0, 1))!.Markdown);
        Assert.Contains("`Core.Scalar` = 1", service.Hover(Uri("a.geom"), new DocumentPosition(0, 4))!.Markdown);
    }

    [Fact]
    public void A_call_in_another_file_lowers_through_the_source_supplied_expander()
    {
        using var service = Service();
        service.Open(Uri("defs.geom"), 1, "def cube(w: Scalar, h: Scalar, d: Scalar) = box w h d;");
        service.Open(Uri("use.geom"), 1, "make cube(1, 2, 3);");
        Assert.Empty(service.Diagnostics(Uri("use.geom")));

        var inspection = service.InspectDocument(Uri("use.geom"))!;
        Assert.Empty(inspection.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(inspection.Roots));
        Assert.Equal("Geometry.BoxMesh", root.Signature.Id);
        Assert.Equal([1f, 2f, 3f], root.Arguments.Select(argument => Assert.IsType<HirConstant>(argument).Value));
    }

    [Fact]
    public void A_semantic_check_from_the_grammar_reports_in_the_document()
    {
        using var service = Service();
        service.Open(Uri("a.geom"), 1, "def bad(w: Mesh, h: Scalar, d: Scalar) = box 1 h d;");
        Assert.Contains(service.Diagnostics(Uri("a.geom")), d => d.Code == "GD0001");
    }

    [Fact]
    public void A_semantic_composition_error_shows_on_the_grammar()
    {
        string module = Path.Combine(_root, "BoxMeshModule.cs");
        File.WriteAllText(module, File.ReadAllText(module).Replace("new SemanticModule(\"Geometry\", []", "new SemanticModule(\"Geometry\", [\"Missing\"]"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("Geometry.ngr"), 1, File.ReadAllText(Path.Combine(_root, "Geometry.ngr")));
        var error = Assert.Single(service.Diagnostics(Uri("Geometry.ngr")), d => d.Code == "NC0001");
        Assert.Equal(ServiceSeverity.Error, error.Severity);
    }
}
