using Nitrogen.Binding;
using Nitrogen.Tests.Scopes;
using Nitrogen.Tests.Typed;
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

    [Fact]
    public void An_absent_optional_before_a_declaration_child_binds_the_same_symbols()
    {
        // Let = "let" Name:Identifier Annotation:(":" TypeName)? "=" Value:Expr ";": the absent
        // annotation is a null slot, and Value keeps its position after it.
        var typed = new LanguageBuilder().Add(TypedModule.Instance).Build();
        using var bare = typed.Parse("let x = 1; let y = x;", TypedModule.File);
        using var annotated = typed.Parse("let x : num = 1; let y : num = x;", TypedModule.File);
        int let = bare.Tree.Child(bare.Tree.Child(0, 0), 0); // Statement is an alias: File, List, Let
        Assert.Equal(SyntaxTree.NoNode, bare.Tree.Child(let, 2));
        var bareBinding = FileBinding.Bind("a.typed", bare.Tree);
        var annotatedBinding = FileBinding.Bind("b.typed", annotated.Tree);
        Assert.Equal(new[] { "value x export", "value y export" }, Declarations(bareBinding));
        Assert.Equal(Declarations(annotatedBinding), Declarations(bareBinding));
        Assert.Equal(new[] { "value x" }, References(bareBinding));
        Assert.Equal(References(annotatedBinding), References(bareBinding));
        Assert.All(bareBinding.Declarations, d => Assert.Equal(d.Name, Text(bareBinding, d.NameSpan)));
        Assert.Empty(bareBinding.Diagnostics);
    }

    [Theory]
    [InlineData("anon a;", new[] { "value a" })]
    [InlineData("anon ;", new string[0])]
    public void An_optional_declared_name_declares_only_when_present(string text, string[] declarations)
    {
        var shapes = new LanguageBuilder().Add(Nitrogen.Tests.Shapes.ShapesModule.Instance).Build();
        using var parsed = shapes.Parse(text, Nitrogen.Tests.Shapes.ShapesModule.Anon);
        Assert.True(parsed.Success);
        Assert.Equal(declarations.Length == 0, parsed.Tree.Child(parsed.Tree.Root, 1) == SyntaxTree.NoNode);
        var binding = FileBinding.Bind("a.shapes", parsed.Tree);
        // Absent, as with the old Empty leaf: no symbol, no hole, no diagnostic.
        Assert.Equal(declarations, Declarations(binding));
        Assert.All(binding.Declarations, d => Assert.Equal(d.Name, Text(binding, d.NameSpan)));
        Assert.Empty(binding.Holes);
        Assert.Empty(binding.Diagnostics);
    }

    [Theory]
    [InlineData("want a;", new[] { "value a" })]
    [InlineData("want ;", new string[0])]
    public void An_optional_referenced_name_references_only_when_present(string text, string[] references)
    {
        var shapes = new LanguageBuilder().Add(Nitrogen.Tests.Shapes.ShapesModule.Instance).Build();
        using var parsed = shapes.Parse(text, Nitrogen.Tests.Shapes.ShapesModule.Want);
        Assert.True(parsed.Success);
        var binding = FileBinding.Bind("a.shapes", parsed.Tree);
        Assert.Equal(references, References(binding));
        Assert.Empty(binding.Holes);
        Assert.Empty(binding.Diagnostics);
    }
}
