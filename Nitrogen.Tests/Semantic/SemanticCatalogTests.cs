using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SemanticCatalogTests
{
    [Fact]
    public void Equivalent_module_exports_coalesce_and_core_is_always_available()
    {
        var unitsA = new SemanticModule("Units", ["Core"], [SemanticTypes.Angle], []);
        var unitsB = new SemanticModule("Units", ["Core"], [SemanticType.Named("Units", "Angle")], []);

        var language = new LanguageBuilder().AddSemantic(unitsA).AddSemantic(unitsB).Build();

        Assert.Equal(SemanticTypes.Angle, language.SemanticCatalog.Types["Units.Angle"]);
        Assert.Equal(SemanticTypes.Scalar, language.SemanticCatalog.Types["Core.Scalar"]);
        Assert.Equal(SemanticTypes.Text, language.SemanticCatalog.Types["Core.Text"]);
        Assert.Equal(5, language.SemanticCatalog.Types.Count);
    }

    [Fact]
    public void Transitive_imports_are_resolved_independent_of_add_order()
    {
        var units = new SemanticModule("Units", ["Core"], [SemanticTypes.Angle], []);
        var cad = new SemanticModule("Cad", ["Units"], [], [new OperationSignature("Cad.Rotate", SemanticTypes.Angle, SemanticTypes.Angle)]);

        var first = new LanguageBuilder().AddSemantic(cad).AddSemantic(units).Build().SemanticCatalog;
        var reversed = new LanguageBuilder().AddSemantic(units).AddSemantic(cad).Build().SemanticCatalog;

        Assert.Equal(first.Types.Keys.Order(), reversed.Types.Keys.Order());
        Assert.Equal(first.Operations.Keys.Order(), reversed.Operations.Keys.Order());
        Assert.Equal("Cad.Rotate", first.Operations["Cad.Rotate"].Id);
    }

    [Fact]
    public void Missing_import_and_cycle_have_deterministic_diagnostics()
    {
        var missing = new LanguageBuilder().AddSemantic(new SemanticModule("Broken", ["Missing"], [], []));
        Assert.False(missing.TryBuild(out var language, out var errors));
        Assert.Null(language);
        Assert.Contains(errors, d => d.Code == "NC0001" && d.Modules.Contains("Missing"));

        var cycle = new LanguageBuilder()
            .AddSemantic(new SemanticModule("B", ["A"], [], []))
            .AddSemantic(new SemanticModule("A", ["B"], [], []));
        Assert.False(cycle.TryBuild(out _, out var cycleErrors));
        Assert.Contains(cycleErrors, d => d.Code == "NC0002" && d.Message.Contains("A -> B -> A"));
    }

    [Fact]
    public void Conflicting_type_and_operation_exports_name_both_modules()
    {
        var plain = new SemanticModule("A", [], [SemanticType.Named("Shared", "Value")],
            [new OperationSignature("Shared.Read", SemanticTypes.Scalar)]);
        var changed = new SemanticModule("B", [], [SemanticType.Named("Shared", "Value", SemanticTypes.Bool)],
            [new OperationSignature("Shared.Read", SemanticTypes.Bool)]);

        var builder = new LanguageBuilder().AddSemantic(changed).AddSemantic(plain);
        Assert.False(builder.TryBuild(out _, out var errors));
        Assert.Equal(new[] { "NC0003", "NC0004" }, errors.Select(d => d.Code).Order());
        Assert.All(errors, d => Assert.Equal(new[] { "A", "B" }, d.Modules.Order()));
        var exception = Assert.Throws<SemanticCompositionException>(() => builder.Build());
        Assert.Equal(2, exception.Diagnostics.Count);
    }

    [Fact]
    public void Operation_arguments_are_compared_structurally()
    {
        var left = new SemanticModule("A", [], [],
            [new OperationSignature("Shared.Sum", SemanticTypes.Angle, SemanticTypes.Angle)]);
        var equal = new SemanticModule("B", [], [],
            [new OperationSignature("Shared.Sum", SemanticType.Named("Units", "Angle"), SemanticType.Named("Units", "Angle"))]);
        var different = new SemanticModule("C", [], [],
            [new OperationSignature("Shared.Sum", SemanticTypes.Angle, SemanticTypes.Scalar)]);

        Assert.True(new LanguageBuilder().AddSemantic(left).AddSemantic(equal).TryBuild(out _, out var clean));
        Assert.Empty(clean);
        Assert.False(new LanguageBuilder().AddSemantic(left).AddSemantic(different).TryBuild(out _, out var errors));
        Assert.Contains(errors, d => d.Code == "NC0004");
    }
}
