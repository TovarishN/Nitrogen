using Nitrogen.Grammar;
using Xunit;
namespace Nitrogen.Tests;
public class GrammarContextCompilerTests
{
    static GrammarCompileResult Compile(string body) => GrammarCompiler.Compile([new("context.ngr",body,"ContextExample")]);
    [Fact] public void Scoped_grammar_emits_transparent_runtime_calls()
    {
        var result=Compile("syntax module A { context Enabled; syntax Root = with(Enabled = true) Value; syntax Value = context(Enabled) \"x\"; }");
        Assert.False(result.HasErrors,string.Join(";",result.Diagnostics.Select(d=>d.Diagnostic.Message)));
        string code=Assert.Single(result.Sources).Code;Assert.Contains("TestContext",code);Assert.Contains("ParseWithContext",code);Assert.Contains("GrammarContextDecl",code);
    }
    [Theory]
    [InlineData("syntax module A { syntax Root = context(Missing) \"x\"; }","NGR0163")]
    [InlineData("syntax module A { context F; context F; syntax Root = \"x\"; }","NGR0164")]
    [InlineData("syntax module A { context F; token X = context(F) \"x\"; syntax Root = X; }","NGR0165")]
    [InlineData("syntax module A { context F; token X = with(F = true) \"x\"; syntax Root = X; }","NGR0165")]
    public void Invalid_contexts_fail_before_emission_with_authored_locations(string text,string code)
    {
        var result=Compile(text);Assert.True(result.HasErrors);Assert.Empty(result.Sources);
        Assert.Contains(result.Diagnostics,d=>d.Path=="context.ngr"&&d.Diagnostic.Code==code&&d.Diagnostic.Span.Length>0);
    }
    [Fact] public void Imported_context_resolution_and_missing_declaration_are_checked()
    {
        var result=Compile("syntax module A { context Flag; } syntax module B { using A; syntax Root = with(Flag = true) context(A.Flag) \"x\"; }");
        Assert.False(result.HasErrors,string.Join(";",result.Diagnostics.Select(d=>d.Diagnostic.Message)));
        Assert.Contains("ContextReferences",result.Sources.Single(s=>s.HintName=="B.g.cs").Code);
    }
    [Fact] public void Unqualified_ambiguous_contexts_are_rejected()
    {
        var result=Compile("syntax module A { context F; } syntax module B { context F; } syntax module C { using A; using B; syntax Root = context(F) \"x\"; }");
        Assert.Contains(result.Diagnostics,d=>d.Diagnostic.Code=="NGR0163"&&d.Diagnostic.Message.Contains("ambiguous"));
    }
    [Fact] public void Context_predicates_cannot_hide_nullable_repetition_or_left_recursion()
    {
        var repeat=Compile("syntax module A { context F; syntax Root = context(F)*; }");Assert.True(repeat.HasErrors);Assert.Empty(repeat.Sources);
        var indirect=Compile("syntax module A { context F; syntax Guard = context(F); syntax Root = Guard*; }");Assert.True(indirect.HasErrors);
        var recursion=Compile("syntax module A { context F; syntax Root = with(F = true) Root; }");Assert.True(recursion.HasErrors);Assert.Empty(recursion.Sources);
    }
    [Fact]public void Grammar_function_context_names_bind_at_the_definition_site_and_unused_bodies_are_validated()
    {
        var good=Compile("syntax module A { context F; function Wrap(r: Grammar.Rule): Grammar.Rule = with(F = true) r; } syntax module B { using A; syntax Root = Wrap(Value); syntax Value = context(A.F) \"x\"; }");
        Assert.False(good.HasErrors,string.Join(";",good.Diagnostics.Select(d=>d.Diagnostic.Message)));
        var imported=Compile("syntax module A { context F; } syntax module B { using A; function Wrap(r: Grammar.Rule): Grammar.Rule = with(F = true) r; } syntax module C { using B; syntax Root = Wrap(\"x\"); }");
        Assert.False(imported.HasErrors,string.Join(";",imported.Diagnostics.Select(d=>d.Diagnostic.Message)));
        var bad=Compile("syntax module A { function Wrap(r: Grammar.Rule): Grammar.Rule = with(Missing = true) r; syntax Root = \"x\"; }");
        Assert.Contains(bad.Diagnostics,d=>d.Diagnostic.Code=="NGR0163");
    }
    [Theory]
    [InlineData("syntax module A { context F; syntax Root = with(F = true) context(F) \"x\"; }")]
    [InlineData("/*𐐀*/syntax module A { context F; syntax Root = with(A.F = false) (Value / \"x\")?; syntax Value = \"v\"; }")]
    [InlineData("syntax module A { context F; function Wrap(r: Grammar.Rule): Grammar.Rule = with(F = true) r; syntax Root = Wrap(\"x\"); }")]
    public void Selfhost_and_bootstrap_models_match_including_context_name_and_expression_spans(string text)
    {
        var expected=GrammarParser.Parse(text);var actual=Nitrogen.Ngr.NgrParser.Parse(text);
        Assert.True(expected.Success);Assert.True(actual.Success,string.Join(";",actual.Diagnostics));
        Assert.Equal(GrammarDumper.Dump(expected.File!),GrammarDumper.Dump(actual.File!));Assert.Equal(expected.File,actual.File);
    }
    [Theory][InlineData("context()")][InlineData("with(F = maybe) \"x\"")]
    public void Both_parsers_reject_malformed_context_forms(string body)
    {
        string text="syntax module A { context F; syntax Root = "+body+"; }";
        Assert.False(GrammarParser.Parse(text).Success);Assert.False(Nitrogen.Ngr.NgrParser.Parse(text).Success);
    }
    [Fact]public void Existing_nested_choices_without_context_keep_their_nominal_rule_kind()
    {
        var result=Compile("syntax module A { syntax R = (X / Y) / (U / V); syntax X=\"x\"; syntax Y=\"y\"; syntax U=\"u\"; syntax V=\"v\"; }");
        Assert.False(result.HasErrors);Assert.Contains("public const int LR =",Assert.Single(result.Sources).Code);
    }
}
