using Nitrogen.Binding;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Documents bound together: scopes, exports, built-ins and diagnostics (issue 237).</summary>
public sealed class ProjectTests : IDisposable
{
    readonly List<ParseResult> _parsed = new();

    internal Project Project { get; } = new(FileBindingTests.Scopes);

    internal FileBinding Set(string path, string text)
    {
        var result = FileBindingTests.Scopes.Parse(text, ScopesModule.File);
        _parsed.Add(result);
        return Project.Set(path, result.Tree);
    }

    internal string[] Diagnostics(string path) =>
        Project.Diagnostics(path).Select(d => $"{d.Code} {Project[path].Tree.Text.Substring(d.Span.Start, d.Span.Length)}").ToArray();

    public void Dispose()
    {
        foreach (var result in _parsed) result.Dispose();
    }

    [Fact]
    public void The_innermost_declaration_wins()
    {
        const string text = "unit a { let x = 1; block { let x = 2; let y = x; } }";
        var binding = Set("a", text);
        var symbol = Assert.Single(Project.Resolve(binding.References[0]));
        Assert.Equal(text.IndexOf("x = 2", StringComparison.Ordinal), symbol.NameSpan.Start);
        Assert.Empty(Diagnostics("a"));
    }

    [Fact]
    public void A_declaration_is_visible_throughout_its_scope() =>
        Assert.Empty(DiagnosticsOf("unit a { let y = x; let x = 1; }"));

    [Fact]
    public void Unresolved_and_not_visible()
    {
        Assert.Equal(new[] { "NB0004 z", "NB0001 q" }, DiagnosticsOf("unit a { block { let z = 1; } let w = z; let v = q; }"));
    }

    [Fact]
    public void Duplicates_are_project_diagnostics_too() =>
        Assert.Equal(new[] { "NB0002 x" }, DiagnosticsOf("unit a { let x = 1; let x = 2; }"));

    [Fact]
    public void Exports_cross_files_and_follow_replacement()
    {
        Set("a", "unit a { let x = 1; }");
        var b = Set("b", "unit b { use a; let y = x; }");
        Assert.Equal(new[] { "NB0001 x" }, Diagnostics("b")); // lets are not exported
        Assert.Equal("a", Assert.Single(Project.Resolve(b.References[0])).Path);

        int version = Project.Version;
        Set("a", "unit c { }");
        Assert.True(Project.Version > version);
        Assert.Equal(new[] { "NB0001 a", "NB0001 x" }, Diagnostics("b"));

        Project.Remove("a");
        Assert.Equal(new[] { "b" }, Project.Paths);
    }

    [Fact]
    public void Two_files_exporting_one_name_are_ambiguous()
    {
        Set("a", "unit a { }");
        Set("b", "unit a { }");
        var c = Set("c", "unit c { use a; }");
        Assert.Equal(new[] { "NB0003 a" }, Diagnostics("a"));
        Assert.Equal(new[] { "NB0003 a" }, Diagnostics("b"));
        Assert.Equal(2, Project.Resolve(c.References[0]).Count);
        Assert.Empty(Diagnostics("c"));
    }

    [Fact]
    public void Builtins_resolve_last()
    {
        var binding = Set("a", "unit a { let t = time; let m = max(1); let pi = 3; let r = pi; }");
        Assert.Empty(Diagnostics("a"));
        Assert.True(Assert.Single(Project.Resolve(binding.References[0])).IsBuiltin);
        Assert.False(Assert.Single(Project.Resolve(binding.References[^1])).IsBuiltin); // the local pi hides the built-in
    }

    [Fact]
    public void A_scoped_builtin_is_visible_only_inside_its_rules_scopes()
    {
        var binding = Set("a", "unit a { let h = here; block { let i = here; } }");
        Assert.Equal(new[] { "NB0001 here" }, Diagnostics("a"));
        Assert.True(Assert.Single(Project.Resolve(binding.References[1])).IsBuiltin);
    }

    [Fact]
    public void An_optional_dotted_reference_hides_its_parts_only_when_it_resolves()
    {
        var binding = Set("a", "unit a { let c = sys.clock; let d = foo.bar; let e = pi.x; }");
        Assert.Equal(new[] { "NB0001 foo" }, Diagnostics("a"));
        Assert.True(Project.IsEffective(binding.References[0]));   // sys.clock
        Assert.False(Project.IsEffective(binding.References[1]));  // sys, hidden by it
    }

    [Fact]
    public void A_dynamic_name_silences_its_scope_only()
    {
        const string text = "unit a { gen p$1; let y = p1; } unit b { let z = p1; }";
        Set("a", text);
        var diagnostic = Assert.Single(Project.Diagnostics("a"));
        Assert.Equal(BindingCodes.Unresolved, diagnostic.Code);
        Assert.Equal(text.LastIndexOf("p1", StringComparison.Ordinal), diagnostic.Span.Start);
    }

    [Theory]
    [InlineData("unit a { let x = 1 let y = x; }")]
    [InlineData("unit { let x = 1; }")]
    [InlineData("unit a { use ; }")]
    [InlineData("unit a { let x = ; }")]
    [InlineData("unit a { let x = 1 ) ; let y = x; }")]
    public void A_parse_error_never_causes_a_binding_diagnostic(string text)
    {
        Set("a", text);
        Assert.NotEmpty(_parsed[^1].Diagnostics.ToArray());
        Assert.Empty(Diagnostics("a"));
    }

    string[] DiagnosticsOf(string text)
    {
        Set("a", text);
        return Diagnostics("a");
    }
}
