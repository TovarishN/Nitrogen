using Nitrogen.Tests.Base;
using Nitrogen.Tests.Uses;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>A module using another module's tokens, rules and extension point (issue 236).</summary>
public class CrossModuleTests
{
    static readonly Language Uses = new LanguageBuilder().Add(BaseModule.Instance).Add(UsesModule.Instance).Build();

    internal const string Sample = "doc let x = -1; (y) end";

    internal const string Expected =
        "(Doc \"doc\" (List (Assign \"let\" Word:\"x\" \"=\" (Neg \"-\" (Num Number:\"1\")) \";\") (Pair \"(\" Word:\"y\" \")\")) \"end\")";

    [Fact]
    public void A_module_uses_another_modules_tokens_rules_and_extension_point()
    {
        using var result = Uses.Parse(Sample, UsesModule.Doc);
        Assert.True(result.Success, result.Success ? "" : result.FormatMessage(result.Diagnostics[0]));
        Assert.Equal(Expected, SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void Recovery_sites_reach_into_the_other_module()
    {
        using var result = Uses.Parse("doc let x = ; end", UsesModule.Doc);
        Assert.Contains("(Assign \"let\" Word:\"x\" \"=\" !Error \";\")", SyntaxDumper.Dump(result.Tree));
        Assert.Single(result.Diagnostics.ToArray());
    }
}
