using Nitrogen.Binding;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Definition, references and visible names: what an editor asks (issue 237).</summary>
public sealed class ProjectQueryTests : IDisposable
{
    const string A = "unit a { let x = 1; block { let x = 2; let y = x + x; } let z = x; let c = sys.clock; }";

    readonly ProjectTests _project = new();

    public ProjectQueryTests()
    {
        _project.Set("a", A);
        _project.Set("b", "unit b { use a; }");
    }

    Project Project => _project.Project;

    static int At(string needle, int skip = 0)
    {
        int index = -1;
        for (int i = 0; i <= skip; i++) index = A.IndexOf(needle, index + 1, StringComparison.Ordinal);
        return index;
    }

    [Fact]
    public void Definition_of_a_reference_and_of_a_declaration()
    {
        var inner = Assert.Single(Project.DefinitionAt("a", At("x + x")));
        Assert.Equal(At("x = 2"), inner.NameSpan.Start);
        Assert.Same(inner, Assert.Single(Project.DefinitionAt("a", At("x = 2"))));
        Assert.Equal("sys.clock", Assert.Single(Project.DefinitionAt("a", At("sys") + 1)).Name); // the dotted name, not `sys`
        Assert.Empty(Project.DefinitionAt("a", At("{")));
    }

    [Fact]
    public void References_to_a_symbol_across_the_project()
    {
        var inner = Project.DefinitionAt("a", At("x = 2"))[0];
        var outer = Project.DefinitionAt("a", At("x = 1"))[0];
        Assert.Equal(new[] { At("x + x"), At("x", 3) }, Project.ReferencesTo(inner).Select(r => r.NameSpan.Start));
        Assert.Equal(new[] { At("x;", 1) }, Project.ReferencesTo(outer).Select(r => r.NameSpan.Start));

        var unit = Project.DefinitionAt("a", At("a")).Single();
        var use = Assert.Single(Project.ReferencesTo(unit));
        Assert.Equal("b", use.Path);
    }

    [Fact]
    public void Visible_names_innermost_first_then_exports_then_builtins()
    {
        var inBlock = Project.VisibleAt("a", At("x + x"), "value");
        Assert.Equal(new[] { "x", "y", "z", "c", "pi", "time", "sys.clock", "here" }, inBlock.Select(s => s.Name)); // `here` only in blocks
        Assert.Equal(At("x = 2"), inBlock[0].NameSpan.Start); // the inner x hides the outer one

        Assert.Equal(new[] { "a", "b" }, Project.VisibleAt("a", 0, "unit").Select(s => s.Name));
        var inUnit = Project.VisibleAt("a", At("let z"), "value");
        Assert.DoesNotContain(inUnit, s => s.Name == "y"); // y lives in the block
        Assert.DoesNotContain(inUnit, s => s.Name == "here");
    }

    [Fact]
    public void The_name_at_a_position_and_what_it_names()
    {
        var reference = Project.NameAt("a", At("x + x"))!.Value;
        Assert.Equal(new TextSpan(At("x + x"), 1), reference.Span);
        Assert.Equal(At("x = 2"), Assert.Single(reference.Symbols).NameSpan.Start);

        var dotted = Project.NameAt("a", At("sys") + 2)!.Value; // inside the dotted built-in
        Assert.Equal(new TextSpan(At("sys"), 9), dotted.Span);

        var declaration = Project.NameAt("a", At("x = 1") + 1)!.Value; // the end of a name still counts
        Assert.Equal(At("x = 1"), declaration.Span.Start);
        Assert.Null(Project.NameAt("a", At("{")));
    }

    public void Dispose() => _project.Dispose();
}
