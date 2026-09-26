using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Typed;
using Xunit;

namespace Nitrogen.Tests;

public sealed class HirLoweringTests
{
    static (FileSemantics File, ParseResult Parsed) Open(string text)
    {
        var language = new LanguageBuilder().Add(TypedModule.Instance).Build();
        var parsed = language.Parse(text, TypedModule.File);
        var project = new Project(language);
        project.Set("a.typed", parsed.Tree);
        return (new ProjectSemantics(project)["a.typed"], parsed);
    }

    static SemanticCatalog Catalog(LoweringRegistration registration) =>
        new LanguageBuilder().AddSemantic(new SemanticModule("Test", [], [],
            [new OperationSignature("Test.Value", SemanticTypes.Scalar)], [registration])).Build().SemanticCatalog;

    [Fact]
    public void Valid_node_lowers_with_its_source_origin()
    {
        var (file, parsed) = Open("let x = 1;");
        using (parsed)
        {
            var result = HirLowering.Lower(file, Catalog(new LoweringRegistration(TypedKinds.Num,
                "Test.Value", (context, node) => new HirConstant(1, SemanticTypes.Scalar, context.Origin(node)))));
            Assert.Empty(result.Diagnostics);
            var root = Assert.IsType<HirConstant>(Assert.Single(result.Roots));
            Assert.Equal("a.typed", Assert.Single(root.Origins).Path);
            Assert.Equal(8, Assert.Single(root.Origins).Span.Start);
        }
    }

    [Fact]
    public void Null_result_is_a_supported_skip()
    {
        var (file, parsed) = Open("let x = 1;");
        using (parsed)
        {
            var result = HirLowering.Lower(file, Catalog(new LoweringRegistration(TypedKinds.Num,
                "Test.Value", (_, _) => null)));
            Assert.Empty(result.Roots);
            Assert.Empty(result.Diagnostics);
        }
    }

    [Fact]
    public void Lowerer_can_report_an_expected_failure_without_throwing()
    {
        var (file, parsed) = Open("let x = 1;");
        using (parsed)
        {
            var result = HirLowering.Lower(file, Catalog(new LoweringRegistration(TypedKinds.Num,
                "Test.Value", (context, node) =>
                {
                    context.Report("GXTEST", context.Origin(node), "expected failure");
                    return null;
                })));
            Assert.Empty(result.Roots);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("GXTEST", diagnostic.Code);
            Assert.Equal(8, diagnostic.Origin.Span.Start);
        }
    }

    [Fact]
    public void Opted_in_lowerer_handles_unresolved_reference_itself()
    {
        var (file, parsed) = Open("let x = unknown;");
        using (parsed)
        {
            var registration = new LoweringRegistration(TypedKinds.Ref, "Test.Value",
                (context, node) =>
                {
                    context.Report("GXLOOKUP", context.Origin(node), "missing name");
                    return null;
                }, HandlesUnresolvedReferences: true);
            var result = HirLowering.Lower(file, Catalog(registration));
            Assert.Empty(result.Roots);
            Assert.Equal("GXLOOKUP", Assert.Single(result.Diagnostics).Code);
        }
    }

    [Fact]
    public void Related_file_uses_the_same_project_semantics()
    {
        var language = new LanguageBuilder().Add(TypedModule.Instance).Build();
        using var first = language.Parse("let x = 1;", TypedModule.File);
        using var second = language.Parse("let y = 2;", TypedModule.File);
        var project = new Project(language);
        project.Set("definitions.typed", first.Tree);
        project.Set("scene.typed", second.Tree);
        var semantics = new ProjectSemantics(project);
        Assert.Same(semantics["definitions.typed"], semantics["scene.typed"].RelatedFile("definitions.typed"));
        Assert.Throws<KeyNotFoundException>(() => semantics["scene.typed"].RelatedFile("missing.typed"));
    }

    [Fact]
    public void Lowerer_exception_becomes_a_diagnostic_at_the_node()
    {
        var (file, parsed) = Open("let x = 1;");
        using (parsed)
        {
            var result = HirLowering.Lower(file, Catalog(new LoweringRegistration(TypedKinds.Num,
                "Test.Value", (_, _) => throw new InvalidOperationException("boom"))));
            Assert.Empty(result.Roots);
            Assert.Contains(result.Diagnostics, d => d.Code == "NH0004" && d.Origin.Span.Start == 8);
        }
    }

    [Fact]
    public void Conflicting_lowerer_registrations_identify_both_modules()
    {
        var one = new LoweringRegistration(TypedKinds.Num, "Test.Value", (_, _) => null);
        var two = new LoweringRegistration(TypedKinds.Num, "Test.Value", (_, _) => null);
        var signature = new OperationSignature("Test.Value", SemanticTypes.Scalar);
        var builder = new LanguageBuilder().AddSemantic(new SemanticModule("One", [], [], [signature], [one]))
            .AddSemantic(new SemanticModule("Two", [], [], [signature], [two]));
        Assert.False(builder.TryBuild(out _, out var diagnostics));
        Assert.Contains(diagnostics, d => d.Code == "NC0005" && d.Modules.SequenceEqual(["One", "Two"]));
    }

    [Fact]
    public void Recovered_construct_cannot_become_a_success_root()
    {
        var (file, parsed) = Open("let x = 1");
        using (parsed)
        {
            var result = HirLowering.Lower(file, Catalog(new LoweringRegistration(TypedKinds.Let,
                "Test.Value", (context, node) => new HirConstant(1, SemanticTypes.Scalar, context.Origin(node)))));
            Assert.Empty(result.Roots);
            Assert.Contains(result.Diagnostics, d => d.Code == "NH0001");
        }
    }

    [Fact]
    public void Unresolved_reference_cannot_become_a_success_root()
    {
        var (file, parsed) = Open("let x = unknown;");
        using (parsed)
        {
            var result = HirLowering.Lower(file, Catalog(new LoweringRegistration(TypedKinds.Ref,
                "Test.Value", (context, node) => new HirConstant(1, SemanticTypes.Scalar, context.Origin(node)))));
            Assert.Empty(result.Roots);
            Assert.Contains(result.Diagnostics, d => d.Code == "NH0002");
        }
    }

    [Fact]
    public void Invalid_semantics_cannot_become_a_success_root()
    {
        var (file, parsed) = Open("let x = true + 1;");
        using (parsed)
        {
            var result = HirLowering.Lower(file, Catalog(new LoweringRegistration(TypedKinds.Add,
                "Test.Value", (context, node) => new HirConstant(1, SemanticTypes.Scalar, context.Origin(node)))));
            Assert.Empty(result.Roots);
            Assert.Contains(result.Diagnostics, d => d.Code == "NH0003");
        }
    }

    [Fact]
    public void A_lowered_operation_must_match_the_catalog_signature()
    {
        var (file, parsed) = Open("let x = 1;");
        using (parsed)
        {
            var wrong = new OperationSignature("Test.Value", SemanticTypes.Scalar, SemanticTypes.Scalar);
            var registration = new LoweringRegistration(TypedKinds.Num, "Test.Value",
                (context, node) => new HirOperation(wrong,
                    [new HirConstant(1, SemanticTypes.Scalar, context.Origin(node))], [context.Origin(node)]));
            var result = HirLowering.Lower(file, Catalog(registration));
            Assert.Empty(result.Roots);
            Assert.Contains(result.Diagnostics, d => d.Code == "NH0003");
        }
    }
}
