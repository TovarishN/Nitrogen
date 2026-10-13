using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarExtractionTests
{
    const string Uri = "file:///source.ngr";

    static ExtractionSelectionResult Select(string source, string selected) =>
        GrammarExtraction.Select(new(Uri, source), new(source.IndexOf(selected, StringComparison.Ordinal), selected.Length));

    [Fact]
    public void Selects_a_complete_rule()
    {
        const string rule = "syntax Word = \"x\";";
        var result = Select("syntax module A { " + rule + " }", rule);
        Assert.Empty(result.Problems);
        Assert.Equal("A", result.Selection!.Module);
        Assert.Equal(ExtractionShape.Rules, result.Selection.Shape);
        Assert.Single(result.Selection.Definitions);
    }

    [Fact]
    public void Rejects_partial_rules()
    {
        var result = Select("syntax module A { syntax Word = \"x\"; }", "Word");
        Assert.Null(result.Selection);
        Assert.Contains(result.Problems, p => p.Message.Contains("complete"));
    }

    [Fact]
    public void Selects_alternatives_with_their_extension_target()
    {
        const string alternative = "| Num = \"1\"";
        var result = Select("syntax module A { extensible syntax Expr { " + alternative + " } }", alternative);
        Assert.Empty(result.Problems);
        Assert.Equal(ExtractionShape.Alternatives, result.Selection!.Shape);
        Assert.Equal("A.Expr", result.Selection.ExtensionTarget);
    }

    [Fact]
    public void Selects_an_entire_extensible_rule_instead_of_its_alternatives()
    {
        const string rule = "extensible syntax Expr { | Num = \"1\" }";
        Assert.Equal(ExtractionShape.Rules, Select("syntax module A { " + rule + " }", rule).Selection!.Shape);
    }

    [Fact]
    public void Preserves_an_attached_comment_and_crlf()
    {
        const string rule = "syntax Word = \"x\";";
        string text = "syntax module A\r\n{\r\n  // Word documentation\r\n  " + rule + "\r\n}\r\n";
        var selection = Select(text, rule).Selection!;
        Assert.Equal("  // Word documentation\r\n  " + rule + "\r\n", text.Substring(selection.Removal.Start, selection.Removal.Length));
    }

    [Theory]
    [InlineData("syntax module A { syntax X = \"x\";", "syntax X = \"x\";")]
    [InlineData("syntax module A { syntax X = \"x\"; } syntax module B { syntax Y = \"y\"; }", "syntax X = \"x\"; } syntax module B { syntax Y = \"y\";")]
    public void Rejects_invalid_or_cross_module_selections(string text, string selected) => Assert.Null(Select(text, selected).Selection);
}
