using DateCalc.Syntax;
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DateCalcPatternTests
{
    [Fact]
    public void The_CSharp_demo_constructs_owned_syntax_and_retains_operand_origins()
    {
        var language=DateCalcEvaluator.Language;
        using var source=language.ParseTemplate("1 + 2 * 3","DateCalc.Expr",new Dictionary<string,string>());
        var origin=new SourceOrigin("demo.datecalc",Guid.NewGuid(),source.Tree.Root,source.Tree.Span(source.Tree.Root));
        Assert.True(PatternMatching.TrySwapAddition(language,new(source.Tree,source.Tree.Root,origin),out var result));
        using var owned=result!;
        source.Dispose();
        Assert.True(owned.Success);
        var hir=Assert.Single(TemplateListInstantiationTests.Lower(language,owned.Tree!).Roots);
        Assert.Equal("DateCalc.Add(DateCalc.Multiply(2:Core.Scalar,3:Core.Scalar),1:Core.Scalar)",TemplateListInstantiationTests.Shape(hir));
        Assert.Contains(origin,owned.Origins.Values);
    }

    [Theory]
    [InlineData("let count = 3; count + 2 swapped;","3","5")]
    [InlineData("let count = 3; count swapped;","3","3")]
    [InlineData("let count = 3; (count + 2) swapped;","3","5")]
    [InlineData("let count = 3; 2 weeks + count days swapped;","3","17 days")]
    public void The_grammar_demo_matches_original_syntax_and_preserves_caller_values(string source,string first,string last)
    {
        var lines=DateCalcEvaluator.Run(source);
        Assert.All(lines,line=>Assert.False(line.IsError,line.Text));
        Assert.Equal(new[]{first,last},lines.Select(line=>line.Text));
    }

    [Fact]
    public void Matched_operands_lower_in_swapped_order_with_original_initializer_origins()
    {
        const string text="let count = 3; count + 2 swapped;";
        var language=DateCalcEvaluator.Language;
        using var parsed=language.Parse(text,DateCalcComposition.Start);
        var project=new Project(language);
        project.Set("caller.datecalc",parsed.Tree);
        var file=new ProjectSemantics(project)["caller.datecalc"];
        var lowered=HirLowering.LowerSelected(file,DateCalcEvaluator.Profile.StatementKinds,Guid.NewGuid());
        Assert.Empty(lowered.Diagnostics);
        var root=Assert.IsType<HirOperation>(lowered.Roots.Last());
        Assert.Equal("DateCalc.Add(2:Core.Scalar,3:Core.Scalar)",TemplateListInstantiationTests.Shape(root));
        Assert.Contains(root.Arguments[1].Origins,origin=>origin.Path=="caller.datecalc" && origin.Span.Start==text.IndexOf('3'));
    }

    [Fact]
    public void The_CSharp_demo_returns_no_result_for_an_ordinary_mismatch()
    {
        var language=DateCalcEvaluator.Language;
        using var parsed=language.ParseTemplate("1","DateCalc.Expr",new Dictionary<string,string>());
        Assert.False(PatternMatching.TrySwapAddition(language,new(parsed.Tree,parsed.Tree.Root),out var result));
        Assert.Null(result);
    }
}
