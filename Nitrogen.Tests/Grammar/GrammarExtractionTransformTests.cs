using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarExtractionTransformTests
{
    const string Uri = "file:///a.ngr";
    const string Destination = "file:///aa.ngr";
    static ExtractionTransform Move(string text, string selected, params ExtractionSource[] others)
    {
        var source = new ExtractionSource(Uri, text);
        var selection = GrammarExtraction.Select(source, new(text.IndexOf(selected, StringComparison.Ordinal), selected.Length));
        Assert.Empty(selection.Problems);
        return GrammarExtraction.Transform(new[] { source }.Concat(others).ToArray(), selection.Selection!, Destination, "Z");
    }
    static string Apply(string text, IEnumerable<ExtractionTextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Span.Start))
            text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        return text;
    }
    [Fact]
    public void Moves_a_rule_and_updates_consumers_without_touching_literals_or_comments()
    {
        const string rule = "syntax Word = \"Word\";";
        string text = "syntax module A { " + rule + " syntax Program = Word; // Word\n }";
        var result = Move(text, rule);
        Assert.Empty(result.Problems);
        Assert.Contains(rule, result.DestinationText);
        string remaining = Apply(text, result.Edits.Where(e => e.Uri == Uri));
        Assert.Contains("syntax Program = Z.Word; // Word", remaining);
        Assert.DoesNotContain("syntax Word =", remaining);
        Assert.False(GrammarCompiler.Compile(new[] { new GrammarInput("a.ngr", remaining, "Test"), new("z.ngr", result.DestinationText, "Test") }).HasErrors);
    }
    [Fact]
    public void Qualifies_retained_dependencies_and_updates_external_consumers_by_identity()
    {
        const string rule = "syntax Word = Number;";
        string text = "syntax module A { token Number = ['0'..'9']+; " + rule + " }";
        var result = Move(text, rule, new ExtractionSource("file:///b.ngr", "syntax module B { using A; syntax Program = Word; syntax Literal = \"Word\"; }"),
            new ExtractionSource("file:///other.ngr", "syntax module Other { syntax Word = \"other\"; syntax Program = Word; }"));
        Assert.Empty(result.Problems);
        Assert.Contains("syntax Word = A.Number;", result.DestinationText);
        Assert.Contains(result.Edits, e => e.Uri == "file:///b.ngr" && e.NewText == "Z.Word");
        Assert.DoesNotContain(result.Edits, e => e.Uri == "file:///other.ngr");
    }
    [Fact]
    public void Moves_an_alternative_to_an_extension_and_retains_shared_properties()
    {
        const string alternative = "| Num = Value:Number { Type = 1; }";
        string text = "syntax module A { token Number = ['0'..'9']+; extensible syntax Expr { out Type : int = 0; " + alternative + " } }";
        var result = Move(text, alternative);
        Assert.Empty(result.Problems);
        Assert.Contains("extend syntax A.Expr", result.DestinationText);
        Assert.Contains("Value:A.Number", result.DestinationText);
        Assert.Contains("out Type : int = 0;", Apply(text, result.Edits));
        Assert.DoesNotContain("out Type", result.DestinationText);
    }
    [Fact]
    public void Updates_extension_targets_when_the_whole_rule_moves()
    {
        const string rule = "extensible syntax Expr { | Num = \"1\" }";
        var result = Move("syntax module A { " + rule + " }", rule,
            new ExtractionSource("file:///b.ngr", "syntax module B { extend syntax A.Expr { | Extra = \"2\" } }"));
        Assert.Empty(result.Problems);
        Assert.Contains(result.Edits, e => e.Uri == "file:///b.ngr" && e.NewText == "Z.Expr");
    }
    [Fact]
    public void Refuses_a_move_that_changes_a_rewrite_category()
    {
        const string rule = "syntax Sugar = \"s\" Value:Num rewrites <[ $Value ]>;";
        var result = Move("syntax module A { token Number = ['0'..'9']+; syntax Num = Number; " + rule + " syntax Expr = Num / Sugar; }", rule);
        Assert.Contains(result.Problems, p => p.Message.Contains("quotation category"));
        Assert.Empty(result.Edits);
    }
    [Fact]
    public void Resolves_an_imported_unqualified_extension_target()
    {
        const string alternative = "| Extra = \"2\"";
        var result = Move("syntax module B { using A; extend syntax Expr { " + alternative + " } }", alternative,
            new ExtractionSource("file:///b.ngr", "syntax module A { extensible syntax Expr { | Num = \"1\" } }"));
        Assert.Empty(result.Problems);
        Assert.Contains("extend syntax A.Expr", result.DestinationText);
    }
    [Fact]
    public void Refuses_a_duplicate_module_name()
    {
        var result = Move("syntax module A { syntax Word = \"x\"; }", "syntax Word = \"x\";", new ExtractionSource("file:///b.ngr", "syntax module Z { }"));
        Assert.NotEmpty(result.Problems);
        Assert.Empty(result.Edits);
    }
    [Fact]
    public void Refuses_extraction_that_reorders_alternatives()
    {
        var result = Move("syntax module A { extensible syntax Expr { | First = \"1\" | Second = \"2\" } }", "| First = \"1\"");
        Assert.Contains(result.Problems, p => p.Message.Contains("order"));
        Assert.Empty(result.Edits);
    }
}
