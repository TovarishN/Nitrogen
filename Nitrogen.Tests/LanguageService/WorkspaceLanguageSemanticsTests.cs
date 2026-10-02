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
    public void A_call_in_another_file_lowers_through_the_declarative_template()
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

    const string Cube = "def cube(w: Scalar, h: Scalar, d: Scalar) = box w h d;";

    [Fact]
    public void Each_call_that_closes_a_definition_cycle_shows_its_lowering_error()
    {
        using var service = Service();
        service.Open(Uri("defs.geom"), 1,
            "def a(w: Scalar, h: Scalar, d: Scalar) = make b(w, h, d);\ndef b(w: Scalar, h: Scalar, d: Scalar) = make a(w, h, d);");
        service.Open(Uri("use.geom"), 1, "make a(1, 2, 3);");
        service.Open(Uri("again.geom"), 1, "make b(1, 2, 3);");

        // use.geom enters the cycle at a, closing it at b's call of a; again.geom closes it at a's call of b.
        var cycles = service.Diagnostics(Uri("defs.geom")).Where(d => d.Code == "NH0007").ToList();
        Assert.Equal([new DocumentPosition(0, 46), new DocumentPosition(1, 46)], cycles.Select(d => d.Range.Start).OrderBy(p => p.Line));
        Assert.All(cycles, d => Assert.Equal(ServiceSeverity.Error, d.Severity));
        Assert.Empty(service.Diagnostics(Uri("use.geom")));
    }

    [Fact]
    public void A_lowering_error_clears_when_its_cause_is_fixed()
    {
        using var service = Service();
        service.Open(Uri("defs.geom"), 1, "def a(w: Scalar, h: Scalar, d: Scalar) = make a(w, h, d);");
        service.Open(Uri("use.geom"), 1, "make a(1, 2, 3);");
        Assert.Contains(service.Diagnostics(Uri("defs.geom")), d => d.Code == "NH0007");

        Assert.Contains(Uri("use.geom"), service.Change(Uri("defs.geom"), 2, Cube.Replace("cube", "a")));
        Assert.Empty(service.Diagnostics(Uri("defs.geom")));
        Assert.Empty(service.Diagnostics(Uri("use.geom")));
    }

    [Fact]
    public void An_undefined_shape_is_reported_once()
    {
        using var service = Service();
        service.Open(Uri("use.geom"), 1, "make nope(1, 2, 3);");
        var diagnostic = Assert.Single(service.Diagnostics(Uri("use.geom")));
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 5), new DocumentPosition(0, 9)), diagnostic.Range);
    }

    [Theory]
    [InlineData("box 0 2 3;")]          // GE0001: a semantic error
    [InlineData("box 1 2;")]            // a parse error
    [InlineData("box 1 2 3; box -1 2 3;")]
    public void A_lowering_block_by_an_error_already_shown_is_not_repeated(string text)
    {
        using var service = Service();
        service.Open(Uri("a.geom"), 1, text);
        var diagnostics = service.Diagnostics(Uri("a.geom"));
        Assert.NotEmpty(diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Code.StartsWith("NH", StringComparison.Ordinal));
    }
}
