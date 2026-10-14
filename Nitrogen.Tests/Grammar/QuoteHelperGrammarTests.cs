using Nitrogen.Grammar;
using Nitrogen.Ngr;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteHelperGrammarTests
{
    [Theory]
    [InlineData("syntax function F(X: E) : E = <[ $X ]>;", "rewrites F(Value)")]
    [InlineData("syntax function F() : E = <[ x ]>;", "rewrites F()")]
    [InlineData("syntax function F(X: M.E*, Y: E) : M.E = <[ ..$X $Y ]>;", "rewrites M.F(Values, Value)")]
    public void Helpers_and_calls_have_self_hosted_model_and_span_parity(string helper, string call)
    {
        string text = "syntax module M { " + helper + " syntax E = \"x\"; syntax R = Value:E Values:E* " + call + "; }";
        var bootstrap = GrammarParser.Parse(text);
        Assert.True(bootstrap.Success, string.Join("\n", bootstrap.Diagnostics));
        var declared = Assert.Single(bootstrap.File!.Modules[0].Helpers);
        Assert.Equal("F", declared.Name.Name);
        Assert.Equal(declared.Body.Text, text.Substring(declared.Body.Span.Start, declared.Body.Span.Length));
        Assert.DoesNotContain(bootstrap.File.Modules[0].Rules, rule => rule.Name == "F");
        var selfHosted = NgrParser.Parse(text);
        Assert.True(selfHosted.Success, string.Join("\n", selfHosted.Diagnostics));
        Assert.Equal(bootstrap.File, selfHosted.File);
        Assert.Equal(GrammarDumper.Dump(bootstrap.File!), GrammarDumper.Dump(selfHosted.File!));
    }

    [Fact]
    public void A_legacy_rule_named_function_remains_a_rule()
    {
        const string text = "syntax module M { syntax function = \"x\"; }";
        Assert.True(GrammarParser.Parse(text).Success);
        Assert.Equal(GrammarParser.Parse(text).File, NgrParser.Parse(text).File);
    }

    [Theory]
    [InlineData("syntax function F(X:E?) : E = <[ $X ]>;")]
    [InlineData("syntax function F(X:E) : E* = <[ $X ]>;")]
    [InlineData("syntax function F(X:E = x) : E = <[ $X ]>;")]
    [InlineData("syntax R=Value:E rewrites F(G(Value));")]
    public void Unsupported_function_forms_are_syntax_errors(string member)
    {
        Assert.False(GrammarParser.Parse("syntax module M { syntax E=\"x\"; " + member + " }").Success);
    }

    [Theory]
    [InlineData("| (Left: E, Right: E) <[ $Left + $Right ]> => <[ $Right + $Left ]>;")]
    [InlineData("| (Args: E*) <[ f(..$Args) ]> => <[ f(..$Args) ]>;")]
    [InlineData("| <[ x ]> => <[ x ]>;")]
    [InlineData("| () <[ x ]> => <[ x ]>;")]
    public void Match_helpers_have_full_bootstrap_and_self_hosted_parity(string arm)
    {
        string text = "syntax module M { syntax function F(Value:E):E = match Value {\n" + arm +
            "\n| _ => <[ $Value ]>; }; extensible syntax E { | Atom=\"x\" | Add=E \"+\" E precedence 10 left | Call=\"f\" \"(\" Args:(E;\",\")* \")\" } }";
        var bootstrap = GrammarParser.Parse(text);
        Assert.True(bootstrap.Success, string.Join("\n", bootstrap.Diagnostics));
        var helper = Assert.Single(bootstrap.File!.Modules[0].Helpers);
        Assert.Equal("Value", helper.Match!.Selector.Name);
        Assert.Single(helper.Match.Arms);
        Assert.Equal(" $Value ", helper.Body.Text);
        var self = NgrParser.Parse(text);
        Assert.True(self.Success, string.Join("\n", self.Diagnostics));
        Assert.Equal(bootstrap.File, self.File);
        Assert.Equal(GrammarDumper.Dump(bootstrap.File), GrammarDumper.Dump(self.File!));
        Assert.Equal(arm, text.Substring(helper.Match.Arms[0].Span.Start, helper.Match.Arms[0].Span.Length));
    }

    [Theory]
    [InlineData("| <[ x ]> => <[ x ]>;")]
    [InlineData("| _ => <[ $Value ]>; | _ => <[ $Value ]>;")]
    [InlineData("| _ => <[ $Value ]>; | <[ x ]> => <[ x ]>;")]
    [InlineData("| <[ x ]> = <[ x ]>; | _ => <[ $Value ]>;")]
    public void Invalid_match_fallback_or_arrow_is_rejected_by_both_parsers(string arms)
    {
        string text = "syntax module M { syntax function F(Value:E):E=match Value { " + arms + " }; syntax E=\"x\"; }";
        Assert.False(GrammarParser.Parse(text).Success);
        Assert.False(NgrParser.Parse(text).Success);
    }

    [Fact]
    public void A_truncated_match_arm_is_a_syntax_error()
    {
        Assert.False(GrammarParser.Parse("syntax module M { syntax function F(Value:E):E=match Value { |").Success);
        Assert.False(NgrParser.Parse("syntax module M { syntax function F(Value:E):E=match Value { |").Success);
    }

    [Fact]
    public void Match_syntax_does_not_silently_generate_a_fallback_only_helper()
    {
        const string text = "syntax module M { syntax E=\"x\"; syntax function F(Value:E):E=match Value { | _ => <[ $Value ]>; }; }";
        var result = GrammarCompiler.Compile([new GrammarInput("match.ngr", text, "Demo")]);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Contains("QuoteHelperMatchSpec", Assert.Single(result.Sources).Code);
    }
}
