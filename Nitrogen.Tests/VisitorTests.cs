using System.Globalization;
using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class VisitorTests
{
    /// <summary>Evaluates Calc expressions; Calc.Power's Pow is handled through DefaultVisit.</summary>
    sealed class Evaluator : CalcVisitor<double>
    {
        public override double VisitNum(NumExpr node) => double.Parse(node.Number.ToString(), CultureInfo.InvariantCulture);

        public override double VisitParen(ParenExpr node) => Visit(node.Expr);

        public override double VisitNeg(NegExpr node) => -Visit(node.Expr);

        public override double VisitAdd(AddExpr node) => Visit(node.Expr1) + Visit(node.Expr2);

        public override double VisitSub(SubExpr node) => Visit(node.Expr1) - Visit(node.Expr2);

        public override double VisitMul(MulExpr node) => Visit(node.Expr1) * Visit(node.Expr2);

        protected override double DefaultVisit(SyntaxTree tree, int node) =>
            SyntaxView.TryCast<PowExpr>(tree, node, out var pow)
                ? Math.Pow(Visit(pow.Expr1), Visit(pow.Expr2))
                : throw new NotSupportedException(tree.Language?.GetKindName(tree.Kind(node)));
    }

    static double Evaluate(Language language, string expr)
    {
        using var result = language.Parse(expr + ";", CalcModule.Program);
        Assert.True(result.Success);
        var tree = result.Tree;
        int statement = tree.Child(tree.Child(0, 0), 0);
        return new Evaluator().Visit(tree, tree.Child(statement, 0));
    }

    [Theory]
    [InlineData("-(1+2)*3", -9)]
    [InlineData("10-4-3", 3)]
    [InlineData("2.5*4", 10)]
    public void Visitor_dispatches_on_generated_kinds(string expr, double expected)
    {
        var calc = new LanguageBuilder().Add(CalcModule.Instance).Build();
        Assert.Equal(expected, Evaluate(calc, expr));
    }

    [Theory]
    [InlineData("2*3^2", 18)]
    [InlineData("2^3^2", 512)]
    public void Kinds_from_another_module_reach_the_default_visit(string expr, double expected)
    {
        var withPower = new LanguageBuilder().Add(CalcModule.Instance).Add(PowerModule.Instance).Build();
        Assert.Equal(expected, Evaluate(withPower, expr));
    }
}
