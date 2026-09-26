using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Base;
using Nitrogen.Tests.Calc;
using Nitrogen.Tests.Uses;
using Xunit;

namespace Nitrogen.Tests;

public sealed class IndependentModuleTests
{
    [Fact]
    public void Independent_syntax_and_semantic_modules_lower_one_document()
    {
        var registration = new LoweringRegistration(UsesKinds.Doc, "Test.Doc",
            (context, node) => new HirConstant(1, SemanticTypes.Scalar, context.Origin(node)));
        var language = new LanguageBuilder().Add(BaseModule.Instance).Add(UsesModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticType.Named("Units", "Angle")], []))
            .AddSemantic(new SemanticModule("Test", ["Units"], [],
                [new OperationSignature("Test.Doc", SemanticTypes.Scalar)], [registration])).Build();
        using var parsed = language.Parse(CrossModuleTests.Sample, UsesModule.Doc);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("a.uses", parsed.Tree);
        var lowered = HirLowering.Lower(new ProjectSemantics(project)["a.uses"], language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        Assert.Equal("a.uses", Assert.Single(Assert.Single(lowered.Roots).Origins).Path);
    }

    [Fact]
    public void Duplicate_independent_syntax_extensions_name_both_contributors()
    {
        var error = Assert.Throws<LanguageCompositionException>(() => new LanguageBuilder()
            .Add(CalcModule.Instance).Add(ClashModule.Instance).Add(ClashCopyModule.Instance).Build());
        Assert.Contains("Calc.Clash", error.Message);
        Assert.Contains("Calc.ClashCopy", error.Message);
        Assert.Contains("Invoke", error.Message);
    }

    [Fact]
    public void Ambiguity_names_the_owning_modules()
    {
        var language = new LanguageBuilder().Add(CalcModule.Instance).Add(ClashModule.Instance).Build();
        using var result = language.Parse("f(1);", CalcModule.Program);
        var diagnostic = Assert.Single(result.Diagnostics.ToArray());
        var message = result.FormatMessage(diagnostic);
        Assert.Contains("Calc.Call", message);
        Assert.Contains("Calc.Clash.Invoke", message);
    }

    [Fact]
    public void Semantic_conflicts_have_identical_diagnostics_in_either_order()
    {
        var one = new SemanticModule("One", [], [],
            [new OperationSignature("Shared.Value", SemanticTypes.Angle)]);
        var two = new SemanticModule("Two", [], [],
            [new OperationSignature("Shared.Value", SemanticTypes.Bool)]);
        new LanguageBuilder().AddSemantic(one).AddSemantic(two).TryBuild(out _, out var forward);
        new LanguageBuilder().AddSemantic(two).AddSemantic(one).TryBuild(out _, out var reverse);
        Assert.Equal(forward.Select(d => (d.Code, d.Message)), reverse.Select(d => (d.Code, d.Message)));
    }
}
