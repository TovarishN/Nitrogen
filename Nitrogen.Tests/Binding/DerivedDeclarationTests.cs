using Nitrogen.Binding;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DerivedDeclarationTests
{
    const string Source = "unit a { globalgen limb$1; let local = limb_1; let bad = limb_9; }";

    static DerivedDeclaration Generated(FileBinding binding, string name, bool export = false)
    {
        int node = Enumerable.Range(0, binding.Tree.NodeCount)
            .Single(node => binding.Tree.Kind(node) == ScopesKinds.GlobalGen);
        return new DerivedDeclaration(name, node, binding.Tree.Span(binding.Tree.Child(node, 1)), export);
    }

    [Fact]
    public void Publishing_derived_names_seals_file_openness_and_preserves_source_locations()
    {
        using var parsed = FileBindingTests.Scopes.Parse(Source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var original = project.Set("a", parsed.Tree);
        Assert.Empty(project.Diagnostics("a"));
        Assert.Empty(project.Resolve(original.References[0]));
        var declaration = Generated(original, "limb_1");
        var binding = project.SetDerivedDeclarations("a", "value", [declaration]);
        var symbol = Assert.Single(project.Resolve(binding.References[0]));
        Assert.Equal(declaration.Node, symbol.Node);
        Assert.Equal(declaration.NameSpan, symbol.NameSpan);
        Assert.Equal("a", symbol.Path);
        var error = Assert.Single(project.Diagnostics("a"));
        Assert.Equal(BindingCodes.Unresolved, error.Code);
        Assert.Equal(Source.IndexOf("limb_9", StringComparison.Ordinal), error.Span.Start);
        Assert.Contains(binding.Declarations, symbol => symbol.Name == "local");
        Assert.Contains(binding.Declarations, symbol => symbol.Kind == "unit" && symbol.Name == "a");
    }

    [Fact]
    public void Local_declarations_still_shadow_derived_file_names()
    {
        const string source = "unit a { globalgen limb$1; let limb_1 = 2; let use = limb_1; }";
        using var parsed = FileBindingTests.Scopes.Parse(source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var original = project.Set("a", parsed.Tree);
        var binding = project.SetDerivedDeclarations("a", "value", [Generated(original, "limb_1")]);
        Assert.Empty(project.Diagnostics("a"));
        Assert.Equal(source.IndexOf("limb_1 =", StringComparison.Ordinal),
            Assert.Single(project.Resolve(binding.References[0])).NameSpan.Start);
    }

    [Fact]
    public void Replacing_the_index_invalidates_export_resolution_and_does_not_accumulate_names()
    {
        using var parsed = FileBindingTests.Scopes.Parse(Source, ScopesModule.File);
        using var consumer = FileBindingTests.Scopes.Parse("unit b { let first = limb_1; let last = limb_9; }", ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var original = project.Set("a", parsed.Tree);
        var uses = project.Set("b", consumer.Tree).References;
        project.SetDerivedDeclarations("a", "value", [Generated(original, "limb_1", true)]);
        Assert.Single(project.Resolve(uses[0]));
        Assert.Empty(project.Resolve(uses[1]));
        project.SetDerivedDeclarations("a", "value", [Generated(original, "limb_9", true)]);
        Assert.Empty(project.Resolve(uses[0]));
        Assert.Single(project.Resolve(uses[1]));
        Assert.DoesNotContain(project["a"].Declarations, symbol => symbol.Name == "limb_1");
        project.Set("a", parsed.Tree);
        Assert.Empty(project.Resolve(uses[1]));
    }

    [Fact]
    public void Empty_index_removes_authored_templates_and_keeps_unknown_names_as_errors()
    {
        using var parsed = FileBindingTests.Scopes.Parse("unit a { global limb; let local = limb; }", ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        project.Set("a", parsed.Tree);
        project.SetDerivedDeclarations("a", "value", []);
        Assert.Equal(BindingCodes.Unresolved, Assert.Single(project.Diagnostics("a")).Code);
        Assert.DoesNotContain(project["a"].Declarations, symbol => symbol.Name == "limb");
    }

    [Fact]
    public void Duplicate_derived_names_keep_normal_duplicate_diagnostics()
    {
        using var parsed = FileBindingTests.Scopes.Parse(Source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var binding = project.Set("a", parsed.Tree);
        var declaration = Generated(binding, "limb_1");
        project.SetDerivedDeclarations("a", "value", [declaration, declaration]);
        Assert.Contains(project.Diagnostics("a"), error => error.Code == BindingCodes.Duplicate && error.Span == declaration.NameSpan);
    }

    [Fact]
    public void Invalid_source_node_does_not_replace_the_current_index()
    {
        using var parsed = FileBindingTests.Scopes.Parse(Source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var binding = project.Set("a", parsed.Tree);
        var declaration = Generated(binding, "limb_1");
        project.SetDerivedDeclarations("a", "value", [declaration]);
        int version = project.Version;
        Assert.Throws<ArgumentOutOfRangeException>(() => project.SetDerivedDeclarations("a", "value",
            [declaration with { Node = parsed.Tree.NodeCount }]));
        Assert.Equal(version, project.Version);
        Assert.Single(project.Resolve(project["a"].References[0]));
    }

    [Fact]
    public void Non_exported_derived_names_stay_inside_their_document()
    {
        using var parsed = FileBindingTests.Scopes.Parse(Source, ScopesModule.File);
        using var other = FileBindingTests.Scopes.Parse("unit b { let use = limb_1; }", ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var binding = project.Set("a", parsed.Tree);
        project.Set("b", other.Tree);
        project.SetDerivedDeclarations("a", "value", [Generated(binding, "limb_1")]);
        Assert.Empty(project.Resolve(project["b"].References[0]));
        Assert.Equal(BindingCodes.Unresolved, Assert.Single(project.Diagnostics("b")).Code);
        Assert.Contains(project.VisibleAt("a", Source.Length - 2, "value"), symbol => symbol.Name == "limb_1");
    }

    [Fact]
    public void Publishing_another_kind_preserves_previously_derived_indexes()
    {
        using var parsed = FileBindingTests.Scopes.Parse(Source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var binding = project.Set("a", parsed.Tree);
        var unit = Assert.Single(binding.Declarations, symbol => symbol.Kind == "unit");
        project.SetDerivedDeclarations("a", "unit", [new DerivedDeclaration("renamed", unit.Node, unit.NameSpan)]);
        project.SetDerivedDeclarations("a", "value", [Generated(binding, "limb_1")]);
        Assert.Equal("renamed", Assert.Single(project["a"].Declarations, symbol => symbol.Kind == "unit").Name);
        Assert.Single(project.Resolve(project["a"].References[0]));
    }

    [Fact]
    public void Invalid_source_span_is_rejected_atomically()
    {
        using var parsed = FileBindingTests.Scopes.Parse(Source, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        var binding = project.Set("a", parsed.Tree);
        var declaration = Generated(binding, "limb_1");
        project.SetDerivedDeclarations("a", "value", [declaration]);
        int version = project.Version;
        Assert.Throws<ArgumentException>(() => project.SetDerivedDeclarations("a", "value",
            [declaration with { NameSpan = new TextSpan(0, 1) }]));
        Assert.Equal(version, project.Version);
        Assert.Single(project.Resolve(project["a"].References[0]));
    }
}
