using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class GrammarValidatorTests
{
    static GrammarDiagnostic[] Validate(params string[] texts)
    {
        var modules = new List<ModuleDecl>();
        foreach (string text in texts)
        {
            var result = GrammarParser.Parse(text);
            Assert.True(result.Success, result.Diagnostics.Count > 0 ? result.Diagnostics[0].ToString() : "");
            modules.AddRange(result.File!.Modules);
        }
        return GrammarValidator.Validate(modules).ToArray();
    }

    static GrammarDiagnostic Single(string code, params string[] texts)
    {
        var diagnostics = Validate(texts);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(GrammarSeverity.Error, diagnostic.Severity);
        return diagnostic;
    }

    [Fact]
    public void A_valid_grammar_has_no_diagnostics()
    {
        Assert.Empty(Validate(
            "syntax module M { token T = ['a'..'z']+; syntax R = X:T (T; \",\")* \"end\"; extensible syntax E { | T | Add = E \"+\" E precedence 10 } }",
            "syntax module N { extend syntax M.E { | Neg = \"-\" E precedence 5 | Twice = E E precedence 3 } }"));
    }

    [Fact]
    public void Undefined_rule()
    {
        var d = Single(GrammarCodes.UndefinedRule, "syntax module M { syntax R = X; }");
        Assert.Equal("undefined rule 'X'", d.Message);
        Assert.Equal(new GrammarSpan(29, 1), d.Span);
        Assert.Equal("M", d.Module);
    }

    [Fact]
    public void Qualified_references()
    {
        Assert.Equal("unknown module 'Q'", Single(GrammarCodes.UnknownModule, "syntax module M { syntax R = Q.X; }").Message);
        Assert.Equal("module 'A' has no rule 'U'",
            Single(GrammarCodes.UndefinedRule, "syntax module A { token T = \"t\"; }", "syntax module B { syntax R = A.U; }").Message);
        Assert.Empty(Validate("syntax module A.B { token T = \"t\"; }", "syntax module C { syntax R = A.B.T; }"));
    }

    [Fact]
    public void Using_imports_rules()
    {
        Assert.Empty(Validate("syntax module A { token T = \"t\"; }", "syntax module B { using A; syntax R = T; }"));
        Assert.Equal("'T' is defined in both 'A' and 'C'", Single(GrammarCodes.AmbiguousReference,
            "syntax module A { token T = \"t\"; }", "syntax module C { token T = \"c\"; }",
            "syntax module B { using A; using C; syntax R = T; }").Message);
        var unknown = Single(GrammarCodes.UnknownModule, "syntax module M { using Z; }");
        Assert.Equal("unknown module 'Z'", unknown.Message);
        Assert.Equal(new GrammarSpan(18, 8), unknown.Span);
    }

    [Fact]
    public void Local_rules_win_over_imported_ones()
    {
        Assert.Empty(Validate("syntax module A { token T = \"t\"; }", "syntax module B { using A; token T = \"b\"; syntax R = T; }"));
    }

    [Theory]
    [InlineData("syntax module M { syntax R = R \"x\" / \"y\"; }", "left recursion through M.R -> M.R")]
    [InlineData("syntax module M { syntax A = \"x\"? B; syntax B = A \"y\"; }", "left recursion through M.A -> M.B -> M.A")]
    [InlineData("syntax module M { extensible syntax E { | S = Stmt } syntax Stmt = E \";\"; }", "left recursion through M.E -> M.Stmt -> M.E")]
    public void Left_recursion(string text, string messageStart)
    {
        Assert.StartsWith(messageStart, Single(GrammarCodes.LeftRecursion, text).Message);
    }

    [Fact]
    public void Postfix_self_reference_is_not_left_recursion()
    {
        Assert.Empty(Validate("syntax module M { extensible syntax E { | N = \"n\" | Add = E \"+\" E precedence 10 } }"));
    }

    [Fact]
    public void Unreachable_alternatives()
    {
        var d = Single(GrammarCodes.UnreachableAlternative, "syntax module M { syntax R = \"+\" / \"+=\"; }");
        Assert.Equal("alternative 2 can never match: alternative 1 matches first", d.Message);
        Assert.Equal(new GrammarSpan(35, 4), d.Span);
        Single(GrammarCodes.UnreachableAlternative, "syntax module M { token T = \"if\" / \"iffy\"; }");
        Single(GrammarCodes.UnreachableAlternative, "syntax module M { token A = \"a\"; syntax R = A? / \"b\"; }");
        Assert.Empty(Validate("syntax module M { syntax R = \"if\" / \"iffy\"; }"));
        Assert.Empty(Validate("syntax module M { syntax R = \"+=\" / \"+\"; }"));
    }

    [Fact]
    public void Duplicate_label()
    {
        Assert.Equal("label 'X' is used twice",
            Single(GrammarCodes.DuplicateLabel, "syntax module M { token A = \"a\"; syntax R = X:A X:A; }").Message);
        Assert.Empty(Validate("syntax module M { token A = \"a\"; syntax R = X:A / X:\"b\"; }"));
    }

    [Fact]
    public void Duplicate_alternative_names()
    {
        Assert.Equal("extension point 'M.E' has two alternatives named 'A'",
            Single(GrammarCodes.DuplicateAlternative, "syntax module M { extensible syntax E { | A = \"a\" | A = \"b\" } }").Message);
        Single(GrammarCodes.DuplicateAlternative,
            "syntax module M { extensible syntax E { | A = \"a\" } }",
            "syntax module N { extend syntax M.E { | A = \"b\" } }");
    }

    [Fact]
    public void Duplicate_rule_and_module()
    {
        var rule = Single(GrammarCodes.DuplicateRule, "syntax module M { token A = \"a\"; token A = \"b\"; }");
        Assert.Equal("rule 'A' is declared twice", rule.Message);
        Assert.Equal(33, rule.Span.Start);
        Assert.Equal("module 'M' is declared twice", Single(GrammarCodes.DuplicateModule, "syntax module M { } syntax module M { }").Message);
    }

    [Fact]
    public void Alternative_needs_a_name()
    {
        Assert.Equal("an alternative that is not a single rule reference needs a name: '| Name = ...'",
            Single(GrammarCodes.AlternativeNeedsName, "syntax module M { extensible syntax E { | \"x\" } }").Message);
    }

    [Fact]
    public void Precedence_rules()
    {
        Assert.Equal("postfix alternative 'Add' needs a precedence of 1..255",
            Single(GrammarCodes.BadPrecedence, "syntax module M { extensible syntax E { | N = \"n\" | Add = E \"+\" E } }").Message);
        Assert.Equal("precedence 300 is outside 0..255",
            Single(GrammarCodes.BadPrecedence, "syntax module M { extensible syntax E { | N = \"n\" precedence 300 } }").Message);
        Single(GrammarCodes.BadPrecedence, "syntax module M { extensible syntax E { | N = \"n\" | Add = E \"+\" E precedence 0 } }");
    }

    [Fact]
    public void Extend_target_must_be_extensible()
    {
        Assert.Equal("'M.T' is not an extensible rule", Single(GrammarCodes.NotExtensible,
            "syntax module M { token T = \"t\"; }", "syntax module N { extend syntax M.T { | A = \"a\" } }").Message);
        Single(GrammarCodes.UnknownModule, "syntax module N { extend syntax Q.E { | A = \"a\" } }");
    }

    [Fact]
    public void Token_rules_stay_lexical()
    {
        Assert.Equal("token rule cannot reference syntax rule 'R'",
            Single(GrammarCodes.TokenReferencesSyntax, "syntax module M { token T = R; syntax R = \"r\"; }").Message);
    }

    [Fact]
    public void Prefix_alternative_must_consume_input()
    {
        Assert.Equal("alternative 'Nothing' can match empty input",
            Single(GrammarCodes.NullablePrefix, "syntax module M { extensible syntax E { | Nothing = \"x\"? } }").Message);
    }

    [Fact]
    public void Extend_resolves_names_through_the_extended_module()
    {
        Assert.Empty(Validate(
            "syntax module Calc { token Identifier = ['a'..'z']+; extensible syntax Expr { | Ref = Identifier } }",
            "syntax module Calc.Clash { extend syntax Calc.Expr { | Invoke = Callee:Identifier \"(\" Args:(Expr; \",\")* \")\" } }"));
    }

    const string BindingPrelude = "symbols { unit value } token Id = ['a'..'z']+; token Num = ['0'..'9']+; ";

    [Fact]
    public void A_valid_binding_grammar_has_no_diagnostics()
    {
        Assert.Empty(Validate(
            "syntax module M { " + BindingPrelude
            + "builtin value { pi sys.clock } syntax U = \"u\" Name:Id declares unit Name export scope;"
            + " extensible syntax E { | Ref = Id references (value | unit) this | Dot = E \".\" P:Id precedence 9 left references? value this } }",
            "syntax module N { using M; syntax V = \"v\" Name:M.Id declares value Name; syntax G = Head:M.Id \"$\" Seq:M.Num dynamic; }"));
    }

    [Theory]
    [InlineData("syntax R = \"r\" N:Id declares thing N;", GrammarCodes.UnknownSymbolKind)]
    [InlineData("syntax R = \"r\" N:Id declares unit M;", GrammarCodes.UnknownBindingField)]
    [InlineData("syntax R = \"r\" N:!Id Id declares unit N;", GrammarCodes.UnknownBindingField)]
    [InlineData("syntax R = Id declares unit this;", GrammarCodes.BindingOnAlias)]
    [InlineData("syntax R = \"r\" N:Id declares unit N declares value N;", GrammarCodes.DuplicateClause)]
    [InlineData("syntax R = \"r\" N:Id scope scope;", GrammarCodes.DuplicateClause)]
    [InlineData("builtin thing { x }", GrammarCodes.UnknownSymbolKind)]
    [InlineData("builtin value { x x }", GrammarCodes.DuplicateBuiltin)]
    [InlineData("symbols { unit }", GrammarCodes.DuplicateSymbolKind)]
    [InlineData("builtin value in Nowhere { x }", GrammarCodes.BadBuiltinScope)]
    [InlineData("builtin value in R { x } syntax R = \"r\" N:Id;", GrammarCodes.BadBuiltinScope)]
    public void Binding_errors(string member, string code) =>
        Single(code, "syntax module M { " + BindingPrelude + member + " }");

    const string Typed = """
        syntax module T
        {
          symbols { v }
          symbol property Type for v : string = "";
          token Id = ['a'..'z']+;
          syntax Let  = "let" Name:Id "=" Value:E ";" declares v Name { symbol.Type = "x"; Value.Expected = "num"; }
          syntax Opt  = "opt" Default:("=" E)? { Default.Expected = "d"; }
          syntax Many = "many" Items:E* { Items.Expected = "m"; }
          extensible syntax E
          {
            out hover Type : string = "";
            in expected Expected : string? = null;
            | Num = Id { Type = "num"; }
            | Add = E "+" E precedence 6 left { Type = E1.Type; E1.Expected = Expected; E2.Expected = Expected; check TT0001 E1.Type == E2.Type : "mismatch"; }
          }
        }
        """;

    [Fact]
    public void Valid_semantics_have_no_diagnostics() => Assert.Empty(Validate(Typed));

    [Theory]
    [InlineData("{ Type = \"num\"; }", "{ Type = \"num\"; Typo = \"x\"; }", GrammarCodes.UnknownProperty)]
    [InlineData("{ Type = \"num\"; }", "{ Type = \"num\"; Expected = \"x\"; }", GrammarCodes.WrongPropertyDirection)]
    [InlineData("E1.Expected = Expected;", "E1.Expected = Expected; E1.Type = \"\";", GrammarCodes.WrongPropertyDirection)]
    [InlineData("E1.Expected = Expected;", "E1.Expected = Expected; E3.Expected = \"\";", GrammarCodes.UnknownSemanticChild)]
    [InlineData("Value.Expected = \"num\";", "Value.Expected = \"num\"; Name.Expected = \"\";", GrammarCodes.UnknownSemanticChild)]
    [InlineData("{ Items.Expected = \"m\"; }", "{ symbol.Type = \"\"; }", GrammarCodes.SymbolWithoutDeclaration)]
    [InlineData("symbol.Type = \"x\";", "symbol.Type = \"x\"; symbol.Kind = \"\";", GrammarCodes.UnknownProperty)]
    [InlineData("for v : string = \"\";", "for v : string = \"\"; symbol property Size for q : int = 0;", GrammarCodes.UnknownSymbolKind)]
    [InlineData("in expected Expected : string? = null;", "in expected Expected : string? = null; in Expected : int = 0;", GrammarCodes.DuplicateProperty)]
    [InlineData("Value.Expected = \"num\";", "Value.Expected = \"num\"; Value.Expected = \"again\";", GrammarCodes.DuplicateProperty)]
    [InlineData("in expected Expected", "in hover expected Expected", GrammarCodes.DuplicatePropertyFlag)]
    [InlineData("syntax Many =", "syntax Alias = Let / Many { }\n  syntax Many =", GrammarCodes.SemanticsOnAlias)]
    [InlineData("| Num = Id { Type = \"num\"; }", "| Num = Id { out Extra : int = 0; Type = \"num\"; }", GrammarCodes.PropertyOnAlternative)]
    [InlineData("for v : string = \"\";", "for v : string = \"\"; symbol property Type for v : int = 0;", GrammarCodes.DuplicateProperty)]
    [InlineData("check TT0001 E1.Type == E2.Type : \"mismatch\";", "check TT0001 E1.Type == E2.Type : \"mismatch\" at E9;", GrammarCodes.UnknownSemanticChild)]
    [InlineData("{ Default.Expected = \"d\"; }", "{ Default.Expected = \"d\"; check TT0002 true : \"x\" at Default; }", GrammarCodes.UnknownSemanticChild)]
    public void Semantics_errors(string find, string replace, string code)
    {
        Assert.Contains(find, Typed);
        Single(code, Typed.Replace(find, replace));
    }

    [Fact]
    public void An_undefined_out_property_is_a_warning()
    {
        var diagnostic = Assert.Single(Validate(Typed.Replace("{ Type = \"num\"; }", "")));
        Assert.Equal((GrammarCodes.UndefinedProperty, GrammarSeverity.Warning), (diagnostic.Code, diagnostic.Severity));
    }

    [Fact]
    public void A_qualifier_must_be_a_symbol_kind()
    {
        var d = Single(GrammarCodes.UnknownSymbolKind, "syntax module M { symbols { v } token A = \"a\"; syntax R = \"r\" N:A references v N in nope; }");
        Assert.Contains("nope", d.Message);
    }

    [Fact]
    public void Lowers_arguments_must_be_labels()
    {
        var d = Single(GrammarCodes.UnknownBindingField, "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers M.Op(A, B); }");
        Assert.Contains("'B'", d.Message);
    }

    [Fact]
    public void Lowers_literal_field_must_be_a_label_or_this()
    {
        Single(GrammarCodes.UnknownBindingField, "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers literal Core.Scalar Z; }");
        Assert.Empty(Validate("syntax module M { token T = \"t\"; syntax R = \"-\" A:T lowers literal Core.Scalar this; }"));
    }

    [Theory]
    [InlineData("M.Part")]
    [InlineData("inferred")]
    public void Inline_sequence_argument_requires_a_list_field(string type)
    {
        var d = Single(GrammarCodes.SequenceArgumentNeedsList,
            "syntax module M { token T = \"t\"; syntax R = A:T lowers M.Op(sequence " + type + " A); }");
        Assert.Contains("repeated or separated list", d.Message);
    }

    const string TemplateRules = "symbols { f p } token N = ['a'..'z']+; syntax P = Name:N declares p Name type Core.Scalar; ";

    [Fact]
    public void Template_parameters_and_expansion_arguments_must_be_lists()
    {
        Assert.Contains("repeated or separated list", Single(GrammarCodes.SequenceArgumentNeedsList,
            "syntax module M { " + TemplateRules + "syntax D = Name:N Params:P Body:N declares f Name lowers template Body(Params); }").Message);
        Assert.Contains("repeated or separated list", Single(GrammarCodes.SequenceArgumentNeedsList,
            "syntax module M { " + TemplateRules + "syntax C = Name:N Args:N references f Name lowers expand Name(Args); }").Message);
    }

    [Fact]
    public void A_template_needs_a_declares_clause_and_an_expansion_a_references_clause_on_its_field()
    {
        Assert.Contains("declares", Single(GrammarCodes.InvalidTemplateClause,
            "syntax module M { " + TemplateRules + "syntax D = Name:N Params:P* Body:N lowers template Body(Params); }").Message);
        Assert.Contains("references", Single(GrammarCodes.InvalidTemplateClause,
            "syntax module M { " + TemplateRules + "syntax C = Name:N Other:N Args:N* references f Other lowers expand Name(Args); }").Message);
    }

    [Fact]
    public void Template_and_expansion_fields_must_be_labels()
    {
        Single(GrammarCodes.UnknownBindingField,
            "syntax module M { " + TemplateRules + "syntax D = Name:N Params:P* declares f Name lowers template Body(Params); }");
        Single(GrammarCodes.UnknownBindingField,
            "syntax module M { " + TemplateRules + "syntax C = Name:N references f Name lowers expand Name(Args); }");
    }

    [Fact]
    public void Value_lowering_requires_an_out_float_property()
    {
        var d = Single(GrammarCodes.InvalidValueProperty,
            "syntax module M { token T = ['0'..'9']+; syntax R = Value:T lowers value Core.Scalar Missing; }");
        Assert.Contains("out float", d.Message);
    }

    [Fact]
    public void Computed_operation_requires_an_out_signature_property()
    {
        var diagnostic = Single(GrammarCodes.InvalidValueProperty,
            "syntax module M { token T = ['0'..'9']+; syntax R = Value:T lowers operation Selected(Value) { out Selected : float? = null; Selected = null; } }");
        Assert.Contains("OperationSignature", diagnostic.Message);
    }

    [Fact]
    public void Reference_lowering_requires_a_binding_clause()
    {
        var diagnostic = Single(GrammarCodes.InvalidValueProperty,
            "syntax module M { token T = ['a'..'z']+; syntax R = Name:T lowers reference type Resolved { out Resolved : Nitrogen.Semantic.SemanticType? = null; Resolved = null; } }");
        Assert.Contains("references clause", diagnostic.Message);
    }

    [Fact]
    public void Optional_lowering_argument_requires_an_optional_field()
    {
        var d = Single(GrammarCodes.OptionalArgumentNeedsOptionalField,
            "syntax module M { token T = ['0'..'9']+; syntax R = Value:T lowers M.Build(optional Core.Scalar Value); }");
        Assert.Contains("optional element", d.Message);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("sequence")]
    public void Structured_lowers_field_must_be_a_label_or_this(string form)
    {
        Single(GrammarCodes.UnknownBindingField,
            $"syntax module M {{ token T = \"t\"; syntax R = \"r\" A:T lowers {form} M.Value Z; }}");
    }

    [Fact]
    public void A_rule_takes_one_lowers_clause()
    {
        var d = Single(GrammarCodes.DuplicateClause,
            "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers M.Op(A) lowers literal Core.Scalar A; }");
        Assert.Contains("'lowers'", d.Message);
    }

    [Fact]
    public void Declared_type_is_a_label_or_a_qualified_name()
    {
        Single(GrammarCodes.UnknownBindingField, "syntax module M { symbols { v } token T = \"t\"; syntax R = N:T declares v N type Missing; }");
        Assert.Empty(Validate("syntax module M { symbols { v } token T = \"t\"; syntax R = N:T K:T declares v N type K; }"));
        Assert.Empty(Validate("syntax module M { symbols { v } token T = \"t\"; syntax R = N:T declares v N type Units.Angle; }"));
    }

    [Fact]
    public void Lowers_only_rule_emits_no_binding_table()
    {
        var result = GrammarCompiler.Compile([new GrammarInput("m.ngr",
            "syntax module M { token T = \"t\"; syntax R = \"r\" A:T lowers M.Op(A); }", "N")]);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain("GetBinding", Assert.Single(result.Sources).Code);
    }
}
