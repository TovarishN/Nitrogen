using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Where a C# fragment in a semantics block ends (issue 239).</summary>
public class CSharpCaptureTests
{
    static int Scan(string text, string stops = CSharpCapture.Statement) => CSharpCapture.Scan(text, 0, stops);

    [Theory]
    [InlineData("a + b; rest", 5)]
    [InlineData("f(a; b) ; x", 8)]
    [InlineData("\"a;b\" + c; x", 9)]
    [InlineData("$\"a{b};\" ; x", 9)]
    [InlineData("@\"a\"\";b\" ; x", 9)]
    [InlineData("$@\"a;\" ; x", 7)]
    [InlineData("';' ; x", 4)]
    [InlineData("'\\'' ; x", 5)]
    [InlineData("a // ;\n b; x", 9)]
    [InlineData("a /* ; */ b; x", 11)]
    [InlineData("new() { [\"k\"] = 1 }; x", 19)]
    [InlineData("a - b % c <= d; x", 14)]
    public void A_statement_ends_at_the_first_top_level_semicolon(string text, int end) => Assert.Equal(end, Scan(text));

    [Fact]
    public void A_condition_ends_at_a_top_level_colon_and_a_type_at_equals()
    {
        Assert.Equal(21, Scan("(a ? b : c) && d > 0 : \"m\";", CSharpCapture.Condition));
        Assert.Equal(24, Scan("Dictionary<string, int> = x;", CSharpCapture.Type));
        Assert.Equal(1, Scan("x;", CSharpCapture.Type));
    }

    [Theory]
    [InlineData("a } b", 2)]           // a closer at the top level ends the fragment
    [InlineData("a (] ;", 2)]          // a mismatched bracket ends it where the group opens
    [InlineData("f(a;", 1)]            // so does an unclosed one
    [InlineData("x \"abc", 2)]         // and an unterminated string
    [InlineData("x '' ;", 2)]          // and an empty char
    [InlineData("a /* b ; c", 7)]      // an unterminated block comment is just a '/'
    [InlineData("abc", 3)]
    public void Broken_fragments_end_where_the_self_hosted_tokens_end(string text, int end) => Assert.Equal(end, Scan(text));
}
