using Nitrogen.Binding;
using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>One file's declarations, references and scopes (issue 237).</summary>
public class FileBindingTests
{
    internal static readonly Language Scopes = new LanguageBuilder().Add(ScopesModule.Instance).Build();

    static string Text(FileBinding binding, TextSpan span) => binding.Tree.Text.Substring(span.Start, span.Length);

    static string[] Declarations(FileBinding binding) =>
        binding.Declarations.Select(d => $"{d.Kind} {d.Name}{(d.IsExported ? " export" : "")}").ToArray();

    static string[] References(FileBinding binding) =>
        binding.References.Select(r => $"{string.Join("|", r.Kinds)} {r.Name}{(r.IsOptional ? "?" : "")}").ToArray();

    [Fact]
    public void Declarations_references_and_scopes()
    {
        using var parsed = Scopes.Parse("unit a { let x = 1; block { let y = x; } use a; }", ScopesModule.File);
        var binding = FileBinding.Bind("a.scopes", parsed.Tree);
        Assert.Equal(new[] { "unit a export", "value x", "value y" }, Declarations(binding));
        Assert.Equal(new[] { "value|func x", "unit a" }, References(binding));
        Assert.All(binding.Declarations, d => Assert.Equal(d.Name, Text(binding, d.NameSpan)));
        Assert.Equal(3, binding.Scopes.Length); // file, unit a, block
        Assert.Equal(0, binding.Declarations[0].Scope);
        Assert.Equal(1, binding.Declarations[1].Scope);
        Assert.Equal(2, binding.Declarations[2].Scope);
        Assert.Empty(binding.Diagnostics);
    }

    [Fact]
    public void A_duplicate_in_one_scope_is_reported_after_the_first()
    {
        const string text = "unit a { let x = 1; let x = 2; block { let x = 3; } }";
        using var parsed = Scopes.Parse(text, ScopesModule.File);
        var binding = FileBinding.Bind("a.scopes", parsed.Tree);
        var duplicate = Assert.Single(binding.Diagnostics);
        Assert.Equal(BindingCodes.Duplicate, duplicate.Code);
        Assert.Equal(text.IndexOf("x = 2", StringComparison.Ordinal), duplicate.Span.Start);
    }

    [Fact]
    public void A_dotted_member_is_one_optional_reference_guarding_its_parts()
    {
        using var parsed = Scopes.Parse("unit a { let c = sys.clock; }", ScopesModule.File);
        var binding = FileBinding.Bind("a.scopes", parsed.Tree);
        Assert.Equal(new[] { "value sys.clock?", "value|func sys" }, References(binding));
        Assert.Equal(-1, binding.References[0].Guard);
        Assert.Equal(0, binding.References[1].Guard);
    }

    [Fact]
    public void Missing_names_bind_nothing()
    {
        using var parsed = Scopes.Parse("unit { let x = 1; } unit b { use ; }", ScopesModule.File);
        Assert.NotEmpty(parsed.Diagnostics.ToArray());
        var binding = FileBinding.Bind("a.scopes", parsed.Tree);
        Assert.Equal(new[] { "value x", "unit b export" }, Declarations(binding));
        Assert.Empty(binding.References);
    }

    [Fact]
    public void A_dynamic_name_opens_its_scope_instead_of_declaring()
    {
        using var parsed = Scopes.Parse("unit a { gen p$1; }", ScopesModule.File);
        var binding = FileBinding.Bind("a.scopes", parsed.Tree);
        Assert.Equal(new[] { "unit a export" }, Declarations(binding));
        Assert.True(binding.Scopes[1].IsOpen("value"));
        Assert.False(binding.Scopes[0].IsOpen("value"));
    }

    [Fact]
    public void A_missing_name_is_a_silent_hole_with_its_kinds()
    {
        const string text = "unit a { use ; let y = ; }";
        using var parsed = Scopes.Parse(text, ScopesModule.File);
        var binding = FileBinding.Bind("a.scopes", parsed.Tree);
        Assert.Empty(binding.References);
        Assert.Empty(binding.Diagnostics);
        var use = binding.Holes[0];
        Assert.Equal(new[] { "unit" }, use.Kinds);
        Assert.InRange(use.Position, text.IndexOf("use", StringComparison.Ordinal) + 3, text.IndexOf(';'));
    }

    [Fact]
    public void File_declarations_and_dynamic_names_belong_to_the_root_scope()
    {
        using var parsed = Scopes.Parse("unit a { block { global shared; globalgen p$1; } let local = 1; }", ScopesModule.File);
        Assert.True(parsed.Success);
        var binding = FileBinding.Bind("a.scopes", parsed.Tree);
        Assert.Equal(0, Assert.Single(binding.Declarations, symbol => symbol.Name == "shared").Scope);
        Assert.Equal(1, Assert.Single(binding.Declarations, symbol => symbol.Name == "local").Scope);
        Assert.True(binding.Scopes[0].IsOpen("value"));
        Assert.False(binding.Scopes[2].IsOpen("value"));
        Assert.Empty(binding.Diagnostics);
    }
}
