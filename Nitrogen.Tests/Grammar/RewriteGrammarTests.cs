using Nitrogen.Grammar;
using Nitrogen.Semantic;
using Nitrogen.Tests.Rewrites;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The rewrites clause from grammar text to generated rules (spec: quasiquote rewrites).</summary>
public class RewriteGrammarTests
{
    static BindingClause RewriteClause(string grammar)
    {
        var result = GrammarParser.Parse(grammar);
        Assert.True(result.Success, result.Diagnostics.Count > 0 ? result.Diagnostics[0].ToString() : "");
        var rule = (SyntaxRule)result.File!.Modules[0].Rules.Single(r => r.Name == "R");
        return rule.Clauses.Single(c => c.Kind == BindingClauseKind.Rewrites);
    }

    [Fact]
    public void The_template_is_kept_verbatim_with_its_span()
    {
        const string grammar = "syntax module M { token N = ['0'..'9']+; syntax R = \"r\" X:N  rewrites <[ a\n  ]>; }";
        var clause = RewriteClause(grammar);
        int open = grammar.IndexOf("<[", StringComparison.Ordinal) + 2;
        Assert.Equal(" a\n  ", clause.Target!.Name);
        Assert.Equal(open, clause.Target.Span.Start);
        Assert.Equal(grammar.IndexOf("rewrites", StringComparison.Ordinal), clause.Span.Start);
        Assert.Equal(grammar.IndexOf("]>", StringComparison.Ordinal) + 2, clause.Span.End);
    }

    static GrammarDiagnostic[] Validate(string grammar) =>
        GrammarValidator.Validate(GrammarParser.Parse(grammar).File!.Modules).ToArray();

    [Fact]
    public void An_unknown_hole_and_a_hole_for_a_token_are_errors()
    {
        var unknown = Assert.Single(Validate("syntax module M { token N = ['0'..'9']+; syntax R = \"r\" X:S  rewrites <[ $Y ]>; syntax S = N; }"));
        Assert.Equal("NGR0135", unknown.Code);
        Assert.Equal("'$Y' is not a field of R", unknown.Message);
        var alternative = Assert.Single(Validate("syntax module M { token N = ['0'..'9']+; syntax S = N; extensible syntax E { | Twice = X:S \"twice\"  rewrites <[ $Y ]> } }"));
        Assert.Equal("'$Y' is not a field of Twice", alternative.Message);
        var token = Assert.Single(Validate("syntax module M { token N = ['0'..'9']+; syntax R = \"r\" X:N  rewrites <[ $X ]>; }"));
        Assert.Equal("NGR0136", token.Code);
    }

    [Fact]
    public void A_rule_takes_lowers_or_rewrites_not_both()
    {
        var both = Validate("syntax module M { token N = ['0'..'9']+; syntax S = N; syntax R = \"r\" X:S  lowers literal Core.Scalar X  rewrites <[ $X ]>; }");
        Assert.Contains(both, d => d.Code == GrammarCodes.DuplicateClause);
    }

    [Fact]
    public void The_generated_rules_carry_the_template_its_category_and_its_holes()
    {
        var rewrites = RewritesModule.Instance.DeclarativeRules.Where(r => r.Form == DeclarativeForm.Rewrite).ToList();
        var doubled = Assert.Single(rewrites, r => r.Rewrite!.Category == "Rewrites.Expr").Rewrite!;
        Assert.Equal(" $Inner * 2 ", doubled.Text);
        Assert.Equal(new RewriteHole("Inner", 0, "Rewrites.Expr"), Assert.Single(doubled.Holes));

        // A plain rule listed by one alias parses its template as that alias.
        var twice = Assert.Single(rewrites, r => r.Rewrite!.Category == "Rewrites.Statement").Rewrite!;
        Assert.Equal(new RewriteHole("Value", 1, "Rewrites.Expr"), Assert.Single(twice.Holes));
    }
}
