using Nitrogen.Semantic;
using Nitrogen.Tests.Lowered;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeCompositionTests
{
    internal static readonly SemanticType Angle = SemanticType.Named("Units", "Angle");
    internal static readonly OperationSignature Add = new("Test.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    internal static readonly OperationSignature Turn = new("Test.Turn", Angle, Angle);

    internal static SemanticModule Units() => new("Units", [], [Angle], []);
    internal static SemanticModule Test(params OperationSignature[] operations) => new("Test", ["Units"], [], operations);

    internal static Language Build(params SemanticModule[] extra)
    {
        var builder = new LanguageBuilder().Add(LoweredModule.Instance).AddSemantic(Units()).AddSemantic(Test(Add, Turn));
        foreach (var module in extra) builder.AddSemantic(module);
        return builder.Build();
    }

    static IReadOnlyList<CompositionDiagnostic> Fail(params SemanticModule[] modules)
    {
        var builder = new LanguageBuilder().Add(LoweredModule.Instance);
        foreach (var module in modules) builder.AddSemantic(module);
        Assert.False(builder.TryBuild(out var language, out var diagnostics));
        Assert.Null(language);
        return diagnostics;
    }

    [Fact]
    public void Operation_rules_register_lowerers()
    {
        var catalog = Build().SemanticCatalog;
        Assert.Equal("Test.Add", Assert.Single(catalog.LowerersFor(LoweredKinds.Add)).OperationId);
        Assert.Equal("Test.Turn", Assert.Single(catalog.LowerersFor(LoweredKinds.Turn)).OperationId);
        Assert.Empty(catalog.LowerersFor(LoweredKinds.Num));
    }

    [Fact]
    public void Unknown_operation_is_NM0008()
    {
        var diagnostic = Assert.Single(Fail(Units(), Test(Add)));
        Assert.Equal("NM0008", diagnostic.Code);
        Assert.Contains("Test.Turn", diagnostic.Message);
    }

    [Fact]
    public void Unknown_declared_type_is_NM0009()
    {
        var diagnostic = Assert.Single(Fail(new SemanticModule("Test", [], [],
            [Add, new OperationSignature("Test.Turn", SemanticTypes.Scalar, SemanticTypes.Scalar)])));
        Assert.Equal("NM0009", diagnostic.Code);
        Assert.Contains("Units.Angle", diagnostic.Message);
    }

    [Fact]
    public void Argument_count_mismatch_is_NM0010()
    {
        var diagnostic = Assert.Single(Fail(Units(), Test(Add, new OperationSignature("Test.Turn", Angle, Angle, Angle))));
        Assert.Equal("NM0010", diagnostic.Code);
    }

    [Fact]
    public void Declarative_and_host_lowerer_for_one_kind_is_NC0005()
    {
        var host = new LoweringRegistration(LoweredKinds.Add, Add.Id, (_, _) => null);
        var diagnostic = Assert.Single(Fail(Units(), new SemanticModule("Test", ["Units"], [], [Add, Turn], [host])));
        Assert.Equal("NC0005", diagnostic.Code);
    }

    [Fact]
    public void Language_without_rules_builds_unchanged()
    {
        var language = new LanguageBuilder().Add(Nitrogen.Tests.Scopes.ScopesModule.Instance).Build();
        Assert.True(language.Declarative.IsEmpty);
    }
}
