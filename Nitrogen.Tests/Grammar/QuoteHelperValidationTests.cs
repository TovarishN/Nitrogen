using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarQuoteHelperValidationTests
{
    const string Grammar = """
        syntax module M {
          syntax Other = "other";
          syntax function F(V:E) : E = <[ $V ]>;
          extensible syntax E { | Atom = "x" | Wrap = X:E "wrap" precedence 30 rewrites F(X) }
        }
        """;

    [Fact]
    public void A_valid_call_emits_a_rewrite_without_adding_a_kind()
    {
        var result = Compile(Grammar);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        string code = Assert.Single(result.Sources).Code;
        Assert.Contains("RewriteHole(\"V\", 0, \"M.E\")", code);
        Assert.DoesNotContain("LF =", code);
    }

    [Theory]
    [InlineData("rewrites F(X)", "rewrites Missing(X)", "NGR0143")]
    [InlineData("rewrites F(X)", "rewrites F()", "NGR0145")]
    [InlineData("V:E", "V:Other", "NGR0147")]
    [InlineData("X:E \"wrap\"", "\"wrap\" X:E*", "NGR0146")]
    [InlineData("F(V:E)", "F(V:E,V:E)", "NGR0142")]
    [InlineData("syntax Other", "syntax F = \"f\"; syntax Other", "NGR0141")]
    public void Invalid_signatures_or_calls_are_diagnosed(string before, string after, string code)
    {
        var result = Compile(Grammar.Replace(before, after, StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Diagnostic.Code == code);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void Imported_helpers_resolve_unqualified_or_qualified_and_report_ambiguity()
    {
        const string shared = "syntax module Shared { using M; syntax function F(V:E):E=<[ $V ]>; }";
        string caller = Grammar.Replace("syntax function F(V:E) : E = <[ $V ]>;", "using Shared;");
        Assert.False(Compile(caller, shared).HasErrors);
        Assert.False(Compile(caller.Replace("rewrites F(X)", "rewrites Shared.F(X)"), shared).HasErrors);
        var ambiguous = Compile(caller.Replace("using Shared;", "using Shared; using Another;"), shared,
            shared.Replace("module Shared", "module Another"));
        Assert.Contains(ambiguous.Diagnostics, d => d.Diagnostic.Code == "NGR0144");
    }

    static GrammarCompileResult Compile(params string[] grammars) => GrammarCompiler.Compile(
        grammars.Select((text, index) => new GrammarInput(index + ".ngr", text, "Demo")));
}
