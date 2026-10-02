using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.PropertyLowering;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public static class DynamicSignatures
{
    public static readonly OperationSignature AddScalars = new("Test.AddScalars", SemanticTypes.Scalar,
        SemanticTypes.Scalar, SemanticTypes.Scalar);
    public static readonly OperationSignature AddAngles = new("Test.AddAngles", SemanticTypes.Angle,
        SemanticTypes.Angle, SemanticTypes.Angle);
}

public sealed class PropertyLoweringTests
{
    [Fact]
    public void Nested_value_lowering_exposes_an_optional_operation_with_its_source_origin()
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse("optional-dynamic 1+1", PropertyLoweringModule.OptionalDynamicFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("nested-value.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["nested-value.txt"];
        Assert.Empty(file.Diagnostics());
        var snapshot = Guid.Parse("1224c4ed-16b4-46a2-b975-4916a29d06fa");

        var operation = Assert.IsType<HirOperation>(HirLowering.LowerNested(
            new LoweringContext(file, snapshot), file.Tree.Root));

        Assert.Equal("Test.AddScalars", operation.Signature.Id);
        Assert.Equal("nested-value.txt", operation.Origins[0].Path);
        Assert.Equal(snapshot, operation.Origins[0].SnapshotId);
        Assert.Equal(file.Tree.Span(file.Tree.Root), operation.Origins[0].Span);
    }

    [Fact]
    public void Optional_computed_operation_skips_uncovered_operand_types_without_a_diagnostic()
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse("optional-dynamic 1+2", PropertyLoweringModule.OptionalDynamicFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("optional-dynamic.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["optional-dynamic.txt"];

        Assert.Empty(file.Diagnostics());
        Assert.Null(file.DeclarativeTypes.TypeOf(file.Tree.Root));
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        Assert.Empty(lowered.Roots);
    }

    [Fact]
    public void Optional_computed_operation_lowers_inside_a_supported_parent()
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse("root optional-dynamic 1+1", PropertyLoweringModule.OptionalDynamicRoot);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("nested-dynamic.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["nested-dynamic.txt"];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal("Test.Size", root.Signature.Id);
        var inner = Assert.IsType<HirOperation>(Assert.Single(root.Arguments));
        Assert.Equal("Test.AddScalars", inner.Signature.Id);
    }

    [Fact]
    public void Declarative_reference_retains_the_bound_symbol_in_hir()
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse("input size use size", PropertyLoweringModule.ReferenceFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("reference.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["reference.txt"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var operation = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var reference = Assert.IsType<HirSymbolRef>(Assert.Single(operation.Arguments));
        Assert.Equal(SemanticTypes.Scalar, reference.Type);
        Assert.Equal("size", reference.Symbol.Binding.Name);
        Assert.Equal(15, reference.Origins[0].Span.Start);

        var size = SemanticType.Named("Test", "Size");
        var registry = new ProjectionRegistry(language.SemanticCatalog,
        [new ProjectionHandler(operation.Signature, args =>
            new ProjectedValue(size, (float)args[0].Value * 2f))]);
        var projected = HirProjector.Project(operation, registry,
            new Dictionary<Nitrogen.Binding.Symbol, ProjectedValue>
            {
                [reference.Symbol.Binding] = new(SemanticTypes.Scalar, 4f),
            });
        Assert.Empty(projected.Diagnostics);
        Assert.Equal(8f, projected.Value!.Value);
    }

    [Fact]
    public void Reference_type_must_be_exported_by_the_semantic_catalog()
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse("input missing use missing", PropertyLoweringModule.ReferenceFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("missing-reference.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["missing-reference.txt"];

        Assert.Contains(file.Diagnostics(), diagnostic => diagnostic.Code == "NT0002");
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
    }

    [Fact]
    public void Declarative_reference_can_lower_a_builtin_symbol()
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse("builtin gain", PropertyLoweringModule.BuiltinReferenceFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("builtin.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["builtin.txt"];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var operation = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var reference = Assert.IsType<HirSymbolRef>(Assert.Single(operation.Arguments));
        Assert.True(reference.Symbol.Binding.IsBuiltin);
    }

    [Theory]
    [InlineData("dynamic 1+1", "Test.AddScalars", "Core.Scalar")]
    [InlineData("dynamic 2+2", "Test.AddAngles", "Units.Angle")]
    public void Computed_operation_uses_the_checked_operand_types(string source, string operationId, string typeId)
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse(source, PropertyLoweringModule.DynamicFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("dynamic.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["dynamic.txt"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var operation = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal(operationId, operation.Signature.Id);
        Assert.Equal(typeId, operation.Type.Id);
    }

    [Fact]
    public void Missing_computed_operation_is_diagnosed_before_lowering()
    {
        var language = DynamicLanguage();
        using var parsed = language.Parse("dynamic 1+2", PropertyLoweringModule.DynamicFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("mixed.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["mixed.txt"];

        Assert.Contains(file.Diagnostics(), diagnostic => diagnostic.Code == "NT0006");
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
    }

    static Language DynamicLanguage()
    {
        var size = SemanticType.Named("Test", "Size");
        return new LanguageBuilder().Add(PropertyLoweringModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
            .AddSemantic(new SemanticModule("Test", [], [size],
            [
                new OperationSignature("Test.Size", size, SemanticTypes.Scalar),
                new OperationSignature("Test.OptionalSize", size, SemanticTypes.OptionalOf(SemanticTypes.Scalar)),
                new OperationSignature("Test.Turn", SemanticTypes.Angle, SemanticTypes.Angle),
                DynamicSignatures.AddScalars, DynamicSignatures.AddAngles,
            ])).Build();
    }

    [Theory]
    [InlineData("typed 1", "Core.Scalar")]
    [InlineData("typed 2", "Units.Angle")]
    public void Computed_value_uses_its_declared_semantic_type(string source, string expected)
    {
        var language = new LanguageBuilder().Add(PropertyLoweringModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
            .AddSemantic(new SemanticModule("Test", [], [SemanticType.Named("Test", "Size")],
            [
                new OperationSignature("Test.Size", SemanticType.Named("Test", "Size"), SemanticTypes.Scalar),
                new OperationSignature("Test.OptionalSize", SemanticType.Named("Test", "Size"),
                    SemanticTypes.OptionalOf(SemanticTypes.Scalar)),
                new OperationSignature("Test.Turn", SemanticTypes.Angle, SemanticTypes.Angle),
            ])).Build();
        using var parsed = language.Parse(source, PropertyLoweringModule.TypedFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("typed.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["typed.txt"];

        Assert.Empty(file.Diagnostics());
        var node = Enumerable.Range(0, file.Tree.NodeCount)
            .Single(candidate => file.Tree.Kind(candidate) == PropertyLoweringKinds.TypedAmount);
        Assert.Equal(expected, file.DeclarativeTypes.TypeOf(node)!.Id);
    }

    [Fact]
    public void Computed_type_reaches_hir_and_unknown_type_reports_a_diagnostic()
    {
        var size = SemanticType.Named("Test", "Size");
        var language = new LanguageBuilder().Add(PropertyLoweringModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
            .AddSemantic(new SemanticModule("Test", [], [size],
            [
                new OperationSignature("Test.Size", size, SemanticTypes.Scalar),
                new OperationSignature("Test.OptionalSize", size, SemanticTypes.OptionalOf(SemanticTypes.Scalar)),
                new OperationSignature("Test.Turn", SemanticTypes.Angle, SemanticTypes.Angle),
            ])).Build();

        using var valid = language.Parse("angle 2", PropertyLoweringModule.TypedAngleFile);
        Assert.True(valid.Success);
        var project = new Project(language);
        project.Set("angle.txt", valid.Tree);
        var file = new ProjectSemantics(project)["angle.txt"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var operation = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var value = Assert.IsType<HirConstant>(Assert.Single(operation.Arguments));
        Assert.Equal(SemanticTypes.Angle, value.Type);
        Assert.Equal(2f, value.Value);

        using var invalid = language.Parse("typed 3", PropertyLoweringModule.TypedFile);
        Assert.True(invalid.Success);
        project.Set("missing-type.txt", invalid.Tree);
        var badFile = new ProjectSemantics(project)["missing-type.txt"];
        Assert.Contains(badFile.Diagnostics(), diagnostic => diagnostic.Code == "NT0002");
    }

    [Fact]
    public void Optional_argument_requires_the_matching_operation_input()
    {
        var size = SemanticType.Named("Test", "Size");
        var builder = new LanguageBuilder().Add(PropertyLoweringModule.Instance)
            .AddSemantic(new SemanticModule("Test", [], [size],
            [
                new OperationSignature("Test.Size", size, SemanticTypes.Scalar),
                new OperationSignature("Test.OptionalSize", size, SemanticTypes.Scalar),
            ]));

        Assert.False(builder.TryBuild(out _, out var diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0011" &&
            diagnostic.Message.Contains("an optional of Core.Scalar", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("optional", false)]
    [InlineData("optional 3", true)]
    public void Optional_argument_preserves_presence_and_computed_value(string source, bool present)
    {
        var size = SemanticType.Named("Test", "Size");
        var sizeOperation = new OperationSignature("Test.Size", size, SemanticTypes.Scalar);
        var optionalOperation = new OperationSignature("Test.OptionalSize", size,
            SemanticTypes.OptionalOf(SemanticTypes.Scalar));
        var language = new LanguageBuilder().Add(PropertyLoweringModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
            .AddSemantic(new SemanticModule("Test", [], [size], [sizeOperation, optionalOperation,
                new OperationSignature("Test.Turn", SemanticTypes.Angle, SemanticTypes.Angle)])).Build();
        using var parsed = language.Parse(source, PropertyLoweringModule.OptionalFile);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("optional.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["optional.txt"];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var optional = Assert.IsType<HirOptional>(Assert.Single(root.Arguments));
        Assert.Equal(present, optional.Value is not null);
        if (present) Assert.Equal(6f, Assert.IsType<HirConstant>(optional.Value).Value);
    }

    [Fact]
    public void Missing_computed_value_reports_a_semantic_error_and_blocks_lowering()
    {
        var size = SemanticType.Named("Test", "Size");
        var operation = new OperationSignature("Test.Size", size, SemanticTypes.Scalar);
        var optionalOperation = new OperationSignature("Test.OptionalSize", size,
            SemanticTypes.OptionalOf(SemanticTypes.Scalar));
        var language = new LanguageBuilder().Add(PropertyLoweringModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
            .AddSemantic(new SemanticModule("Test", [], [size], [operation, optionalOperation,
                new OperationSignature("Test.Turn", SemanticTypes.Angle, SemanticTypes.Angle)])).Build();
        using var parsed = language.Parse("size 0", PropertyLoweringModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("bad-size.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["bad-size.txt"];

        var error = Assert.Single(file.Diagnostics(), diagnostic => diagnostic.Code == "NT0003");
        Assert.Equal(5, error.Span.Start);
        var lowered = HirLowering.Lower(file, language.SemanticCatalog);
        Assert.Empty(lowered.Roots);
        Assert.Contains(lowered.Diagnostics, diagnostic => diagnostic.Code == "NH0003");
    }

    [Fact]
    public void Semantic_float_property_lowers_as_a_source_linked_constant()
    {
        var size = SemanticType.Named("Test", "Size");
        var operation = new OperationSignature("Test.Size", size, SemanticTypes.Scalar);
        var optionalOperation = new OperationSignature("Test.OptionalSize", size,
            SemanticTypes.OptionalOf(SemanticTypes.Scalar));
        var language = new LanguageBuilder().Add(PropertyLoweringModule.Instance)
            .AddSemantic(new SemanticModule("Units", [], [SemanticTypes.Angle], []))
            .AddSemantic(new SemanticModule("Test", [], [size], [operation, optionalOperation,
                new OperationSignature("Test.Turn", SemanticTypes.Angle, SemanticTypes.Angle)])).Build();
        using var parsed = language.Parse("size 3", PropertyLoweringModule.File);
        Assert.True(parsed.Success);
        var project = new Project(language);
        project.Set("size.txt", parsed.Tree);
        var file = new ProjectSemantics(project)["size.txt"];
        Assert.Empty(file.Diagnostics());

        var lowered = HirLowering.Lower(file, language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        var amount = Assert.IsType<HirConstant>(Assert.Single(root.Arguments));
        Assert.Equal(6f, amount.Value);
        Assert.Equal(5, amount.Origins[0].Span.Start);
    }
}
