using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class GrammarParserTests
{
    static GrammarFile ParseOk(string text)
    {
        var result = GrammarParser.Parse(text);
        Assert.True(result.Success, result.Diagnostics.Count > 0 ? result.Diagnostics[0].ToString() : "");
        Assert.Empty(result.Diagnostics);
        return result.File!;
    }

    static string BodyDump(string body)
    {
        var file = ParseOk("syntax module M { syntax R = " + body + "; }");
        return GrammarDumper.Dump(((SyntaxRule)file.Modules[0].Rules[0]).Body);
    }

    [Theory]
    [InlineData("\"a\"", "(lit \"a\")")]
    [InlineData("\"\\n\"", "(lit \"\\n\")")]
    [InlineData("A B", "(seq (ref A) (ref B))")]
    [InlineData("A / B C", "(choice (ref A) (seq (ref B) (ref C)))")]
    [InlineData("X:A? B* C+", "(seq (label X (opt (ref A))) (star (ref B)) (plus (ref C)))")]
    [InlineData("(A; \",\")*", "(sep* (ref A) (lit \",\"))")]
    [InlineData("(A; \",\")+", "(sep+ (ref A) (lit \",\"))")]
    [InlineData("(A B)?", "(opt (seq (ref A) (ref B)))")]
    [InlineData("(A)", "(ref A)")]
    [InlineData("['a'..'z' '_']", "(class 'a'..'z' '_')")]
    [InlineData("[^ '\"' '\\\\']", "(class ^ '\"' '\\\\')")]
    [InlineData("!\"*/\" .", "(seq (not (lit \"*/\")) any)")]
    [InlineData("&A B", "(seq (and (ref A)) (ref B))")]
    [InlineData("M.Expr", "(ref M.Expr)")]
    [InlineData("A?*", "(star (opt (ref A)))")]
    public void Expressions(string body, string expected)
    {
        Assert.Equal(expected, BodyDump(body));
    }

    [Fact]
    public void Module_with_every_member_kind()
    {
        var file = ParseOk(@"
            // header comment
            syntax module Calc.Power
            {
              using Base;
              token T = ""t"";
              extensible syntax E
              {
                | Num
                | Add = E ""+"" E precedence 10 left
                | Neg = ""-"" E precedence 30
                | Pow = E ""^"" E precedence 25 right
              }
              extend syntax Other.X { | Y = ""y"" }
            }");

        Assert.Equal(
            "(module Calc.Power (using Base) (token T (lit \"t\"))" +
            " (extensible E (alt Num (ref Num)) (alt Add (seq (ref E) (lit \"+\") (ref E)) 10 left)" +
            " (alt Neg (seq (lit \"-\") (ref E)) 30) (alt Pow (seq (ref E) (lit \"^\") (ref E)) 25 right))" +
            " (extend Other.X (alt Y (lit \"y\"))))",
            GrammarDumper.Dump(file));
        var expr = (ExtensibleRule)file.Modules[0].Rules[1];
        Assert.True(expr.Alternatives[0].NameIsImplicit);
        Assert.False(expr.Alternatives[1].NameIsImplicit);
    }

    [Fact]
    public void Unnamed_non_reference_alternative_has_an_empty_name()
    {
        var file = ParseOk("syntax module M { extensible syntax E { | \"x\" \"y\" } }");
        Assert.Equal("", ((ExtensibleRule)file.Modules[0].Rules[0]).Alternatives[0].Name);
    }

    [Fact]
    public void Several_modules_and_an_empty_file()
    {
        Assert.Equal(2, ParseOk("syntax module A { } syntax module B { }").Modules.Count);
        Assert.Empty(ParseOk("  // nothing\n").Modules);
    }

    [Fact]
    public void Spans()
    {
        //                     0         1         2         3
        //                     0123456789012345678901234567890123456
        var file = ParseOk("syntax module M { syntax R = A B; }");
        var module = file.Modules[0];
        var rule = (SyntaxRule)module.Rules[0];
        Assert.Equal(new GrammarSpan(0, 35), module.Span);
        Assert.Equal(new GrammarSpan(18, 15), rule.Span);
        Assert.Equal(new GrammarSpan(29, 3), rule.Body.Span);

        var usingFile = ParseOk("syntax module M { using Z; }");
        Assert.Equal(new GrammarSpan(18, 8), usingFile.Modules[0].Usings[0].Span);

        var alternative = ((ExtensibleRule)ParseOk("syntax module M { extensible syntax E { | A = \"a\" precedence 5 left } }")
            .Modules[0].Rules[0]).Alternatives[0];
        Assert.Equal(GrammarSpan.FromBounds(40, 67), alternative.Span);
    }

    [Fact]
    public void Parsing_twice_gives_equal_models()
    {
        const string text = "syntax module M { token T = ['a'..'z']+; syntax R = X:T (T; \",\")*; }";
        Assert.Equal(GrammarParser.Parse(text).File, GrammarParser.Parse(text).File);
    }

    [Theory]
    [InlineData("syntax module M { syntax R = ; }", "expected an element, found ';'", 29)]
    [InlineData("syntax module M { syntax R = A }", "expected ';', found '}'", 31)]
    [InlineData("syntax module M { rule R = A; }", "expected 'using', 'symbols', 'symbol', 'builtin', 'token', 'syntax', 'extensible', 'extend' or '}', found 'rule'", 18)]
    [InlineData("syntax module M { syntax R = (A; \",\"); }", "expected '*' or '+' after a separated list, found ';'", 37)]
    [InlineData("syntax module M { syntax R = \"\"; }", "a literal must not be empty", 29)]
    [InlineData("syntax module M { syntax R = ['z'..'a']; }", "empty character range", 30)]
    [InlineData("syntax module M { syntax R = []; }", "a character class needs at least one character", 29)]
    [InlineData("syntax module M { # }", "unexpected character '#'", 18)]
    [InlineData("syntax module M {", "expected 'using', 'symbols', 'symbol', 'builtin', 'token', 'syntax', 'extensible', 'extend' or '}', found end of input", 17)]
    [InlineData("module M { }", "expected 'syntax', found 'module'", 0)]
    [InlineData("syntax module M { extensible syntax E { | A = \"a\" precedence x } }", "expected a precedence number, found 'x'", 61)]
    [InlineData("syntax module M { syntax R = \"a\" \"b; }", "unterminated literal", 33)]
    public void Syntax_errors(string text, string message, int start)
    {
        var result = GrammarParser.Parse(text);
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GrammarCodes.Syntax, diagnostic.Code);
        Assert.Equal(message, diagnostic.Message);
        Assert.Equal(start, diagnostic.Span.Start);
    }

    [Fact]
    public void Token_rule_except_clause()
    {
        var file = ParseOk("syntax module M { token Id = ['a'..'z']+ except \"if\" \"in\"; }");
        Assert.Equal("(module M (token Id (plus (class 'a'..'z')) (except \"if\" \"in\")))", GrammarDumper.Dump(file));
        var rule = (TokenRule)file.Modules[0].Rules[0];
        Assert.Equal(new GrammarSpan(48, 4), rule.Except[0].Span);
        Assert.Equal(GrammarSpan.FromBounds(18, 58), rule.Span);
    }

    [Theory]
    [InlineData("syntax module M { token Id = ['a'..'z']+ except ; }", "expected a string literal, found ';'")]
    [InlineData("syntax module M { token Id = ['a'..'z']+ except \"\"; }", "a literal must not be empty")]
    public void Except_errors(string text, string message)
    {
        var result = GrammarParser.Parse(text);
        Assert.Equal(message, Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Binding_declarations_and_clauses()
    {
        var file = ParseOk("""
            syntax module M
            {
              symbols { unit value }
              builtin value { pi sys.clock }
              syntax U = "unit" Name:Id "{" "}" declares unit Name export scope;
              syntax N = X:Id dynamic;
              extensible syntax E
              {
                | Ref = Id references (value | unit) this
                | Dot = E "." P:Id precedence 9 left references? value this
              }
            }
            """);
        Assert.Equal(
            "(module M (symbols unit value) (builtin value pi sys.clock)"
            + " (syntax U (seq (lit \"unit\") (label Name (ref Id)) (lit \"{\") (lit \"}\")) (declares unit Name export) (scope))"
            + " (syntax N (label X (ref Id)) (dynamic))"
            + " (extensible E (alt Ref (ref Id) (references value|unit this))"
            + " (alt Dot (seq (ref E) (lit \".\") (label P (ref Id))) 9 left (references? value this))))",
            GrammarDumper.Dump(file));
        var dot = ((ExtensibleRule)file.Modules[0].Rules[2]).Alternatives[1];
        Assert.Equal(dot.Clauses[0].Span.End, dot.Span.End);
        Assert.Equal(2, ((SyntaxRule)file.Modules[0].Rules[0]).Clauses.Count);
    }

    [Theory]
    [InlineData("syntax module M { symbols { } }", "expected a symbol kind, found '}'")]
    [InlineData("syntax module M { builtin v { } }", "expected a built-in name, found '}'")]
    [InlineData("syntax module M { syntax R = A declares; }", "expected a symbol kind, found ';'")]
    [InlineData("syntax module M { syntax R = A references (v | ) X; }", "expected a symbol kind, found ')'")]
    [InlineData("syntax module M { syntax R = A references v; }", "expected a field label or 'this', found ';'")]
    public void Binding_syntax_errors(string text, string message)
    {
        var result = GrammarParser.Parse(text);
        Assert.False(result.Success);
        Assert.Equal(message, result.Diagnostics[0].Message);
    }

    [Fact]
    public void A_builtin_can_be_scoped_to_a_rule()
    {
        var file = ParseOk("syntax module M { symbols { v } builtin v in B { here } syntax B = \"b\" scope; }");
        Assert.StartsWith("(module M (symbols v) (builtin v in B here)", GrammarDumper.Dump(file));
        Assert.Equal("B", file.Modules[0].Builtins[0].Scope!.Name);
    }

    [Fact]
    public void Semantics_blocks_capture_csharp_as_text()
    {
        const string text = """
            syntax module M
            {
              symbols { v w }
              symbol property Type for (v | w) : Dictionary<string, int> = new() { ["a;b"] = 1 };
              syntax Let = "let" N:A V:E declares v N
              {
                out Width : int = 0;
                symbol.Type = F(x => { return x; });
                V.Expected = Width > 0 ? "wide" : "narrow";
                Width = V.Size - 1; // a comment
                check MT0001 (V.Type is null ? true : V.Type != "") : $"bad {V.Type}";
                check V.Size >= 0 : "negative";
              }
              extensible syntax E
              {
                out hover Type : string? = null;
                in expected Expected : string = "";
                | Num = A { Type = @"num""s"; }
                | Add = E "+" E precedence 6 left { Type = E1.Type; }
              }
            }
            """;
        var module = ParseOk(text).Modules[0];

        var symbol = Assert.Single(module.SymbolProperties);
        Assert.Equal("Type", symbol.Name.Name);
        Assert.Equal(new[] { "v", "w" }, symbol.Kinds.Select(k => k.Name));
        Assert.Equal("Dictionary<string, int>", symbol.Type.Text);
        Assert.Equal("new() { [\"a;b\"] = 1 }", symbol.Default.Text);
        Assert.Equal(text.IndexOf("Dictionary", StringComparison.Ordinal), symbol.Type.Span.Start);

        var let = (SyntaxRule)module.Rules[0];
        var block = let.Semantics!;
        var width = Assert.Single(block.Properties);
        Assert.Equal((PropertyDirection.Out, "Width", "int", "0"), (width.Direction, width.Name.Name, width.Type.Text, width.Default.Text));
        Assert.Equal(5, block.Statements.Count);
        var symbolAssign = (AssignStatement)block.Statements[0];
        Assert.Equal((AssignTarget.Symbol, "Type", "F(x => { return x; })"), (symbolAssign.Target, symbolAssign.Property.Name, symbolAssign.Value.Text));
        var child = (AssignStatement)block.Statements[1];
        Assert.Equal((AssignTarget.Child, "V", "Expected"), (child.Target, child.Child!.Name, child.Property.Name));
        Assert.Equal("Width > 0 ? \"wide\" : \"narrow\"", child.Value.Text);
        var self = (AssignStatement)block.Statements[2];
        Assert.Equal((AssignTarget.Self, "V.Size - 1"), (self.Target, self.Value.Text));
        var check = (CheckStatement)block.Statements[3];
        Assert.Equal("MT0001", check.Code!.Name);
        Assert.Equal("(V.Type is null ? true : V.Type != \"\")", check.Condition.Text);
        Assert.Equal("$\"bad {V.Type}\"", check.Message.Text);
        var plain = (CheckStatement)block.Statements[4];
        Assert.Null(plain.Code);
        Assert.Equal("V.Size >= 0", plain.Condition.Text);
        Assert.Equal(text.IndexOf('}', text.IndexOf("negative", StringComparison.Ordinal)) + 1, let.Span.End);

        var e = (ExtensibleRule)module.Rules[1];
        Assert.Equal(new[] { "Type", "Expected" }, e.Properties.Select(p => p.Name.Name));
        Assert.True(e.Properties[0].Hover);
        Assert.True(e.Properties[1].Expected);
        Assert.Equal("string?", e.Properties[0].Type.Text);
        Assert.Equal("@\"num\"\"s\"", ((AssignStatement)e.Alternatives[0].Semantics!.Statements[0]).Value.Text);
        Assert.Equal(e.Alternatives[1].Semantics!.Span.End, e.Alternatives[1].Span.End);
    }

    [Theory]
    [InlineData("check MT0001 V.Size >= 0 : $\"bad {V.Type}\" at V;", "$\"bad {V.Type}\"", "V")]
    [InlineData("check MT0001 V.Size >= 0 : $\"bad {V.Type}\";", "$\"bad {V.Type}\"", null)]
    [InlineData("check MT0001 V.Size >= 0 : \"sits at Name\";", "\"sits at Name\"", null)]
    [InlineData("check MT0001 V.Size >= 0 : Pick(\"a\")\n      at   Name ;", "Pick(\"a\")", "Name")]
    public void A_check_may_name_the_child_it_reports_at(string check, string message, string? at)
    {
        string text = "syntax module M { token T = \"t\"; syntax R = V:T { " + check + " } }";
        var rule = ParseOk(text).Modules[0].Rules.OfType<SyntaxRule>().Single();
        var parsed = (CheckStatement)rule.Semantics!.Statements[0];
        Assert.Equal(message, parsed.Message.Text);
        Assert.Equal(at, parsed.At?.Name);
        if (at is not null) Assert.Equal(text.LastIndexOf(at, StringComparison.Ordinal), parsed.At!.Span.Start);
    }

    [Theory]
    [InlineData("syntax module M { syntax R = \"r\" { a.b.c = 1; } }", "an assignment target is Property, Child.Property or symbol.Property")]
    [InlineData("syntax module M { syntax R = \"r\" { X = ; } }", "expected a C# value")]
    [InlineData("syntax module M { syntax R = \"r\" { X = f(1; } }", "expected ';', found '('")]
    [InlineData("syntax module M { syntax R = \"r\" { check MT0001 : \"m\"; } }", "expected a C# condition")]
    [InlineData("syntax module M { extend syntax E { out T : int = 0; | A = \"a\" } }", "expected '|' or '}', found 'out'")]
    public void Malformed_semantics_are_syntax_errors(string text, string message)
    {
        var result = GrammarParser.Parse(text);
        Assert.False(result.Success);
        Assert.Contains(message, Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void A_reference_may_resolve_in_another_symbols_scope()
    {
        var rule = (SyntaxRule)ParseOk("syntax module M { syntax R = \"r\" N:A references value N in skill; }").Modules[0].Rules[0];
        var clause = Assert.Single(rule.Clauses);
        Assert.Equal(("value", "N", "skill"), (clause.Kinds[0].Name, clause.Field, clause.Qualifier!.Name));
    }

    [Theory]
    [InlineData("syntax R = \"r\" A:X B:Y lowers M.Op(A, B);", "(lowers M.Op(A, B))")]
    [InlineData("syntax R = \"r\" lowers Op();", "(lowers Op())")]
    [InlineData("syntax R = V:X lowers literal Core.Scalar V;", "(lowers literal Core.Scalar V)")]
    [InlineData("syntax R = \"-\"? V:X lowers literal Core.Scalar this;", "(lowers literal Core.Scalar this)")]
    [InlineData("syntax R = \"r\" A:X lowers literal(A);", "(lowers literal(A))")]
    [InlineData("syntax R = N:X T:Y declares v N type T;", "(declares v N type T)")]
    [InlineData("syntax R = N:X declares v N export type Units.Angle;", "(declares v N export type Units.Angle)")]
    public void Declarative_clauses_parse(string rule, string clause)
    {
        var file = ParseOk("syntax module M { symbols { v } " + rule + " }");
        var syntax = (SyntaxRule)file.Modules[0].Rules[0];
        Assert.Equal(clause, GrammarDumper.Dump(Assert.Single(syntax.Clauses)));
    }

    [Fact]
    public void Lowers_clause_spans_its_keyword_to_the_closing_parenthesis()
    {
        const string text = "syntax module M { syntax R = \"r\" A:X lowers M.Op(A) ; }";
        var clause = Assert.Single(((SyntaxRule)ParseOk(text).Modules[0].Rules[0]).Clauses);
        Assert.Equal("lowers M.Op(A)", text.Substring(clause.Span.Start, clause.Span.Length));
        Assert.Equal("M.Op", clause.Target!.Name);
        Assert.Equal(["A"], clause.Arguments.Select(argument => argument.Name));
    }
}
