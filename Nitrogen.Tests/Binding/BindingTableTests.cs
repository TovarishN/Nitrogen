using Nitrogen.Tests.Scopes;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Binding clauses become the generated module's binding table (issue 237).</summary>
public class BindingTableTests
{
    [Fact]
    public void Clauses_become_the_modules_binding_table()
    {
        var module = ScopesModule.Instance;
        var unit = module.GetBinding(ScopesModule.LUnit)!;
        Assert.Equal("unit", unit.Declares!.Kind);
        Assert.Equal(1, unit.Declares.Child);
        Assert.True(unit.Declares.Export);
        Assert.True(unit.Scope);
        Assert.Null(unit.References);

        var member = module.GetBinding(ScopesModule.LMember)!;
        Assert.True(member.References!.Optional);
        Assert.Equal(-1, member.References.Child);
        Assert.Equal(new[] { "value", "func" }, module.GetBinding(ScopesModule.LRef)!.References!.Kinds);
        Assert.Equal(0, module.GetBinding(ScopesModule.LCall)!.References!.Child);
        Assert.True(module.GetBinding(ScopesModule.LGenName)!.Dynamic);
        Assert.Null(module.GetBinding(ScopesModule.LNum));
        Assert.Null(module.GetBinding(ScopesModule.LIdentifier));
        Assert.Null(module.GetBinding(9999));

        Assert.Equal(new[] { "value:pi,time,sys.clock@0", "func:max@0", $"value:here@{ScopesModule.LBlock}" },
            module.Builtins.Select(b => b.Kind + ":" + string.Join(",", b.Names) + "@" + b.ScopeKind));
    }
}
