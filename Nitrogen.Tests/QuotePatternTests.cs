using Xunit;

namespace Nitrogen.Tests;

public sealed class QuotePatternTests
{
    [Fact]
    public void Repeated_scalar_capture_uses_structural_equality()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "$X + $X",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        using var same = language.ParseTemplate("1 /* trivia */ + 1", "QuoteLists.Expr", new Dictionary<string, string>());
        using var different = language.ParseTemplate("1 + 2", "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.True(pattern.Pattern!.Match(new(same.Tree, same.Tree.Root)).Matched);
        var mismatch = pattern.Pattern.Match(new(different.Tree, different.Tree.Root));
        Assert.False(mismatch.Matched);
        Assert.Empty(mismatch.Captures);
        Assert.Empty(mismatch.Diagnostics);
    }

    [Theory]
    [InlineData("01", "1", false)]
    [InlineData("1 + 2", "2 + 1", false)]
    [InlineData("1 + 2", "1 /* gap */ + 2", true)]
    [InlineData("1 + 2", "(1 + 2)", false)]
    public void Capture_free_patterns_compare_exact_structure(string text, string source, bool expected)
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Calc.CalcModule.Instance).Build();
        using var compiled = language.CompilePattern("Calc.Expr", text, new Dictionary<string, QuoteHole>());
        using var input = language.ParseTemplate(source, "Calc.Expr", new Dictionary<string, string>());
        Assert.True(compiled.Success);
        Assert.True(input.Success);
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.Equal(expected, result.Matched);
        Assert.Empty(result.Captures);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(source, input.Tree.Text);
    }

    [Fact]
    public void Captures_keep_first_subtree_identity_and_copy_the_contract()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        var contract = new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") };
        using var compiled = language.CompilePattern("QuoteLists.Expr", "$X + $X", contract);
        contract.Clear();
        using var input = language.ParseTemplate("1 + 1", "QuoteLists.Expr", new Dictionary<string, string>());
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.True(result.Matched);
        var captured = Assert.IsType<QuoteArgument.Scalar>(result.Captures["X"]).Value;
        Assert.Same(input.Tree, captured.Tree);
        Assert.Equal(input.Tree.Child(input.Tree.Root, 0), captured.Node);
        using var subtreePattern = language.CompilePattern("QuoteLists.Expr", "1", new Dictionary<string, QuoteHole>());
        Assert.True(subtreePattern.Pattern!.Match(captured).Matched);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, QuoteArgument>)result.Captures).Clear());
    }

    [Theory]
    [InlineData("Missing.Expr", "1", "NQP0001")]
    [InlineData("QuoteLists.Number", "1", "NQP0001")]
    [InlineData("QuoteLists.Expr", "1 /* $X */", "NQP0002")]
    [InlineData("QuoteLists.Expr", "broken", "NQP0001")]
    public void Invalid_contracts_fail_compilation(string category, string text, string code)
    {
        using var compiled = TemplateListInstantiationTests.CreateLanguage().CompilePattern(category, text,
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("QuoteLists.Expr") });
        Assert.False(compiled.Success);
        Assert.Null(compiled.Pattern);
        Assert.Contains(compiled.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Opaque_markers_are_not_capture_occurrences()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Lexical.LexicalModule.Instance).Build();
        using var compiled = language.CompilePattern("Lexical.Item", "\"$X\" /* $X */",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("Lexical.Item") });
        Assert.False(compiled.Success);
        Assert.Equal("NQP0002", Assert.Single(compiled.Diagnostics).Code);
        using var exact = language.CompilePattern("Lexical.Item", "\"$X\"", new Dictionary<string, QuoteHole>());
        using var input = language.ParseTemplate("\"$X\"", "Lexical.Item", new Dictionary<string, string>());
        Assert.True(exact.Pattern!.Match(new(input.Tree, input.Tree.Root)).Matched);
    }

    [Fact]
    public void Read_only_patterns_allow_declarations()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Scopes.ScopesModule.Instance).Build();
        using var compiled = language.CompilePattern("Scopes.Item", "let x = 1;", new Dictionary<string, QuoteHole>());
        using var input = language.ParseTemplate("let x = 1;", "Scopes.Item", new Dictionary<string, string>());
        Assert.True(compiled.Success);
        Assert.True(compiled.Pattern!.Match(new(input.Tree, input.Tree.Root)).Matched);
        using var construction = language.CompileQuote("Scopes.Item", "let x = 1;", new Dictionary<string, QuoteHole>());
        Assert.False(construction.Success);
    }
    [Fact]
    public void Declared_captures_override_overlapping_lexical_tokens_only_at_parsed_hole_sites()
    {
        var workspace = new Nitrogen.Workspace.GrammarWorkspace { CompositionName = "Overlap", StartRule = "Overlap.Program" };
        workspace.SetGrammar("Overlap.ngr", "syntax module Overlap { syntax Program=E; token Dollar=\"$X\"; extensible syntax E { | Atom=\"x\" | Money=Dollar } }");
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        var language = snapshot.Language!;
        using var pattern = language.CompilePattern("Overlap.E", "$X",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("Overlap.E") });
        using var source = language.ParseTemplate("x", "Overlap.E", new Dictionary<string, string>());
        Assert.True(pattern.Success);
        Assert.True(pattern.Pattern!.Match(new(source.Tree, source.Tree.Root)).Matched);
        using var literal = language.CompilePattern("Overlap.E", "$X", new Dictionary<string, QuoteHole>());
        Assert.False(literal.Pattern!.Match(new(source.Tree, source.Tree.Root)).Matched);
    }
}
