using Nitrogen.Tests.Calc;
using Xunit;

namespace Nitrogen.Tests;

public class ModuleApiTests
{
    [Fact]
    public void Modules_find_their_syntax_rules_by_name()
    {
        Assert.Equal("Program", CalcModule.Instance.GetRule("Program")?.Name);
        Assert.Null(CalcModule.Instance.GetRule("Expr")); // an extension point is not a start rule
        Assert.Null(CalcModule.Instance.GetRule("Nope"));
    }

    [Fact]
    public void Extension_point_ids_stay_inside_the_recovery_memo_key()
    {
        Assert.Equal((1 << 21) - 1, ExtensionPointDecl.MaxGlobalId);
        long key = ((long)ExtensionPointDecl.MaxGlobalId << 10) | (3 << 8) | 255;
        Assert.True(key <= int.MaxValue);
    }
}
