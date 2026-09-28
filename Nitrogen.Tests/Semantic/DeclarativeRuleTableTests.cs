using Nitrogen.Semantic;
using Nitrogen.Tests.Lowered;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeRuleTableTests
{
    [Fact]
    public void Generated_table_lists_lowering_and_typed_declarations()
    {
        var rules = LoweredModule.Instance.DeclarativeRules.ToDictionary(rule => rule.LocalKind);

        var add = rules[LoweredModule.LAdd];
        Assert.Equal(DeclarativeForm.Operation, add.Form);
        Assert.Equal("Test.Add", add.Target);
        Assert.Equal([2, 4], add.Arguments);

        var num = rules[LoweredModule.LNum];
        Assert.Equal(DeclarativeForm.Literal, num.Form);
        Assert.Equal("Core.Scalar", num.Target);
        Assert.Equal([0], num.Arguments);

        var input = rules[LoweredModule.LInput];
        Assert.Equal(DeclarativeForm.None, input.Form);
        Assert.Null(input.DeclaredType);
        Assert.Equal(3, input.DeclaredTypeChild);

        var fixedInput = rules[LoweredModule.LFixed];
        Assert.Equal("Units.Angle", fixedInput.DeclaredType);
        Assert.Equal(-1, fixedInput.DeclaredTypeChild);

        Assert.False(rules.ContainsKey(LoweredModule.LPlain));
        Assert.False(rules.ContainsKey(LoweredModule.LWrap));
    }

    [Fact]
    public void Modules_without_clauses_have_no_rules() =>
        Assert.Empty(Nitrogen.Tests.Scopes.ScopesModule.Instance.DeclarativeRules);
}
