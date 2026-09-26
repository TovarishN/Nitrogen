using Nitrogen.Binding;
using Nitrogen.Semantics;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The semantics runtime on a language without semantics (issue 239).</summary>
public class FileSemanticsTests
{
    static readonly Property<string> Probe = new("Probe", inherited: false, static () => "default");

    [Fact]
    public void A_language_without_semantics_yields_defaults_and_no_diagnostics()
    {
        using var parsed = FileBindingTests.Scopes.Parse("unit a { let x = 1; }", ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        project.Set("a.scopes", parsed.Tree);
        var semantics = new ProjectSemantics(project);
        var file = semantics["a.scopes"];
        Assert.Equal("default", file.Get(file.Tree.Root, Probe));
        Assert.Empty(file.Diagnostics());
        Assert.Same(file, semantics["a.scopes"]);

        project.Set("a.scopes", parsed.Tree);
        Assert.NotSame(file, semantics["a.scopes"]); // a change replaces every file's semantics
    }

    [Fact]
    public void The_semantic_parent_skips_lists_and_a_reference_knows_its_symbol()
    {
        const string text = "unit a { use a; }";
        using var parsed = FileBindingTests.Scopes.Parse(text, ScopesModule.File);
        var project = new Project(FileBindingTests.Scopes);
        project.Set("a.scopes", parsed.Tree);
        var file = new ProjectSemantics(project)["a.scopes"];

        int unit = Find(file, ScopesKinds.Unit);
        Assert.Equal(file.Tree.Root, file.ParentOf(unit)); // File → List → Unit
        Assert.Equal(-1, file.ParentOf(file.Tree.Root));

        var symbol = file.SymbolOf(Find(file, ScopesKinds.Use));
        Assert.Equal(("unit", "a"), (symbol!.Kind, symbol.Name));
        Assert.Null(file.SymbolOf(unit));
        Assert.Equal("a", new SemanticNode(file, unit).Semantics.Tree.GetText(file.Tree.Child(unit, 1)).ToString());
    }

    static int Find(FileSemantics file, int kind)
    {
        for (int node = 0; node < file.Tree.NodeCount; node++)
            if (file.Tree.Kind(node) == kind) return node;
        throw new InvalidOperationException("kind not found");
    }
}
