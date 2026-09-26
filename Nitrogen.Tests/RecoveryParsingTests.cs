using Nitrogen.Tests.Recovery;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Generated recovery (issue 235) on Recovery.ngr, end to end.</summary>
public class RecoveryParsingTests
{
    static readonly Language Recovery = new LanguageBuilder().Add(RecoveryModule.Instance).Build();

    static ParseResult Parse(string text) => Recovery.Parse(text, RecoveryModule.File);

    static string[] Messages(ParseResult result) =>
        result.Diagnostics.ToArray().Select(d => result.FormatMessage(d) + " " + d.Span).ToArray();

    static string Item(string name, string fields) => $"(Item \"item\" Name:\"{name}\" \"{{\" {fields} \"}}\")";

    const string X1 = "(Field Name:\"x\" \"=\" Number:\"1\" \";\")";

    [Fact]
    public void A_missing_semicolon_is_inserted_before_the_closing_brace()
    {
        using var result = Parse("item a { x = 1 }");
        Assert.Equal($"(File (List {Item("a", "(List (Field Name:\"x\" \"=\" Number:\"1\" !Literal))")}))", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { "expected ';' [15..15)" }, Messages(result));
    }

    [Fact]
    public void Garbage_before_the_semicolon_is_skipped()
    {
        using var result = Parse("item a { x = 1 ?? ; }");
        Assert.Equal($"(File (List {Item("a", "(List (Field~ Name:\"x\" \"=\" Number:\"1\" \";\"))")}))", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(15, 2) }, result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
    }

    [Fact]
    public void A_skip_resumes_at_the_value_when_the_equals_sign_is_missing()
    {
        using var result = Parse("item a { x ?? 1 ; }");
        Assert.Equal($"(File (List {Item("a", "(List (Field~ Name:\"x\" !Literal Number:\"1\" \";\"))")}))", SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void A_bad_item_costs_only_itself_and_the_list_goes_on()
    {
        using var result = Parse("item a { x = 1 ; ?? y = 2 ; }");
        Assert.Equal(
            $"(File (List {Item("a", $"(List~ {X1} (Field Name:\"y\" \"=\" Number:\"2\" \";\"))")}))",
            SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(17, 2) }, result.Tree.SkippedSpans.ToArray());
        Assert.Single(result.Diagnostics.ToArray());
    }

    [Fact]
    public void Balanced_brackets_are_skipped_whole()
    {
        using var result = Parse("item a { x = 1 (y ; z) ; }");
        Assert.Equal($"(File (List {Item("a", "(List (Field~ Name:\"x\" \"=\" Number:\"1\" \";\"))")}))", SyntaxDumper.Dump(result.Tree));
        Assert.Equal(new[] { new TextSpan(15, 7) }, result.Tree.SkippedSpans.ToArray());
    }

    [Fact]
    public void An_enclosing_closer_stops_the_skip_and_the_next_item_parses_clean()
    {
        using var result = Parse("item a { x = 1 ?? } item b { }");
        Assert.Equal(
            $"(File (List {Item("a", "(List (Field~ Name:\"x\" \"=\" Number:\"1\" !Literal))")} {Item("b", "(List)")}))",
            SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void Everything_missing_at_the_end_of_input_is_one_diagnostic()
    {
        using var result = Parse("item a { x = 1");
        Assert.Equal(
            "(File (List (Item \"item\" Name:\"a\" \"{\" (List (Field Name:\"x\" \"=\" Number:\"1\" !Literal)) !Literal)))",
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
        Assert.Equal($"(File (List {Item("a", "(List)")}))", SyntaxDumper.Dump(result.Tree));
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
}
