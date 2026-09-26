using Gravity.RagdollEditor;
using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests.Geometry;

public sealed class GeometryDefinitionExpansionTests
{
    const string Definition = "def crate(width: Scalar, height: Scalar, depth: Scalar) = box width height depth;";
    const string Calls = "make crate(1, 2, 3); make crate(3, 2, 1);";

    sealed class Harness : IDisposable
    {
        readonly List<ParseResult> _parsed = [];
        public ModuleComposition Composition { get; } = ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);
        public Project Project { get; }
        public ProjectSemantics Semantics { get; }

        public Harness()
        {
            Project = new Project(Composition.Language);
            Semantics = new ProjectSemantics(Project);
        }

        public void Add(string path, string source)
        {
            var parsed = Composition.Language.Parse(source, Composition.StartRules[("Geometry", "Document")]);
            _parsed.Add(parsed);
            Assert.True(parsed.Success, parsed.Diagnostics.Length > 0 ? parsed.FormatMessage(parsed.Diagnostics[0]) : "");
            Project.Set(path, parsed.Tree);
        }

        public LoweringResult Lower(string path, Guid? snapshot = null) =>
            HirLowering.Lower(Semantics[path], Composition.Language.SemanticCatalog, snapshot ?? Guid.NewGuid());

        public void Dispose()
        {
            foreach (var parsed in _parsed) parsed.Dispose();
        }
    }

    [Fact]
    public void Two_cross_file_calls_expand_to_ordered_box_operations_with_both_origins()
    {
        using var harness = new Harness();
        harness.Add("definitions.geom", Definition);
        harness.Add("scene.geom", Calls);
        var snapshot = Guid.NewGuid();
        Assert.Empty(harness.Lower("definitions.geom", snapshot).Roots);
        var lowered = harness.Lower("scene.geom", snapshot);
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal(2, lowered.Roots.Count);
        Assert.Equal([1f, 2f, 3f], Values(lowered.Roots[0]));
        Assert.Equal([3f, 2f, 1f], Values(lowered.Roots[1]));
        foreach (var root in lowered.Roots)
        {
            Assert.Equal(BoxMeshModule.BoxSignature, Assert.IsType<HirOperation>(root).Signature);
            Assert.Contains(root.Origins, origin => origin.Path == "definitions.geom" && origin.SnapshotId == snapshot);
            Assert.Contains(root.Origins, origin => origin.Path == "scene.geom" && origin.SnapshotId == snapshot);
            foreach (var child in Assert.IsType<HirOperation>(root).Arguments)
            {
                Assert.Contains(child.Origins, origin => origin.Path == "definitions.geom");
                Assert.Contains(child.Origins, origin => origin.Path == "scene.geom");
            }
        }
        Assert.NotEqual(lowered.Roots[0].Origins.First(origin => origin.Path == "scene.geom").Span,
            lowered.Roots[1].Origins.First(origin => origin.Path == "scene.geom").Span);
    }

    [Fact]
    public void Missing_or_ambiguous_shape_reports_call_name_and_no_root()
    {
        using var missing = new Harness();
        missing.Add("scene.geom", "make absent(1,2,3);");
        var missingResult = missing.Lower("scene.geom");
        Assert.Empty(missingResult.Roots);
        var missingError = Assert.Single(missingResult.Diagnostics, diagnostic => diagnostic.Code == "GD0002");
        Assert.Equal("absent", "make absent(1,2,3);".Substring(missingError.Origin.Span.Start, missingError.Origin.Span.Length));

        using var ambiguous = new Harness();
        ambiguous.Add("a.geom", Definition);
        ambiguous.Add("b.geom", Definition);
        ambiguous.Add("scene.geom", "make crate(1,2,3);");
        var ambiguousResult = ambiguous.Lower("scene.geom");
        Assert.Empty(ambiguousResult.Roots);
        var ambiguousError = Assert.Single(ambiguousResult.Diagnostics, diagnostic => diagnostic.Code == "GD0002");
        Assert.Equal("crate", "make crate(1,2,3);".Substring(ambiguousError.Origin.Span.Start, ambiguousError.Origin.Span.Length));
    }

    [Fact]
    public void Invalid_definition_and_wrong_call_arity_cannot_expand()
    {
        using var badType = new Harness();
        const string invalidDefinition = "def crate(width: Mesh, height: Scalar, depth: Scalar) = box width height depth;";
        badType.Add("definitions.geom", invalidDefinition);
        badType.Add("scene.geom", "make crate(1,2,3);");
        Assert.Contains(badType.Semantics["definitions.geom"].Diagnostics(), diagnostic => diagnostic.Code == "GD0001");
        Assert.Empty(badType.Lower("scene.geom").Roots);

        using var arity = new Harness();
        arity.Add("definitions.geom", Definition);
        arity.Add("scene.geom", "make crate(1,2);");
        Assert.Contains(arity.Semantics["scene.geom"].Diagnostics(), diagnostic => diagnostic.Code == "GD0003");
        Assert.Empty(arity.Lower("scene.geom").Roots);
    }

    [Fact]
    public void Invalid_sibling_definition_does_not_block_a_valid_shape_in_the_same_file()
    {
        using var harness = new Harness();
        harness.Add("definitions.geom",
            "def broken(w: Mesh, h: Scalar, d: Scalar) = box w h d; " + Definition);
        harness.Add("scene.geom", "make crate(1,2,3);");
        var lowered = harness.Lower("scene.geom");
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal([1f, 2f, 3f], Values(Assert.Single(lowered.Roots)));
    }

    [Fact]
    public void Missing_nested_definition_reports_its_name()
    {
        using var harness = new Harness();
        const string definition =
            "def outer(w: Scalar, h: Scalar, d: Scalar) = make missing(w,h,d);";
        harness.Add("definitions.geom", definition);
        harness.Add("scene.geom", "make outer(1,2,3);");
        var lowered = harness.Lower("scene.geom");
        Assert.Empty(lowered.Roots);
        var error = Assert.Single(lowered.Diagnostics, diagnostic => diagnostic.Code == "GD0002");
        Assert.Equal("definitions.geom", error.Origin.Path);
        Assert.Equal("missing", definition.Substring(error.Origin.Span.Start, error.Origin.Span.Length));
    }

    [Theory]
    [InlineData("make crate(0,2,3);", "0")]
    [InlineData("make crate(-1,2,3);", "-1")]
    [InlineData("make crate(1e999,2,3);", "1e999")]
    public void Invalid_call_dimension_has_literal_diagnostic_and_no_root(string source, string literal)
    {
        using var harness = new Harness();
        harness.Add("definitions.geom", Definition);
        harness.Add("scene.geom", source);
        var diagnostic = Assert.Single(harness.Semantics["scene.geom"].Diagnostics(), item => item.Code == "GE0001");
        Assert.Equal(literal, source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
        Assert.Empty(harness.Lower("scene.geom").Roots);
    }

    [Fact]
    public void Forwarding_across_definitions_uses_bound_parameter_identity_and_preserves_origins()
    {
        using var harness = new Harness();
        harness.Add("outer.geom",
            "def outer(width: Scalar, height: Scalar, depth: Scalar) = make inner(width,height,depth);");
        harness.Add("inner.geom",
            "def inner(width: Scalar, height: Scalar, depth: Scalar) = box width height depth;");
        harness.Add("scene.geom", "make outer(1,2,3); make outer(3,2,1);");
        var lowered = harness.Lower("scene.geom");
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal(2, lowered.Roots.Count);
        Assert.Equal([1f, 2f, 3f], Values(lowered.Roots[0]));
        Assert.Equal([3f, 2f, 1f], Values(lowered.Roots[1]));
        foreach (var root in lowered.Roots)
        {
            Assert.Contains(root.Origins, origin => origin.Path == "outer.geom");
            Assert.Contains(root.Origins, origin => origin.Path == "inner.geom");
            Assert.Contains(root.Origins, origin => origin.Path == "scene.geom");
            foreach (var child in Assert.IsType<HirOperation>(root).Arguments)
            {
                Assert.Contains(child.Origins, origin => origin.Path == "outer.geom");
                Assert.Contains(child.Origins, origin => origin.Path == "inner.geom");
                Assert.Contains(child.Origins, origin => origin.Path == "scene.geom");
            }
        }
        Assert.NotEqual(lowered.Roots[0].Origins.First(origin => origin.Path == "scene.geom").Span,
            lowered.Roots[1].Origins.First(origin => origin.Path == "scene.geom").Span);
    }

    [Fact]
    public void Forwarding_uses_parameter_position_when_names_differ()
    {
        using var harness = new Harness();
        harness.Add("outer.geom",
            "def outer(width: Scalar, height: Scalar, depth: Scalar) = make inner(depth,width,height);");
        harness.Add("inner.geom",
            "def inner(a: Scalar, b: Scalar, c: Scalar) = box a b c;");
        harness.Add("scene.geom", "make outer(1,2,3);");
        var lowered = harness.Lower("scene.geom");
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal([3f, 1f, 2f], Values(Assert.Single(lowered.Roots)));
    }

    [Theory]
    [InlineData("def loop(a: Scalar, b: Scalar, c: Scalar) = make loop(a,b,c);", "make loop(1,2,3);", "loop")]
    [InlineData("def outer(a: Scalar, b: Scalar, c: Scalar) = make inner(a,b,c);\n" +
        "def inner(a: Scalar, b: Scalar, c: Scalar) = make outer(a,b,c);", "make outer(1,2,3);", "outer")]
    public void Recursive_definition_reports_closing_call_without_a_root(string definitions, string source, string closingName)
    {
        using var harness = new Harness();
        harness.Add("definitions.geom", definitions);
        harness.Add("scene.geom", source);
        var lowered = harness.Lower("scene.geom");
        Assert.Empty(lowered.Roots);
        var diagnostic = Assert.Single(lowered.Diagnostics, item => item.Code == "GD0004");
        Assert.Equal("definitions.geom", diagnostic.Origin.Path);
        Assert.Equal(closingName,
            definitions.Substring(diagnostic.Origin.Span.Start, diagnostic.Origin.Span.Length));
    }

    static float[] Values(HirNode root) => Assert.IsType<HirOperation>(root).Arguments
        .Select(node => Assert.IsType<HirConstant>(node).Value).ToArray();
}
