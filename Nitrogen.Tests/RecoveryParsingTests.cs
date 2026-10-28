using Nitrogen.Tests.Recovery;
using ShapesModule = Nitrogen.Tests.Shapes.ShapesModule;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Generated recovery (issue 235) on Recovery.ngr, end to end.</summary>
public unsafe class RecoveryParsingTests
{
    static readonly Language Recovery = new LanguageBuilder().Add(RecoveryModule.Instance).Build();

    static ParseResult Parse(string text) => Recovery.Parse(text, RecoveryModule.File);

    static string[] Messages(ParseResult result) =>
        result.Diagnostics.ToArray().Select(d => result.FormatMessage(d) + " " + d.Span).ToArray();

    static string Item(string name, string fields) => $"(Item \"item\" Name:\"{name}\" \"{{\" {fields} \"}}\")";

    const string X1 = "(Field Name:\"x\" \"=\" Number:\"1\" \";\")";

    [Theory]
    [InlineData("item a a { x = 1; y = 2; } item b { z = 3; }")]
    [InlineData("item a a /* comment */ { x = 1; y = 2; } item b { z = 3; }")]
    public void A_duplicate_header_name_does_not_consume_the_body(string text)
    {
        using var result = Parse(text);
        var file = SyntaxView.Cast<FileNode>(result.Tree, result.Tree.Root);
        Assert.Equal(2, file.Items.Count);
        Assert.Equal(2, file.Items[0].Fields.Count);
        Assert.Equal("x", file.Items[0].Fields[0].Key.Text.ToString());
        Assert.Equal("y", file.Items[0].Fields[1].Key.Text.ToString());
        Assert.Equal("z", file.Items[1].Fields[0].Key.Text.ToString());
        Assert.Equal(new[] { new TextSpan(7, 1) }, result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
    }

    [Fact]
    public void A_repeated_name_that_starts_a_field_is_not_deleted()
    {
        using var result = Parse("item a a = 1; }");
        var file = SyntaxView.Cast<FileNode>(result.Tree, result.Tree.Root);
        Assert.Equal(1, file.Items.Count);
        Assert.Equal(1, file.Items[0].Fields.Count);
        Assert.Equal("a", file.Items[0].Fields[0].Key.Text.ToString());
        Assert.Empty(result.Tree.SkippedSpans.ToArray());
        Assert.True(new SyntaxNode(result.Tree, result.Tree.Child(file.Items[0].Index, 2)).IsMissing);
    }

    [Theory]
    [InlineData("item a { x = 1; item b { y = 2; }")]
    [InlineData("item a { item b { y = 2; }")]
    public void Missing_closing_brace_preserves_the_next_declaration(string text)
    {
        using var result = Parse(text);
        var file = SyntaxView.Cast<FileNode>(result.Tree, result.Tree.Root);
        Assert.Equal(2, file.Items.Count);
        Assert.Equal("b", file.Items[1].Name.Text.ToString());
        Assert.Equal("y", file.Items[1].Fields[0].Key.Text.ToString());
        Assert.Empty(result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
        Assert.Contains("'}'", result.FormatMessage(result.Diagnostics[0]));
        Assert.True(new SyntaxNode(result.Tree, result.Tree.Child(file.Items[0].Index, 4)).IsMissing);
    }

    [Fact]
    public void A_malformed_following_declaration_reports_its_missing_name()
    {
        using var result = Parse("item a { x = 1; y = 2; item } b { z = 3; }");
        var file = SyntaxView.Cast<FileNode>(result.Tree, result.Tree.Root);
        Assert.Equal(2, file.Items.Count);
        Assert.True(file.Items[1].Name.IsMissing);
        Assert.Contains(result.Diagnostics.ToArray(), diagnostic => result.FormatMessage(diagnostic) == "expected Name");
    }

    static readonly Rule WrappedFile = new("WrappedFile", &ParseWrappedFile);

    static bool ParseWrappedFile(ref ParserState s)
    {
        var mark = s.Mark();
        s.Open(SyntaxKinds.Group);
        if (s.MatchKeyword("outer"))
        {
            s.SkipTrivia();
            var parse = RecoveryModule.File.Parse;
            if (parse(ref s))
            {
                s.SkipTrivia();
                if (s.MatchKeyword("end")) { s.Close(); return true; }
            }
        }
        s.Reset(mark);
        return false;
    }

    [Fact]
    public void Root_list_reentry_is_disabled_when_the_rule_is_called_inside_another_rule()
    {
        using var valid = Recovery.Parse("outer item a { } end", WrappedFile);
        Assert.True(valid.Success);
        using var nested = Recovery.Parse("outer item a { } ?? item b { } end", WrappedFile);
        Assert.Equal(SyntaxKinds.Error, nested.Tree.Kind(nested.Tree.Root));
    }

    [Theory]
    [InlineData("?? item a { } item b { }")]
    [InlineData("item a { } ?? item b { }")]
    [InlineData("item a { } (junk { nested }) item b { }")]
    public void Damage_in_a_root_list_preserves_later_declarations(string text)
    {
        using var result = Parse(text);
        var file = SyntaxView.Cast<FileNode>(result.Tree, result.Tree.Root);
        Assert.Equal(2, file.Items.Count);
        Assert.Equal("a", file.Items[0].Name.Text.ToString());
        Assert.Equal("b", file.Items[1].Name.Text.ToString());
        Assert.Single(result.Diagnostics.ToArray());
        Assert.Single(result.Tree.SkippedSpans.ToArray());
    }

    [Fact]
    public void A_missing_semicolon_is_inserted_before_the_closing_brace()
    {
        using var result = Parse("item a { x = 1 }");
        Assert.Equal($"(File {Item("a", "(Field Name:\"x\" \"=\" Number:\"1\" !Literal)")})", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected ';' [15..15)" }, Messages(result));
    }

    [Fact]
    public void Garbage_before_the_semicolon_is_skipped()
    {
        using var result = Parse("item a { x = 1 ?? ; }");
        Assert.Equal($"(File {Item("a", "(Field~ Name:\"x\" \"=\" Number:\"1\" \";\")")})", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(15, 2) }, result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
    }

    [Fact]
    public void A_skip_resumes_at_the_value_when_the_equals_sign_is_missing()
    {
        using var result = Parse("item a { x ?? 1 ; }");
        Assert.Equal($"(File {Item("a", "(Field~ Name:\"x\" !Literal Number:\"1\" \";\")")})", SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void A_bad_item_costs_only_itself_and_the_list_goes_on()
    {
        using var result = Parse("item a { x = 1 ; ?? y = 2 ; }");
        Assert.Equal(
            $"(File {Item("a", $"(List~ {X1} (Field Name:\"y\" \"=\" Number:\"2\" \";\"))")})",
            SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(17, 2) }, result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
    }

    [Fact]
    public void Balanced_brackets_are_skipped_whole()
    {
        using var result = Parse("item a { x = 1 (y ; z) ; }");
        Assert.Equal($"(File {Item("a", "(Field~ Name:\"x\" \"=\" Number:\"1\" \";\")")})", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(15, 7) }, result.Tree.SkippedSpans.ToArray());
    }

    [Fact]
    public void An_enclosing_closer_stops_the_skip_and_the_next_item_parses_clean()
    {
        using var result = Parse("item a { x = 1 ?? } item b { }");
        Assert.Equal(
            $"(File (List {Item("a", "(Field~ Name:\"x\" \"=\" Number:\"1\" !Literal)")} {Item("b", "_")}))",
            SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void Everything_missing_at_the_end_of_input_is_one_diagnostic()
    {
        using var result = Parse("item a { x = 1");
        Assert.Equal(
            "(File (Item \"item\" Name:\"a\" \"{\" (Field Name:\"x\" \"=\" Number:\"1\" !Literal) !Literal))",
            SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected ';' [14..14)" }, Messages(result));
    }

    [Fact]
    public void Two_errors_are_two_diagnostics()
    {
        //                    0         1         2         3
        //                    0123456789012345678901234567890123
        using var result = Parse("item a { x ?? ; } item b { y = ; }");
        Assert.Equal(new[] { "expected '=' [11..11)", "expected Number [31..31)" }, Messages(result));
        Assert.Contains("(Field Name:\"y\" \"=\" !Number \";\")", SyntaxDumper.Dump(result.Tree));
        Assert.Contains("(Field~ Name:\"x\" !Literal !Number \";\")", SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void Trailing_input_is_skipped_trivia()
    {
        using var result = Parse("item a { } junk");
        Assert.Equal($"(File {Item("a", "_")})", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(11, 4) }, result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
    }

    [Fact]
    public void Repairs_stop_at_the_cap_and_the_rest_is_skipped()
    {
        string text = "item a { " + string.Concat(Enumerable.Repeat("x ?? ; ", 150)) + "}";
        using var result = Parse(text);
        Assert.InRange(result.Diagnostics.Length, 95, 102);
        Assert.Equal(text.Length, result.Tree.SkippedSpans[^1].End);
    }

    [Fact]
    public void Views_report_missing_nodes()
    {
        using var result = Parse("item a { x = 1 }");
        var file = SyntaxView.Cast<FileNode>(result.Tree, result.Tree.Root);
        var field = file.Items[0].Fields[0];
        Assert.False(field.Value.IsMissing);
        Assert.True(new SyntaxNode(result.Tree, result.Tree.Child(field.Index, 3)).IsMissing);
        Assert.False(field.IsMissing);
    }

    static readonly Language Shapes = new LanguageBuilder().Add(ShapesModule.Instance).Build();

    [Fact]
    public void A_repair_passes_over_an_absent_optional_as_a_null_slot()
    {
        // The missing Name resumes at "{": the optional Type between is pushed as a null slot.
        using var result = Shapes.Parse("decl { }", ShapesModule.File);
        Assert.Equal("(File (Decl _ \"decl\" !Name _ \"{\" _ \"}\"))", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected Name [5..5)" }, Messages(result));
        int decl = result.Tree.Child(0, 0);
        Assert.Equal(SyntaxTree.NoNode, result.Tree.Child(decl, 3));
        Assert.False(SyntaxView.Cast<Nitrogen.Tests.Shapes.DeclNode>(result.Tree, decl).Type.HasValue);
    }

    [Fact]
    public void A_repair_after_the_last_element_with_width_keeps_null_slots_out_of_the_span()
    {
        using var result = Shapes.Parse("decl a", ShapesModule.File);
        Assert.Equal("(File (Decl _ \"decl\" Name:\"a\" _ !Literal _ !Literal))", SyntaxDumper.Dump(result.Tree));
        int decl = result.Tree.Child(0, 0);
        Assert.Equal(new TextSpan(0, 6), result.Tree.Span(decl));
    }

    [Fact]
    public void A_missing_leaf_alias_stands_in_as_a_literal()
    {
        // TypeName is a leaf-choice alias: its Missing node is a Literal, as for a missing keyword.
        using var result = Shapes.Parse("decl a : { }", ShapesModule.File);
        Assert.Equal("(File (Decl _ \"decl\" Name:\"a\" (Group \":\" !Literal) \"{\" _ \"}\"))", SyntaxDumper.Dump(result.Tree));
        Assert.Single(Messages(result));
    }

    [Theory]
    [InlineData("decl a { }")]
    [InlineData("public static decl a : int { b, c } decl d { }")]
    [InlineData("public decl a { b }")]
    [InlineData("")] // File's one slot is null: a node with only null slots
    [InlineData("  // c\n ")]
    public void Valid_input_with_null_slots_parses_the_same_in_the_recovery_pass(string text)
    {
        using var clean = Shapes.Parse(text, ShapesModule.File);
        using var recovering = Shapes.ParseRecovering(text, ShapesModule.File);
        Assert.True(recovering.Success);
        Assert.Equal(SyntaxDumper.Dump(clean.Tree), SyntaxDumper.Dump(recovering.Tree));
        Assert.Equal(clean.Tree.NodeCount, recovering.Tree.NodeCount);
        // Spans too, including a node whose children are all null slots (it has no child to take a span from).
        Assert.Equal(Enumerable.Range(0, clean.Tree.NodeCount).Select(clean.Tree.Span),
            Enumerable.Range(0, recovering.Tree.NodeCount).Select(recovering.Tree.Span));
    }
}
