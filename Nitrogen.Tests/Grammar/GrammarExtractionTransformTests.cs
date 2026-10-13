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
        Assert.Contains("using Z;", remaining);
        Assert.Contains("syntax Program = Word; // Word", remaining);
        Assert.DoesNotContain("syntax Word =", remaining);
        Assert.False(GrammarCompiler.Compile(new[] { new GrammarInput("a.ngr", remaining, "Test"), new("z.ngr", result.DestinationText, "Test") }).HasErrors);
    }
    [Fact]
    public void Imports_retained_dependencies_and_updates_external_consumers_by_identity()
    {
        const string rule = "syntax Word = Number;";
        string text = "syntax module A { token Number = ['0'..'9']+; " + rule + " }";
        var result = Move(text, rule, new ExtractionSource("file:///b.ngr", "syntax module B { using A; syntax Program = Word; syntax Literal = \"Word\"; }"),
            new ExtractionSource("file:///other.ngr", "syntax module Other { syntax Word = \"other\"; syntax Program = Word; }"));
        Assert.Empty(result.Problems);
        Assert.Contains("syntax Word = Number;", result.DestinationText);
        Assert.Contains(result.Edits, e => e.Uri == "file:///b.ngr" && e.NewText.Contains("using Z;"));
        Assert.DoesNotContain(result.Edits, e => e.Uri == "file:///other.ngr");
    }
    [Fact]
    public void Moves_an_alternative_to_an_extension_and_retains_shared_properties()
    {
        const string alternative = "| Num = Value:Number { Type = 1; }";
        string text = "syntax module A { token Number = ['0'..'9']+; extensible syntax Expr { out Type : int = 0; " + alternative + " } }";
        var result = Move(text, alternative);
        Assert.Empty(result.Problems);
        Assert.Contains("extend syntax Expr", result.DestinationText);
        Assert.Contains("Value:Number", result.DestinationText);
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
        Assert.Contains(result.Edits, e => e.Uri == "file:///b.ngr" && e.NewText == "Expr");
        Assert.Contains(result.Edits, e => e.Uri == "file:///b.ngr" && e.NewText.Contains("using Z;"));
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
        Assert.Contains("extend syntax Expr", result.DestinationText);
    }
    [Fact]
    public void Adding_an_import_preserves_other_imported_referents()
    {
        const string rule = "syntax Word = \"x\";";
        const string consumer = "syntax module B { using Other; syntax Program = Word A.Word; }";
        var result = Move("syntax module A { " + rule + " }", rule,
            new ExtractionSource("file:///b.ngr", consumer), new ExtractionSource("file:///other.ngr", "syntax module Other { syntax Word = \"y\"; }"));
        Assert.Empty(result.Problems);
        string updated = Apply(consumer, result.Edits.Where(e => e.Uri == "file:///b.ngr"));
        Assert.Contains("using Z;", updated);
        Assert.Contains("syntax Program = Other.Word Z.Word;", updated);
    }
    [Fact]
    public void A_local_name_keeps_its_binding_and_the_moved_name_stays_qualified()
    {
        const string rule = "syntax Word = \"x\";";
        const string consumer = "syntax module B { syntax Word = \"local\"; syntax Program = A.Word Word; }";
        var result = Move("syntax module A { " + rule + " }", rule, new ExtractionSource("file:///b.ngr", consumer));
        Assert.Empty(result.Problems);
        Assert.Contains("syntax Program = Z.Word Word;", Apply(consumer, result.Edits.Where(e => e.Uri == "file:///b.ngr")));
    }
    [Fact]
    public void A_moved_dependency_stays_qualified_when_its_imports_conflict()
    {
        const string rule = "syntax Word = Number;";
        var result = Move("syntax module A { using Other; token Number = \"n\"; " + rule + " }", rule,
            new ExtractionSource("file:///other.ngr", "syntax module Other { token Number = \"o\"; }"));
        Assert.Empty(result.Problems);
        Assert.Contains("syntax Word = A.Number;", result.DestinationText);
    }
    [Fact]
    public void Moving_multiple_rules_keeps_internal_and_external_names_unqualified()
    {
        const string rules = "token Number = ['0'..'9']+; syntax Word = Number;";
        string text = "syntax module A { " + rules + " syntax Program = Word; }";
        var result = Move(text, rules);
        Assert.Empty(result.Problems);
        Assert.Contains("syntax Word = Number;", result.DestinationText);
        string remaining = Apply(text, result.Edits.Where(e => e.Uri == Uri));
        Assert.Contains("using Z;", remaining);
        Assert.Contains("syntax Program = Word;", remaining);
    }
    [Fact]
    public void An_import_uses_real_header_tokens_and_preserves_crlf()
    {
        const string selected = "\r\n  syntax Word = \"x\";";
        string text = "syntax module A /* { header comment */ {" + selected + "\r\n  syntax Program = Word;\r\n}";
        var result = Move(text, selected);
        Assert.Empty(result.Problems);
        string remaining = Apply(text, result.Edits.Where(e => e.Uri == Uri));
        Assert.Contains("/* { header comment */ {\r\n  using Z;", remaining);
        Assert.Contains("syntax Program = Word;", remaining);
        Assert.DoesNotContain("\n", remaining.Replace("\r\n", ""));
        Assert.Single(result.Edits, e => e.Uri == Uri && e.Span.Start == text.IndexOf(selected, StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("except")]
    [InlineData("precedence")]
    [InlineData("declares")]
    [InlineData("references")]
    [InlineData("scope")]
    [InlineData("dynamic")]
    [InlineData("lowers")]
    [InlineData("rewrites")]
    public void Short_names_cannot_turn_references_into_grammar_keywords(string name)
    {
        string rule = "token " + name + " = \"e\";";
        string text = "syntax module A { " + rule + " token Program = \"p\" A." + name + " \"x\"; }";
        var result = Move(text, rule);
        Assert.Empty(result.Problems);
        string remaining = Apply(text, result.Edits.Where(e => e.Uri == Uri));
        Assert.Contains("token Program = \"p\" Z." + name + " \"x\";", remaining);
        var program = GrammarParser.Parse(remaining).File!.Modules[0].Rules.OfType<TokenRule>().Single();
        Assert.Equal(3, Assert.IsType<SequenceExpr>(program.Body).Items.Count);
        Assert.Empty(program.Except);
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
