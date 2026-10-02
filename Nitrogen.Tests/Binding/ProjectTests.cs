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
    public void File_declarations_are_visible_to_sibling_scopes_but_not_other_files()
    {
        var binding = Set("a", "unit a { block { global shared; } let x = 1; } unit b { let x = 2; let y = shared; }");
        var symbol = Assert.Single(Project.Resolve(Assert.Single(binding.References)));
        Assert.Equal("shared", symbol.Name);
        Assert.False(symbol.IsExported);
        Assert.Empty(Diagnostics("a"));
        Set("b", "unit c { let y = shared; }");
        Assert.Equal(new[] { "NB0001 shared" }, Diagnostics("b"));
    }

    [Fact]
    public void File_declarations_in_sibling_scopes_still_report_duplicates()
    {
        Set("a", "unit a { global shared; } unit b { global shared; }");
        Assert.Equal(new[] { "NB0002 shared" }, Diagnostics("a"));
    }

    [Fact]
    public void A_declaration_is_visible_throughout_its_scope() =>
        Assert.Empty(DiagnosticsOf("unit a { let y = x; let x = 1; }"));

    [Fact]
    public void Sequential_references_and_completion_use_the_last_completed_declaration()
    {
        const string text = "unit a { seq x = 1; seq y = x; seq x = x; seq z = x; }";
        var binding = Set("a", text);
        int first = text.IndexOf("x = 1", StringComparison.Ordinal);
        int second = text.IndexOf("x = x", StringComparison.Ordinal);
        Assert.Equal(new[] { first, first, second }, binding.References.Select(reference =>
            Assert.Single(Project.Resolve(reference)).NameSpan.Start));
        Assert.Equal(first, Assert.Single(Project.CandidatesFor(binding.References[1]), symbol => symbol.Name == "x").NameSpan.Start);
        Assert.Equal(second, Assert.Single(Project.VisibleAt("a", text.IndexOf("seq z", StringComparison.Ordinal), "value"),
            symbol => symbol.Name == "x").NameSpan.Start);
        Assert.Empty(Diagnostics("a"));
    }

    [Fact]
    public void Sequential_self_and_forward_references_are_not_visible()
    {
        Set("a", "unit a { seq x = x; seq y = later; seq later = 1; }");
        Assert.Equal(new[] { "NB0004 x", "NB0004 later" }, Diagnostics("a"));
    }

    [Fact]
    public void Sequential_initializer_can_see_an_enclosing_value()
    {
        const string text = "unit a { seq x = 1; block { seq x = x; seq y = x; } }";
        var binding = Set("a", text);
        Assert.Equal(new[] { text.IndexOf("x = 1", StringComparison.Ordinal), text.IndexOf("x = x", StringComparison.Ordinal) },
            binding.References.Select(reference => Assert.Single(Project.Resolve(reference)).NameSpan.Start));
        Assert.Empty(Diagnostics("a"));
    }

    [Fact]
    public void Sequential_exports_expose_the_final_value_per_document()
    {
        const string text = "unit a { seqexport x = 1; seqexport x = x; }";
        var local = Set("a", text);
        Assert.Equal(text.IndexOf("x = 1", StringComparison.Ordinal), Assert.Single(Project.Resolve(Assert.Single(local.References))).NameSpan.Start);
        var remote = Set("b", "unit b { let y = x; }");
        Assert.Equal(text.IndexOf("x = x", StringComparison.Ordinal), Assert.Single(Project.Resolve(Assert.Single(remote.References))).NameSpan.Start);
        Assert.Empty(Diagnostics("a"));
        Assert.Empty(Diagnostics("b"));
    }

    [Fact]
    public void Mixing_sequential_and_ordinary_declarations_still_reports_a_duplicate()
    {
        Set("a", "unit a { let x = 1; seq x = 2; }");
        Assert.Equal(new[] { "NB0002 x" }, Diagnostics("a"));
    }

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
