using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class GrammarLexerTests
{
    static string Kinds(string text) =>
        string.Join(" ", GrammarLexer.Tokenize(text).Select(t => t.Kind == TokenKind.End ? "End" : $"{t.Kind}:{t.Value}"));

    [Fact]
    public void Tokens()
    {
        Assert.Equal(
            "Identifier:syntax Identifier:X Equals:= QualifiedName:a.b String:s LBracket:[ Char:a DotDot:.. Char:z RBracket:]" +
            " Dot:. Semicolon:; Bar:| Slash:/ Question:? Star:* Plus:+ Bang:! Amp:& Colon:: Caret:^ LParen:( RParen:) LBrace:{ RBrace:} Integer:25 End",
            Kinds("syntax X = a.b \"s\" ['a'..'z'] . ; | / ? * + ! & : ^ ( ) { } 25"));
    }

    [Fact]
    public void Positions()
    {
        var tokens = GrammarLexer.Tokenize("  ab \"x\"");
        Assert.Equal((2, 2), (tokens[0].Start, tokens[0].Length));
        Assert.Equal((5, 3), (tokens[1].Start, tokens[1].Length));
        Assert.Equal(8, tokens[2].Start);
    }

    [Fact]
    public void Escapes_are_decoded()
    {
        Assert.Equal("\n\t\\\"'A\0\r", GrammarLexer.Tokenize("\"\\n\\t\\\\\\\"\\'\\u0041\\0\\r\"")[0].Value);
        Assert.Equal("'", GrammarLexer.Tokenize("'\\''")[0].Value);
    }

    [Fact]
    public void Comments_are_skipped()
    {
        Assert.Equal("Identifier:a Identifier:b End", Kinds("a // x\n /* y\n z */ b"));
    }

    [Fact]
    public void Dotted_names_need_adjacent_dots()
    {
        Assert.Equal("Identifier:a Dot:. Identifier:b End", Kinds("a . b"));
        Assert.Equal("QualifiedName:A.B.C End", Kinds("A.B.C"));
        Assert.Equal("Identifier:a Dot:. End", Kinds("a."));
    }

    [Theory]
    [InlineData("\"abc", "unterminated literal", 0)]
    [InlineData("\"a\nb\"", "unterminated literal", 0)]
    [InlineData("'ab'", "a character literal must contain exactly one character", 0)]
    [InlineData("x \"\\q\"", "unknown escape '\\q'", 3)]
    [InlineData("\"\\u00\"", "\\u needs four hex digits", 1)]
    [InlineData("a #", "unexpected character '#'", 2)]
    [InlineData("a /* x", "unterminated comment", 2)]
    public void Errors(string text, string message, int start)
    {
        var error = Assert.Throws<GrammarSyntaxException>(() => GrammarLexer.Tokenize(text));
        Assert.Equal(message, error.Message);
        Assert.Equal(start, error.Span.Start);
    }
}
