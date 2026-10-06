using Nitrogen.Grammar;
using Nitrogen.Ngr;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// The self-hosted parser (generated from Nitrogen.ngr) must produce exactly the bootstrap
/// parser's GrammarModel, spans included, for every valid input, and must fail on invalid input.
/// </summary>
public class SelfHostingTests
{
    static void AssertSameModel(string text)
    {
        var bootstrap = GrammarParser.Parse(text);
        var selfHosted = NgrParser.Parse(text);
        Assert.True(bootstrap.Success, bootstrap.Diagnostics.Count > 0 ? "bootstrap: " + bootstrap.Diagnostics[0] : "");
        Assert.True(selfHosted.Success, selfHosted.Diagnostics.Count > 0 ? "self-hosted: " + selfHosted.Diagnostics[0] : "");

        // Readable failures first: structure, then each declaration with its spans, then everything.
        var expected = bootstrap.File!;
        var actual = selfHosted.File!;
        Assert.Equal(GrammarDumper.Dump(expected), GrammarDumper.Dump(actual));
        for (int m = 0; m < expected.Modules.Count; m++)
        {
            var e = expected.Modules[m];
            var a = actual.Modules[m];
            Assert.Equal(e.Usings, a.Usings);
            for (int r = 0; r < e.Rules.Count; r++) Assert.Equal(e.Rules[r], a.Rules[r]);
            for (int x = 0; x < e.Extends.Count; x++) Assert.Equal(e.Extends[x], a.Extends[x]);
            Assert.Equal(e.Span, a.Span);
        }
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(NgrGrammarTests.GrammarFiles), MemberType = typeof(NgrGrammarTests))]
    public void Every_grammar_file_maps_to_the_bootstrap_model(string file) =>
        AssertSameModel(TestGrammarFileTests.ReadGrammar(file));

    [Theory]
    [InlineData("\"a\"")]
    [InlineData("\"\\n\\u0041\\\\\"")]
    [InlineData("A B")]
    [InlineData("A / B C")]
    [InlineData("X:A? B* C+")]
    [InlineData("(A; \",\")*")]
    [InlineData("(A; \",\")+")]
    [InlineData("(A B)?")]
    [InlineData("(A)")]
    [InlineData("(A) B")]
    [InlineData("['a'..'z' '_']")]
    [InlineData("[^ '\"' '\\\\' '\\'' '\\u00e9']")]
    [InlineData("!\"*/\" .")]
    [InlineData("&A B")]
    [InlineData("!!A")]
    [InlineData("M.Expr")]
    [InlineData("A?*")]
    [InlineData("X : A")]
    [InlineData("precedenceX")]
    [InlineData("scopeX declaresY")]
    [InlineData("lowersX literalY")]
    public void Every_expression_form_maps_to_the_bootstrap_model(string body) =>
        AssertSameModel("syntax module M { syntax R = " + body + "; }");

    [Theory]
    [InlineData("")]
    [InlineData("  // only a comment\n")]
    [InlineData("syntax module A { } syntax module B.C { using A; }")]
    [InlineData("syntax module M { extensible syntax E { | N = \"n\" precedence 5 left | P = E \"+\" E precedence 10 | Q | \"x\" \"y\" } }")]
    [InlineData("syntax module M { extend syntax Other.E { | A = \"a\" precedence 7 right } }")]
    [InlineData("syntax module M { token Id = ['a'..'z']+ except \"if\" \"in\"; token T = Id; }")]
    [InlineData("syntax module M { syntax R = exceptional; token exceptional = \"x\"; }")]
    [InlineData("syntax module M { extensible syntax E { | G = (A B) | H = \"h\" (C) precedence 3 } }")]
    [InlineData("syntax module M\n{\n  /* block */ token T = \"t\" ; // trailing\n}\n")]
    [InlineData("syntax module M { symbols { a b } builtin a { x y.z } syntax R = \"r\" N:A declares a N export scope; syntax D = X:A \"$\" dynamic; }")]
    [InlineData("syntax module M { extensible syntax E { | Ref = A references (a | b) this | Dot = E \".\" P:A precedence 9 left references? a this | Plain = A \"x\" references a this scope } }")]
    [InlineData("syntax module M { syntax R = \"r\" N:A declares a N ; syntax S = \"s\" F:A references a F; }")]
    [InlineData("syntax module M { symbols { v } builtin v in B { here there.x } syntax B = \"b\" scope; }")]
    [InlineData("syntax module M { symbols { v } symbol property T for v : List<int> = new() { 1, 2 }; syntax R = \"r\" N:A declares v N { symbol.T = F(x => { return x; }); } }")]
    [InlineData("syntax module M { symbols { v } syntax R = \"r\" N:A T:B declares v N type T; syntax S = N:A declares v N export type Units.Angle; }")]
    [InlineData("syntax module M { symbols { v } syntax R = N:A declares v N in file; syntax S = N:A declares v N in file export type Core.Scalar; }")]
    [InlineData("syntax module M { symbols { v } syntax R = N:A declares v N sequential; syntax S = N:A declares v N sequential in file export type Core.Scalar; }")]
    [InlineData("syntax module M { syntax R = \"r\" A:X B:Y lowers M.Op(A, B); syntax S = V:X lowers literal Core.Scalar V { out T : int = 0; } syntax U = \"u\" lowers Op(); syntax W = \"w\" A:X lowers literal(A); }")]
    [InlineData("syntax module M { token N = ['a'..'z']+; syntax R = V:N lowers text Core.Text V; syntax S = V:R* lowers sequence M.Part V; }")]
    [InlineData("syntax module M { syntax R = Count:E Iterator:I Children:P* lowers repeat Core.Scalar Count Iterator Children; }")]
    [InlineData("syntax module M { syntax R = Items:(Part; \",\")* lowers M.Build(sequence M.Part Items); }")]
    [InlineData("syntax module M { token T = ['a'..'z']+; syntax R = Value:T lowers M.Build(text Value); }")]
    [InlineData("syntax module M { token T = ['0'..'9']+; syntax R = Value:T lowers value Core.Scalar Number { out Number : float? = null; Number = 3f; } }")]
    [InlineData("syntax module M { token T = ['0'..'9']+; syntax R = Value:T lowers value type Resolved Number { out Resolved : Nitrogen.Semantic.SemanticType = Nitrogen.Semantic.SemanticTypes.Scalar; out Number : float? = null; } }")]
    [InlineData("syntax module M { syntax R = A:X B:Y lowers operation Selected(A, B) { out Selected : Nitrogen.Semantic.OperationSignature? = null; } }")]
    [InlineData("syntax module M { syntax R = A:X B:Y lowers operation? Selected(A, B) { out Selected : Nitrogen.Semantic.OperationSignature? = null; } }")]
    [InlineData("syntax module M { symbols { value } token N = ['a'..'z']+; syntax R = Name:N references value Name lowers reference type Resolved { out Resolved : Nitrogen.Semantic.SemanticType? = null; } }")]
    [InlineData("syntax module M { symbols { value } token N = ['a'..'z']+; syntax R = Name:N references value Name lowers reference type Resolved initializer Source { out Resolved : Nitrogen.Semantic.SemanticType? = null; out Source : int? = null; } }")]
    [InlineData("syntax module M { token T = ['0'..'9']+; syntax R = Value:T lowers value typeFoo Number { out Number : float? = null; } }")]
    [InlineData("syntax module M { token T = ['0'..'9']+; syntax R = Value:T? lowers M.Build(optional Core.Scalar Value); }")]
    [InlineData("syntax module M { token T = ['a'..'z']+; syntax R = Mark:\"!\"? Value:T lowers M.Build(optional text Mark, text Value); }")]
    [InlineData("syntax module M { extensible syntax E { | N = \"-\"? V:X lowers literal Core.Scalar this | P = L:E \"+\" R:E precedence 6 left lowers M.Add(L, R) } }")]
    [InlineData("syntax module M { extensible syntax E { out hover T : int = 0; in expected X : string? = null; | N = \"n\" { T = 1; } | P = E \"+\" E precedence 6 left { T = E1.T + E2.T; E1.X = \"a;b\"; check AB0001 (T > 0 ? true : false) : $\"bad {T}\"; } } }")]
    [InlineData("syntax module M { syntax R = \"r\" { out O : int = 0; O = 'c' == ';' ? 1 : /* ; */ 2; // ;\n check O >= 0 : @\"x\"\"y\"; } }")]
    [InlineData("syntax module M { symbols { v w } symbol property T for (v | w) : int = 0; }")]
    [InlineData("syntax module M { symbols { v s } syntax R = \"r\" N:A references v N in s; syntax Q = \"q\" N:A references? (v | s) N in s; }")]
    [InlineData("syntax module M { symbols { f p } token N = ['a'..'z']+; syntax P = Name:N declares p Name type Core.Scalar; syntax D = \"def\" Name:N \"(\" Params:(P; \",\")* \")\" Body:N declares f Name lowers template Body(Params); }")]
    [InlineData("syntax module M { symbols { f } token N = ['a'..'z']+; syntax C = \"make\" Name:N \"(\" Args:N* \")\" references f Name lowers expand Name(Args); }")]
    public void Every_declaration_form_maps_to_the_bootstrap_model(string text) => AssertSameModel(text);

    [Theory]
    [InlineData("syntax module M { syntax R = ; }")]
    [InlineData("syntax module M { symbols { } }")]
    [InlineData("syntax module M { syntax R = A }")]
    [InlineData("syntax module M { rule R = A; }")]
    [InlineData("syntax module M { syntax R = (A; \",\"); }")]
    [InlineData("syntax module M { syntax R = \"\"; }")]
    [InlineData("syntax module M { syntax R = ['z'..'a']; }")]
    [InlineData("syntax module M { syntax R = []; }")]
    [InlineData("syntax module M { # }")]
    [InlineData("syntax module M {")]
    [InlineData("module M { }")]
    [InlineData("syntax module M { extensible syntax E { | A = \"a\" precedence x } }")]
    [InlineData("syntax module M { extensible syntax E { | A = \"a\" precedence 1234567890 } }")]
    [InlineData("syntax module M { syntax R = \"a\" \"b; }")]
    [InlineData("syntax module M { syntax R = 'ab'; }")]
    [InlineData("syntax module M { token A.B = \"x\"; }")]
    [InlineData("syntax module A { } x")]
    [InlineData("syntax module M { syntax R = \"r\" { a.b.c = 1; } }")]
    [InlineData("syntax module M { syntax R = \"r\" { X = ; } }")]
    [InlineData("syntax module M { syntax R = \"r\" { X = f(1; } }")]
    [InlineData("syntax module M { syntax R = \"r\" { check MT0001 : \"m\"; } }")]
    [InlineData("syntax module M { extend syntax E { out T : int = 0; | A = \"a\" } }")]
    public void Invalid_input_fails_in_both_parsers(string text)
    {
        Assert.False(GrammarParser.Parse(text).Success);
        var result = NgrParser.Parse(text);
        Assert.False(result.Success);
        Assert.Equal(GrammarCodes.Syntax, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void The_grammar_language_describes_itself()
    {
        var result = NgrParser.Parse(TestGrammarFileTests.ReadGrammar("Nitrogen.ngr"));
        Assert.True(result.Success);
        Assert.Empty(GrammarValidator.Validate(result.File!.Modules));
    }
}
