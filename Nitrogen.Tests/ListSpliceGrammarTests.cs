using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public sealed class ListSpliceGrammarTests
{
    [Theory]
    [InlineData("Value:Expr", "..$Value", "NGR0139")]
    [InlineData("Value:Expr*", "$Value", "NGR0139")]
    [InlineData("Value:Expr?", "..$Value", "NGR0140")]
    [InlineData("Value:N*", "..$Value", "NGR0140")]
    [InlineData("Value:(Expr Expr)*", "..$Value", "NGR0140")]
    [InlineData("Value:(Expr; N)*", "..$Value", "NGR0140")]
    public void Invalid_shapes_are_reported(string field, string template, string code)
    {
        string text = "syntax module M { token N = ['0'..'9']+; syntax Expr = N; syntax R = " +
            field + " rewrites <[ " + template + " ]>; }";
        var parsed = GrammarParser.Parse(text);
        Assert.True(parsed.Success);
        Assert.Contains(GrammarValidator.Validate(parsed.File!.Modules), d => d.Code == code);
    }

    [Fact]
    public void Marker_text_in_a_literal_is_not_a_hole()
    {
        const string text = "syntax module M { syntax R = \"r\" rewrites <[ \"$Unknown\" ]>; }";
        var result = GrammarCompiler.Compile([new GrammarInput("M.ngr", text, "Tests.M")]);
        Assert.DoesNotContain(result.Diagnostics, d => d.Diagnostic.Code == "NGR0135");
    }
    [Fact]
    public void Opaque_custom_comment_tokens_do_not_declare_holes()
    {
        const string text = """
            syntax module M
            {
                token Comment = "(*" (!"*)" .)* "*)";
                syntax Expr = Comment / R;
                syntax R = "r" rewrites <[ (* $Unknown *) ]>;
            }
            """;
        var result = GrammarCompiler.Compile([new GrammarInput("M.ngr", text, "Tests.M")]);
        Assert.DoesNotContain(result.Diagnostics, d => d.Diagnostic.Code == "NGR0135");
    }
    [Fact]
    public void Unreachable_tokens_cannot_hide_unknown_splice_fields()
    {
        const string text = """
            syntax module M
            {
                token UnusedOpaque = .+;
                token Number = ['0'..'9']+;
                syntax Expr = Num / R;
                syntax Num = Number;
                syntax R = "r" Value:Expr rewrites <[ $Unknown ]>;
            }
            """;
        var result = GrammarCompiler.Compile([new GrammarInput("M.ngr", text, "Tests.M")]);
        Assert.Contains(result.Diagnostics, d => d.Diagnostic.Code == "NGR0135");
    }
    [Fact]
    public void Reachable_tokens_require_their_actual_syntax_prefix()
    {
        const string text = """
            syntax module M
            {
                token Opaque = .+;
                token Number = ['0'..'9']+;
                syntax Expr = Num / R / Raw;
                syntax Num = Number;
                syntax Raw = "raw" Opaque;
                syntax R = "r" Value:Expr rewrites <[ $Unknown ]>;
            }
            """;
        var result = GrammarCompiler.Compile([new GrammarInput("M.ngr", text, "Tests.M")]);
        Assert.Contains(result.Diagnostics, d => d.Diagnostic.Code == "NGR0135");
    }
    [Fact]
    public void Lookahead_tokens_do_not_hide_unknown_fields()
    {
        const string text = """
            syntax module M
            {
                token DollarName = "$" ['a'..'z' 'A'..'Z']+;
                token Number = ['0'..'9']+;
                syntax Expr = Num / R;
                syntax Num = !DollarName Number;
                syntax R = "r" Value:Expr rewrites <[ $Unknown ]>;
            }
            """;
        var result = GrammarCompiler.Compile([new GrammarInput("M.ngr", text, "Tests.M")]);
        Assert.Contains(result.Diagnostics, d => d.Diagnostic.Code == "NGR0135");
    }
}
