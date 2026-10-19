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

    [Theory]
    [InlineData("f(1, 2)", true, "")]
    [InlineData("f(1, 3, 2)", true, "3")]
    [InlineData("f(1, 3, 4, 2)", true, "3|4")]
    [InlineData("f(0, 3, 2)", false, "")]
    [InlineData("f(1)", false, "")]
    public void List_capture_matches_fixed_prefix_suffix_and_middle_items(string text, bool matched, string items)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "f(1, ..$Args, 2)",
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        using var source = language.ParseTemplate(text, "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.True(pattern.Success, string.Join("\n", pattern.Diagnostics));
        var result = pattern.Pattern!.Match(new(source.Tree, source.Tree.Root));
        Assert.Equal(matched, result.Matched);
        Assert.Empty(result.Diagnostics);
        if (!matched) Assert.Empty(result.Captures);
        else
        {
            var captured = Assert.IsType<QuoteArgument.Many>(result.Captures["Args"]);
            Assert.Equal(items, string.Join("|", captured.Values.Select(value => value.Tree.GetText(value.Node).ToString())));
            Assert.All(captured.Values, value => Assert.Same(source.Tree, value.Tree));
            Assert.Throws<NotSupportedException>(() => ((IList<QuoteNode>)captured.Values).Clear());
        }
    }

    [Theory]
    [InlineData("space[1 ..$Args 2]", "space[1 3 4 2]", true)]
    [InlineData("f(f(..$Args), f(..$Args))", "f(f(1, 2), f(1, 2))", true)]
    [InlineData("f(f(..$Args), f(..$Args))", "f(f(1, 2), f(2, 1))", false)]
    [InlineData("f(f(..$Args), f(..$Args))", "f(f(), f())", true)]
    [InlineData("f(..$Args)", "f()", true)]
    public void Independent_lists_and_unseparated_items_have_deterministic_matching(string text, string input, bool matched)
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", text,
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        using var source = language.ParseTemplate(input, "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.True(pattern.Success, string.Join("\n", pattern.Diagnostics));
        Assert.True(source.Success);
        var result = pattern.Pattern!.Match(new(source.Tree, source.Tree.Root));
        Assert.Equal(matched, result.Matched);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("f(..$Args, ..$Args)")]
    [InlineData("tokenlist(..$Args)")]
    public void Unsupported_or_multiple_list_capture_sites_fail_compilation(string text)
    {
        using var pattern = TemplateListInstantiationTests.CreateLanguage().CompilePattern("QuoteLists.Expr", text,
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        Assert.False(pattern.Success);
        Assert.Contains(pattern.Diagnostics, diagnostic => diagnostic.Code == "NQP0003");
    }

    [Fact]
    public void A_list_capture_can_feed_a_replacement_and_required_lists_remain_nonempty()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        var contract = new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") };
        using var pattern = language.CompilePattern("QuoteLists.Expr", "f(1, ..$Args, 2)", contract);
        using var input = language.ParseTemplate("f(1, 3, 4, 2)", "QuoteLists.Expr", new Dictionary<string, string>());
        var match = pattern.Pattern!.Match(new(input.Tree, input.Tree.Root));
        using var replacement = language.CompileQuote("QuoteLists.Expr", "required(..$Args)", contract);
        using var built = replacement.Template!.Instantiate(match.Captures);
        Assert.True(built.Success);
        Assert.Equal("Test.Call([3:Core.Scalar,4:Core.Scalar])",
            TemplateListInstantiationTests.Shape(Assert.Single(TemplateListInstantiationTests.Lower(language, built.Tree!).Roots)));
        using var empty = language.ParseTemplate("f(1, 2)", "QuoteLists.Expr", new Dictionary<string, string>());
        var emptyMatch = pattern.Pattern.Match(new(empty.Tree, empty.Tree.Root));
        Assert.True(emptyMatch.Matched);
        using var refused = replacement.Template.Instantiate(emptyMatch.Captures);
        Assert.Equal("NQ0005", Assert.Single(refused.Diagnostics).Code);
    }

    [Fact]
    public void Nested_captures_keep_the_first_occurrence_in_pattern_order()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "f(f(..$Args), ..$Args)",
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        using var input = language.ParseTemplate("f(f(1, 2), 1, 2)", "QuoteLists.Expr", new Dictionary<string, string>());
        var match = pattern.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.True(match.Matched);
        var items = Assert.IsType<QuoteArgument.Many>(match.Captures["Args"]).Values;
        Assert.Equal(4, input.Tree.Span(items[0].Node).Start);
    }

    [Fact]
    public void Large_lists_and_nested_structure_match_without_recursive_comparison()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "f(..$Args)",
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        using var input = language.ParseTemplate("f(" + string.Join(",", Enumerable.Repeat("1", 4096)) + ")",
            "QuoteLists.Expr", new Dictionary<string, string>());
        Assert.Equal(4096, Assert.IsType<QuoteArgument.Many>(pattern.Pattern!.Match(new(input.Tree, input.Tree.Root)).Captures["Args"]).Values.Count);
        string deep = new string('(', 64) + "1" + new string(')', 64);
        var calc = new LanguageBuilder().Add(Nitrogen.Tests.Calc.CalcModule.Instance).Build();
        using var nested = calc.CompilePattern("Calc.Expr", "$X + $X",
            new Dictionary<string, QuoteHole> { ["X"] = QuoteHole.Node("Calc.Expr") });
        using var source = calc.ParseTemplate(deep + "+" + deep, "Calc.Expr", new Dictionary<string, string>());
        Assert.True(source.Success);
        Assert.True(nested.Pattern!.Match(new(source.Tree, source.Tree.Root)).Matched);
    }

    [Fact]
    public void Trailing_separator_recovery_cannot_be_matched_as_valid_list_syntax()
    {
        var language = TemplateListInstantiationTests.CreateLanguage();
        using var pattern = language.CompilePattern("QuoteLists.Expr", "f(..$Args)",
            new Dictionary<string, QuoteHole> { ["Args"] = QuoteHole.List("QuoteLists.Expr") });
        using var input = language.ParseTemplate("f(1,)", "QuoteLists.Expr", new Dictionary<string, string>());
        var result = pattern.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.False(result.Matched);
        Assert.Empty(result.Captures);
        Assert.NotEmpty(result.Diagnostics);
    }

    static readonly Language Shapes = new LanguageBuilder().Add(Nitrogen.Tests.Shapes.ShapesModule.Instance).Build();

    static bool ShapesMatch(string pattern, string source, IReadOnlyDictionary<string, QuoteHole>? captures = null)
    {
        using var compiled = Shapes.CompilePattern("Shapes.Decl", pattern, captures ?? new Dictionary<string, QuoteHole>());
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        using var input = Shapes.ParseTemplate(source, "Shapes.Decl", new Dictionary<string, string>());
        Assert.True(input.Success);
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.Empty(result.Diagnostics);
        return result.Matched;
    }

    [Theory]
    [InlineData("decl a { }", "decl a { }", true)]
    [InlineData("decl a { }", "decl a : int { }", false)]
    [InlineData("decl a { }", "decl a { b }", false)]
    [InlineData("decl a { }", "public decl a { }", false)]
    [InlineData("decl a : int { }", "decl a { }", false)]
    [InlineData("public decl a { b }", "decl a { }", false)]
    [InlineData("public decl a : int { b }", "public decl a : int { b }", true)]
    public void A_null_slot_matches_only_a_null_slot(string pattern, string source, bool expected) =>
        Assert.Equal(expected, ShapesMatch(pattern, source));

    [Fact]
    public void A_scalar_capture_in_an_optional_does_not_match_an_absent_one() =>
        Assert.False(ShapesMatch("decl a : $T { }", "decl a { }",
            new Dictionary<string, QuoteHole> { ["T"] = QuoteHole.Node("Shapes.TypeName") }));

    [Theory]
    [InlineData("decl a { }", 0)]
    [InlineData("public decl a { }", 1)]
    [InlineData("public static decl a { }", 2)]
    public void A_list_capture_matches_an_empty_list_as_no_items(string source, int count)
    {
        using var compiled = Shapes.CompilePattern("Shapes.Decl", "..$Mods decl a { }",
            new Dictionary<string, QuoteHole> { ["Mods"] = QuoteHole.List("Shapes.Mod") });
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        using var input = Shapes.ParseTemplate(source, "Shapes.Decl", new Dictionary<string, string>());
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.True(result.Matched);
        Assert.Equal(count, Assert.IsType<QuoteArgument.Many>(result.Captures["Mods"]).Values.Count);
    }

    [Fact]
    public void Instantiating_an_empty_list_capture_makes_the_null_slot_a_parse_makes()
    {
        var holes = new Dictionary<string, QuoteHole> { ["Mods"] = QuoteHole.List("Shapes.Mod") };
        using var quote = Shapes.CompileQuote("Shapes.Decl", "..$Mods decl a { }", holes);
        Assert.True(quote.Success, string.Join("; ", quote.Diagnostics.Select(d => d.Message)));
        using var built = quote.Template!.Instantiate(new Dictionary<string, QuoteArgument>
        {
            ["Mods"] = QuoteArgument.List([]),
        });
        Assert.True(built.Success, string.Join("; ", built.Diagnostics.Select(d => d.Message)));
        using var parsed = Shapes.ParseTemplate("decl a { }", "Shapes.Decl", new Dictionary<string, string>());
        Assert.Equal(SyntaxDumper.Dump(parsed.Tree), SyntaxDumper.Dump(built.Tree!));
        Assert.Equal(parsed.Tree.NodeCount, built.Tree!.NodeCount);
        Assert.Equal(SyntaxTree.NoNode, built.Tree.Child(built.Tree.Root, 0));
        // The constructed tree matches a pattern written with null slots.
        using var pattern = Shapes.CompilePattern("Shapes.Decl", "decl a { }", new Dictionary<string, QuoteHole>());
        Assert.True(pattern.Pattern!.Match(new(built.Tree, built.Tree.Root)).Matched);
    }

    static readonly Language CalcLanguage = new LanguageBuilder().Add(Nitrogen.Tests.Calc.CalcModule.Instance).Build();

    static string[]? CalcCaptures(string pattern, string source)
    {
        using var compiled = CalcLanguage.CompilePattern("Calc.Expr", pattern,
            new Dictionary<string, QuoteHole> { ["A"] = QuoteHole.List("Calc.Expr") });
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        using var input = CalcLanguage.ParseTemplate(source, "Calc.Expr", new Dictionary<string, string>());
        Assert.True(input.Success);
        var result = compiled.Pattern!.Match(new(input.Tree, input.Tree.Root));
        Assert.Empty(result.Diagnostics);
        return result.Matched
            ? Assert.IsType<QuoteArgument.Many>(result.Captures["A"]).Values.Select(v => v.Tree.GetText(v.Node).ToString()).ToArray()
            : null;
    }

    // A lone list hole is its pattern's one-item list; the input list is collapsed with one item.
    [Theory]
    [InlineData("f(..$A)", "f(1)", "1")]
    [InlineData("f(..$A)", "f(1, 2)", "1|2")]
    [InlineData("f(..$A)", "f()", "")]
    [InlineData("f(1, ..$A)", "f(1)", "")]
    [InlineData("f(1, ..$A)", "f(1, 2)", "2")]
    [InlineData("f(1, ..$A)", "f(1, 2, 3)", "2|3")]
    [InlineData("f(..$A, 2)", "f(2)", "")]
    [InlineData("f(..$A, 2)", "f(1, 2)", "1")]
    [InlineData("f(1, ..$A)", "f(2)", null)]
    [InlineData("f(1, ..$A)", "f()", null)]
    public void A_list_hole_matches_one_item_and_two_item_lists(string pattern, string source, string? captured) =>
        Assert.Equal(captured?.Split('|', StringSplitOptions.RemoveEmptyEntries), CalcCaptures(pattern, source));

    [Theory]
    [InlineData("public decl a { }", "public decl a { }", true)]
    [InlineData("public decl a { }", "public static decl a { }", false)]
    [InlineData("public static decl a { }", "public decl a { }", false)]
    [InlineData("decl a { b }", "decl a { b }", true)]
    [InlineData("decl a { b }", "decl a { b, c }", false)]
    [InlineData("decl a { b, c }", "decl a { b }", false)]
    public void A_one_item_list_matches_only_a_one_item_list(string pattern, string source, bool expected) =>
        Assert.Equal(expected, ShapesMatch(pattern, source));

    [Theory]
    [InlineData("public decl a { }", 1)]
    [InlineData("public static decl a { }", 2)]
    public void Instantiating_a_list_capture_makes_the_list_shape_a_parse_makes(string source, int count)
    {
        var holes = new Dictionary<string, QuoteHole> { ["Mods"] = QuoteHole.List("Shapes.Mod") };
        using var parsed = Shapes.ParseTemplate(source, "Shapes.Decl", new Dictionary<string, string>());
        var view = SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(parsed.Tree, parsed.Tree.Root);
        var mods = new List<QuoteNode>();
        foreach (var mod in view.Mods) mods.Add(new(parsed.Tree, mod.Index));
        Assert.Equal(count, mods.Count);
        using var quote = Shapes.CompileQuote("Shapes.Decl", "..$Mods decl a { }", holes);
        Assert.True(quote.Success, string.Join("; ", quote.Diagnostics.Select(d => d.Message)));
        using var built = quote.Template!.Instantiate(new Dictionary<string, QuoteArgument> { ["Mods"] = QuoteArgument.List(mods) });
        Assert.True(built.Success, string.Join("; ", built.Diagnostics.Select(d => d.Message)));
        Assert.Equal(SyntaxDumper.Dump(parsed.Tree), SyntaxDumper.Dump(built.Tree!));
        Assert.Equal(parsed.Tree.NodeCount, built.Tree!.NodeCount);
        // A template list with a fixed item and a hole also collapses when the hole takes nothing.
        using var fixedQuote = Shapes.CompileQuote("Shapes.Decl", "public ..$Mods decl a { }", holes);
        Assert.True(fixedQuote.Success, string.Join("; ", fixedQuote.Diagnostics.Select(d => d.Message)));
        using var one = fixedQuote.Template!.Instantiate(new Dictionary<string, QuoteArgument> { ["Mods"] = QuoteArgument.List([]) });
        using var reparsed = Shapes.ParseTemplate("public decl a { }", "Shapes.Decl", new Dictionary<string, string>());
        Assert.Equal(SyntaxDumper.Dump(reparsed.Tree), SyntaxDumper.Dump(one.Tree!));
    }
}
