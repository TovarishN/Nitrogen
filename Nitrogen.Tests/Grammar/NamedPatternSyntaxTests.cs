using Nitrogen.Grammar;
using Nitrogen.Ngr;
using Xunit;

namespace Nitrogen.Tests;

public sealed class NamedPatternSyntaxTests
{
    const string Prefix = """
        syntax module X {
          token Name = ['a'..'z']+;
          syntax Doc = Items:Item*;
        """;

    static GrammarFile Parse(string body)
    {
        string grammar = Prefix + body + "\n}\n";
        var bootstrap = GrammarParser.Parse(grammar);
        var selfHosted = NgrParser.Parse(grammar);
        Assert.Empty(bootstrap.Diagnostics);
        Assert.Empty(selfHosted.Diagnostics);
        Assert.Equal(bootstrap.File, selfHosted.File);
        return bootstrap.File!;
    }

    static MatchDiagnosticStatement Diagnostic(GrammarFile file) =>
        file.Modules[0].Rules.OfType<SyntaxRule>().Single(rule => rule.Name == "Item").Semantics!.Statements
            .OfType<MatchDiagnosticStatement>().Single();

    [Fact]
    public void Pattern_declaration_with_alternatives()
    {
        var file = Parse("""
              syntax Item = "<" Open:Name ">";
              pattern Secret : Item =
                | <[ <password> ]>
                | <[ <secret> ]>
                | (v: Item) <[ $v ]>;
            """);
        var pattern = Assert.Single(file.Modules[0].Patterns);
        Assert.Equal("Secret", pattern.Name.Name);
        Assert.Equal("Item", pattern.Category.Name);
        Assert.Equal(3, pattern.Alternatives.Count);
        Assert.Equal(" <password> ", pattern.Alternatives[0].Pattern.Text);
        Assert.Equal("v", Assert.Single(pattern.Alternatives[2].Captures).Name.Name);
    }

    [Fact]
    public void Pattern_declaration_without_leading_bar()
    {
        var file = Parse("""
              syntax Item = "<" Open:Name ">";
              pattern One : Item = <[ <a> ]>;
            """);
        Assert.Single(Assert.Single(file.Modules[0].Patterns).Alternatives);
    }

    [Fact]
    public void Descendant_capture_declaration()
    {
        var file = Parse("""
              syntax Item = "<" Open:Name ">" {
                match (s: ..Secret) <[ <a> ]> => warning XP1001 : "m";
              }
            """);
        var capture = Assert.Single(Diagnostic(file).Captures);
        Assert.True(capture.IsDescendant);
        Assert.Equal("Secret", capture.Category.Name);
        Assert.Null(Diagnostic(file).Context);
    }

    [Fact]
    public void Inside_clause()
    {
        var file = Parse("""
              syntax Item = "<" Open:Name ">" {
                match <[ <a> ]> inside <[ <b> ]> => warning XP1001 : "m";
              }
            """);
        var context = Diagnostic(file).Context!;
        Assert.False(context.ParentOnly);
        Assert.Empty(context.Captures);
        Assert.Equal(" <b> ", context.Pattern.Text);
    }

    [Fact]
    public void Parent_clause_with_captures()
    {
        var file = Parse("""
              syntax Item = "<" Open:Name ">" {
                match (a: Item) <[ $a ]> parent (n: Item) <[ $n ]> => warning XP1001 : "m";
              }
            """);
        var context = Diagnostic(file).Context!;
        Assert.True(context.ParentOnly);
        Assert.Equal("n", Assert.Single(context.Captures).Name.Name);
    }

    [Fact]
    public void Contextual_keywords_remain_identifiers()
    {
        var file = Parse("""
              syntax Item = "<" pattern:Name ">" {
                out inside: bool = false;
                out parent: int = 0;
                inside = true;
              }
            """);
        Assert.Empty(file.Modules[0].Patterns);
    }
}
