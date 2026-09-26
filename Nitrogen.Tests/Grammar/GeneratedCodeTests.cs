using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class GeneratedCodeTests
{
    static readonly GrammarCompileResult Result = GrammarCompiler.Compile(GrammarCompilerTests.TestInputs());

    static string Source(string hintName) => Result.Sources.Single(s => s.HintName == hintName).Code;

    [Fact]
    public void Generated_code_compiles_against_the_runtime()
    {
        Assert.Empty(Result.Diagnostics);
        var errors = GeneratedCompilation.Errors(Result.Sources.Select(s => s.Code));
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void Calc_registers_its_alternatives_with_precedence_and_first_characters()
    {
        string calc = Source("Calc.g.cs");
        Assert.Contains("registry.Declare(Expr);", calc);
        Assert.Contains("registry.AddPrefix(Expr, new PrefixExtension(\"Num\", CalcKinds.Num, 0, &ParseExpr_Num, AsciiSet.Of(\"0123456789\")));", calc);
        Assert.Contains("registry.AddPrefix(Expr, new PrefixExtension(\"Ref\", CalcKinds.Ref, 0, &ParseExpr_Ref, AsciiSet.Of(\"ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz\")));", calc);
        Assert.Contains("registry.AddPrefix(Expr, new PrefixExtension(\"Neg\", CalcKinds.Neg, 30, &ParseExpr_Neg, AsciiSet.Of(\"-\")));", calc);
        Assert.Contains("registry.AddPostfix(Expr, new PostfixExtension(\"Add\", CalcKinds.Add, 10, Associativity.Left, &ParseExpr_Add, AsciiSet.Of(\"+\")));", calc);
    }

    [Fact]
    public void Operand_precedence_follows_the_alternative()
    {
        string calc = Source("Calc.g.cs");
        Assert.Contains("s.ParseExtensible(Instance.Expr, 30)", calc);   // Neg's operand
        Assert.Contains("s.ParseExtensible(Instance.Expr, 10)", calc);   // Add's right operand
        Assert.Contains("s.ParseExtensible(Instance.Expr, 20)", calc);   // Mul's right operand

        string power = Source("Calc.Power.g.cs");
        Assert.Contains(
            "registry.AddPostfix(CalcModule.Instance.Expr, new PostfixExtension(\"Pow\", PowerKinds.Pow, 25, Associativity.Right, &ParseExpr_Pow, AsciiSet.Of(\"^\")));",
            power);
        Assert.Contains("s.ParseExtensible(CalcModule.Instance.Expr, 24)", power);   // same namespace: unqualified
    }

    [Fact]
    public void Aliases_have_no_kind_and_keywords_need_a_boundary()
    {
        string calc = Source("Calc.g.cs");
        Assert.DoesNotContain("LStatement", calc);
        Assert.Contains("internal static bool ParseStatement(ref ParserState s) =>", calc);

        string mini = Source("Mini.g.cs");
        Assert.Contains("s.MatchKeyword(\"if\")", mini);
        Assert.Contains("s.MatchLiteral(\"(\")", mini);
    }

    [Fact]
    public void Views_name_unlabeled_references_after_their_rule()
    {
        string calc = Source("Calc.g.cs");
        Assert.Contains("public Token Callee => new(Tree, Tree.Child(Index, 0));", calc);
        Assert.Contains("public SeparatedList<ExprNode> Args => new(Tree, Tree.Child(Index, 2));", calc);
        Assert.Contains("public ExprNode Expr1 => new(Tree, Tree.Child(Index, 0));", calc);
        Assert.Contains("public ExprNode Expr2 => new(Tree, Tree.Child(Index, 2));", calc);
        Assert.Contains("public SyntaxList<SyntaxNode> Statements => new(Tree, Tree.Child(Index, 0));", calc);
        Assert.Contains("public Token Number => new(Tree, Tree.Child(Index, 0));", calc);
    }

    [Fact]
    public void Except_compiles_to_a_span_switch()
    {
        string lexical = Source("Lexical.g.cs");
        Assert.Contains("static bool IsReservedWord(ReadOnlySpan<char> word) => word switch", lexical);
        Assert.Contains("\"pick\" or \"tag\" => true,", lexical);
    }

    [Fact]
    public void Literal_led_rules_check_the_first_character_before_bookkeeping()
    {
        string mini = Source("Mini.g.cs");
        Assert.Contains(
            "    internal static bool ParsePair(ref ParserState s)\n    {\n        s.SkipTrivia();\n        if (s.Current != '(') return s.ExpectLiteral(\"(\");\n        var start = s.Mark();",
            mini);
        Assert.Contains("if (s.Current != 'i') return s.ExpectLiteral(\"if\");", mini);
    }
}
