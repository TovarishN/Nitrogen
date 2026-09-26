using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

public class GrammarModelTests
{
    static readonly GrammarSpan S = new(0, 0);

    static ModuleDecl SampleModule(int spanStart = 0)
    {
        var span = new GrammarSpan(spanStart, 1);
        var call = new Alternative("Call", false,
            new SequenceExpr(new Expr[]
            {
                new LabeledExpr("Callee", new ReferenceExpr("Identifier", S), S),
                new LiteralExpr("(", S),
                new LabeledExpr("Args", new SeparatedListExpr(new ReferenceExpr("Expr", S), new LiteralExpr(",", S), false, S), S),
                new LiteralExpr(")", S),
            }, S),
            null, null, S);
        var add = new Alternative("Add", false,
            new SequenceExpr(new Expr[] { new ReferenceExpr("Expr", S), new LiteralExpr("+", S), new ReferenceExpr("Expr", S) }, S),
            10, GrammarAssociativity.Left, S);
        var num = new Alternative("Number", true, new ReferenceExpr("Number", S), null, null, S);
        return new ModuleDecl("Calc",
            new[] { new UsingDecl("Base", S) },
            new RuleDecl[]
            {
                new TokenRule("Number", new RepeatExpr(RepeatKind.OneOrMore, new CharClassExpr(new[] { new CharRange('0', '9') }, false, S), S), S),
                new ExtensibleRule("Expr", new[] { num, call, add }, span),
            },
            new[] { new ExtendDecl("Other.X", new[] { new Alternative("Y", false, new LiteralExpr("y", S), 5, null, S) }, S) },
            S);
    }

    [Fact]
    public void Module_dump()
    {
        Assert.Equal(
            "(module Calc (using Base)" +
            " (token Number (plus (class '0'..'9')))" +
            " (extensible Expr" +
            " (alt Number (ref Number))" +
            " (alt Call (seq (label Callee (ref Identifier)) (lit \"(\") (label Args (sep* (ref Expr) (lit \",\"))) (lit \")\")))" +
            " (alt Add (seq (ref Expr) (lit \"+\") (ref Expr)) 10 left))" +
            " (extend Other.X (alt Y (lit \"y\") 5)))",
            GrammarDumper.Dump(SampleModule()));
    }

    [Fact]
    public void Expression_dump_covers_every_node()
    {
        Expr body = new SequenceExpr(new Expr[]
        {
            new ChoiceExpr(new Expr[] { new ReferenceExpr("A", S), new ReferenceExpr("M.B", S) }, S),
            new RepeatExpr(RepeatKind.Optional, new CharClassExpr(new[] { new CharRange('a', 'z'), new CharRange('_', '_') }, true, S), S),
            new RepeatExpr(RepeatKind.ZeroOrMore, new AnyCharExpr(S), S),
            new PredicateExpr(PredicateKind.Not, new LiteralExpr("*/", S), S),
            new PredicateExpr(PredicateKind.And, new SeparatedListExpr(new ReferenceExpr("A", S), new LiteralExpr(";", S), true, S), S),
            new LiteralExpr("\"\\\n\u0001", S),
            new CharClassExpr(new[] { new CharRange('\'', '\''), new CharRange('\t', '\t') }, false, S),
        }, S);

        Assert.Equal(
            "(seq (choice (ref A) (ref M.B)) (opt (class ^ 'a'..'z' '_')) (star any) (not (lit \"*/\"))" +
            " (and (sep+ (ref A) (lit \";\"))) (lit \"\\\"\\\\\\n\\u0001\") (class '\\'' '\\t'))",
            GrammarDumper.Dump(body));
    }

    [Fact]
    public void Unnamed_alternative_dumps_as_question_mark()
    {
        Assert.Equal("(alt ? (lit \"x\"))", GrammarDumper.Dump(new Alternative("", false, new LiteralExpr("x", S), null, null, S)));
    }

    [Fact]
    public void Models_compare_by_value_including_spans()
    {
        Assert.Equal(SampleModule(), SampleModule());
        Assert.Equal(SampleModule().GetHashCode(), SampleModule().GetHashCode());
        Assert.NotEqual(SampleModule(), SampleModule(spanStart: 5));
    }

    [Fact]
    public void Diagnostic_text()
    {
        var d = new GrammarDiagnostic(GrammarCodes.UndefinedRule, GrammarSeverity.Error, "undefined rule 'X'", new GrammarSpan(4, 1), "M");
        Assert.Equal("NGR0101 [4..5): undefined rule 'X'", d.ToString());
    }
}
