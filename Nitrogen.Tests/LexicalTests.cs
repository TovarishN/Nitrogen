using Nitrogen.Tests.Lexical;
using Xunit;

namespace Nitrogen.Tests;

public class LexicalTests
{
    static readonly Language Lexical = new LanguageBuilder().Add(LexicalModule.Instance).Build();

    static string Dump(string text)
    {
        using var result = Lexical.Parse(text, LexicalModule.Document);
        Assert.True(result.Success, result.Success ? "" : result.FormatMessage(result.Diagnostics[0]));
        return SyntaxDumper.Dump(result.Tree);
    }

    [Theory]
    [InlineData("(* a *) x", 7)]
    [InlineData("(* a", -1)]
    public void Comment_uses_a_not_predicate_and_any_character(string text, int end) =>
        Assert.Equal(end, LexicalModule.MatchComment(text, 0));

    [Fact]
    public void Text_uses_a_negated_class_and_escapes()
    {
        Assert.Equal(6, LexicalModule.MatchText("\"a\\\"b\" x", 0));
        Assert.Equal(-1, LexicalModule.MatchText("\"open", 0));
    }

    [Fact]
    public void Tokens_with_multi_char_literals_lists_and_predicates()
    {
        Assert.Equal(4, LexicalModule.MatchHex("0x1fZ", 0));
        Assert.Equal(-1, LexicalModule.MatchHex("0x", 0));
        Assert.Equal(5, LexicalModule.MatchDigits("1_000_", 0));
        Assert.Equal(3, LexicalModule.MatchSigned("+12", 0));
        Assert.Equal(-1, LexicalModule.MatchSigned("12", 0));
        Assert.Equal(2, LexicalModule.MatchUnicode("àéa", 0));
        Assert.Equal(-1, LexicalModule.MatchUnicode("a", 0));
    }

    [Fact]
    public void Separated_list_of_alias_items()
    {
        Assert.Equal(
            "(Document (List Hex:\"0x1f\" \",\" Comment:\"(* c *)\" \",\" Text:\"\"s\"\" \",\" Signed:\"+3\" \",\"" +
            " Digits:\"1_0\" \",\" Word:\"word\" \",\" Word:\"picky\" \",\" Unicode:\"àé\") \";\")",
            Dump("0x1f, (* c *), \"s\", +3, 1_0, word, picky, àé;"));
    }

    [Fact]
    public void Repeated_groups()
    {
        Assert.Equal(
            "(Document (List (Block \"{\" (List (Group Word:\"a\" \"=\" Digits:\"1\") (Group Word:\"b\" \"=\" Digits:\"2\")) \"}\")) \";\")",
            Dump("{ a = 1 b = 2 };"));
    }

    [Fact]
    public void Choice_with_a_group_branch_and_an_optional_character_class()
    {
        Assert.Equal("(Document (List (Pick \"pick\" (Group \"b\" \"c\") \"!\")) \";\")", Dump("pick b c!;"));
        Assert.Equal("(Document (List (Pick \"pick\" \"a\" _)) \";\")", Dump("pick a;"));
    }

    [Fact]
    public void Not_predicate_in_a_syntax_rule()
    {
        Assert.Equal("(Document (List (Tagged \"tag\" Word:\"y\")) \";\")", Dump("tag y;"));
        using var result = Lexical.Parse("tag x;", LexicalModule.Document);
        Assert.False(result.Success);
    }

    [Fact]
    public void At_least_one_item_is_required()
    {
        using var result = Lexical.Parse(";", LexicalModule.Document);
        Assert.False(result.Success);
    }

    [Fact]
    public void Views_over_groups_choices_and_optionals()
    {
        using var result = Lexical.Parse("pick b c!, { a = 1 };", LexicalModule.Document);
        var tree = result.Tree;
        var document = SyntaxView.Cast<DocumentNode>(tree, 0);
        Assert.Equal(2, document.Items.Count);

        var pick = SyntaxView.Cast<PickNode>(tree, document.Items[0].Index);
        Assert.Equal(SyntaxKinds.Group, pick.Choice.Kind);
        Assert.True(pick.Mark.HasValue);
        Assert.Equal("!", pick.Mark.Value.ToString());

        var block = SyntaxView.Cast<BlockNode>(tree, document.Items[1].Index);
        Assert.Equal(1, block.Body.Count);
    }

    [Fact]
    public void Trailing_absent_optional_does_not_stretch_the_span()
    {
        using var result = Lexical.Parse("pick a ;", LexicalModule.Document);
        var document = SyntaxView.Cast<DocumentNode>(result.Tree, 0);
        var pick = SyntaxView.Cast<PickNode>(result.Tree, document.Items[0].Index);
        Assert.Equal(new TextSpan(0, 6), pick.Span);
    }

    [Fact]
    public void Except_rejects_reserved_words_only_as_whole_words()
    {
        Assert.Equal(-1, LexicalModule.MatchWord("pick", 0));
        Assert.Equal(-1, LexicalModule.MatchWord("tag;", 0));
        Assert.Equal(5, LexicalModule.MatchWord("picky", 0));
        Assert.Equal(3, LexicalModule.MatchWord("pic", 0));
    }
}
