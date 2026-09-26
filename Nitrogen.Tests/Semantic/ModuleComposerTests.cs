using Nitrogen.Semantic;
using Nitrogen.Binding;
using Nitrogen.Semantics;
using Nitrogen.Tests.Base;
using Nitrogen.Tests.Calc;
using Nitrogen.Tests.Uses;
using Xunit;

namespace Nitrogen.Tests;

public sealed class ModuleComposerTests
{
    [Fact]
    public void Composes_syntax_semantics_and_a_named_start_rule()
    {
        var units = new ModuleDescriptor("Units", null,
            new SemanticModule("Units", [], [SemanticTypes.Angle], []), [], []);
        var uses = new ModuleDescriptor("Uses", UsesModule.Instance,
            new SemanticModule("NeedsUnits", ["Units"], [], []), ["Doc"], []);
        var grammar = new ModuleDescriptor("Base", BaseModule.Instance, null, [], []);

        Assert.True(ModuleComposer.TryCompose([uses, units, grammar], [], out var composed, out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.NotNull(composed);
        Assert.Equal("Doc", composed.StartRules[("Uses", "Doc")].Name);
        using var parsed = composed.Language.Parse(CrossModuleTests.Sample, composed.StartRules[("Uses", "Doc")]);
        Assert.True(parsed.Success);
    }

    [Fact]
    public void Missing_import_is_returned_without_a_partial_result()
    {
        var needsUnits = new ModuleDescriptor("Test", null,
            new SemanticModule("NeedsUnits", ["Units"], [], []), [], []);

        Assert.False(ModuleComposer.TryCompose([needsUnits], [], out var composed, out var diagnostics));
        Assert.Null(composed);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NC0001");
    }

    [Fact]
    public void Duplicate_descriptor_id_is_rejected_even_for_identical_instances()
    {
        var descriptor = new ModuleDescriptor("Units", null,
            new SemanticModule("Units", [], [SemanticTypes.Angle], []), [], []);

        Assert.False(ModuleComposer.TryCompose([descriptor, descriptor], [], out var composed, out var diagnostics));
        Assert.Null(composed);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0001" &&
            diagnostic.Modules.Contains("Units"));
    }

    [Fact]
    public void Unknown_start_rule_is_rejected_with_its_module_id()
    {
        var descriptor = new ModuleDescriptor("Uses", UsesModule.Instance, null, ["Missing"], []);
        var grammar = new ModuleDescriptor("Base", BaseModule.Instance, null, [], []);

        Assert.False(ModuleComposer.TryCompose([descriptor, grammar], [], out var composed, out var diagnostics));
        Assert.Null(composed);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "NM0002" &&
            diagnostic.Modules.Contains("Uses") && diagnostic.Message.Contains("Missing"));
    }

    [Fact]
    public void Contract_diagnostics_are_stable_across_descriptor_order()
    {
        var a = new ModuleDescriptor("A", null, new SemanticModule("A", ["Gone"], [], []), [], []);
        var z = new ModuleDescriptor("Z", UsesModule.Instance, null, ["Missing"], []);

        ModuleComposer.TryCompose([z, a], [], out _, out var forward);
        ModuleComposer.TryCompose([a, z], [], out _, out var reverse);

        Assert.Equal(forward.Select(d => (d.Code, d.Message)), reverse.Select(d => (d.Code, d.Message)));
    }

    [Fact]
    public void Compose_throws_structured_diagnostics_on_failure()
    {
        var descriptor = new ModuleDescriptor("Uses", UsesModule.Instance, null, ["Missing"], []);
        var grammar = new ModuleDescriptor("Base", BaseModule.Instance, null, [], []);

        var error = Assert.Throws<SemanticCompositionException>(() => ModuleComposer.Compose([descriptor, grammar], []));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "NM0002");
    }

    [Fact]
    public void Exact_host_binding_is_published_without_invoking_its_handler()
    {
        var signature = RotateSignature();
        var descriptor = Requires("Test", signature, export: true);
        int calls = 0;
        Func<object?> handler = () => { calls++; return null; };
        var binding = new HostOperationBinding(signature, handler);

        Assert.True(ModuleComposer.TryCompose([descriptor], [binding], out var composed, out var errors));
        Assert.Empty(errors);
        Assert.Same(binding, composed!.HostBindings[signature.Id]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Required_operation_must_be_exported_and_bound()
    {
        var signature = RotateSignature();
        var binding = new HostOperationBinding(signature, (Func<object?>)(() => null));

        Assert.False(ModuleComposer.TryCompose([Requires("Unexported", signature, export: false)], [binding],
            out var unexported, out var exportErrors));
        Assert.Null(unexported);
        Assert.Contains(exportErrors, error => error.Code == "NM0003" && error.Modules.Contains("Unexported"));

        Assert.False(ModuleComposer.TryCompose([Requires("Unbound", signature, export: true)], [],
            out var unbound, out var bindingErrors));
        Assert.Null(unbound);
        Assert.Contains(bindingErrors, error => error.Code == "NM0004" && error.Modules.Contains("Unbound"));
    }

    [Fact]
    public void Host_binding_must_match_ordered_inputs_and_result()
    {
        var signature = RotateSignature();
        var reversed = new OperationSignature("Test.Rotate", SemanticTypes.Angle,
            SemanticTypes.Scalar, SemanticTypes.Angle);
        var differentResult = new OperationSignature("Test.Rotate", SemanticTypes.Scalar,
            SemanticTypes.Angle, SemanticTypes.Scalar);
        var descriptor = Requires("Test", signature, export: true);

        Assert.False(ModuleComposer.TryCompose([descriptor],
            [new HostOperationBinding(reversed, (Func<object?>)(() => null))], out _, out var inputErrors));
        Assert.Contains(inputErrors, error => error.Code == "NM0005");
        Assert.False(ModuleComposer.TryCompose([descriptor],
            [new HostOperationBinding(differentResult, (Func<object?>)(() => null))], out _, out var resultErrors));
        Assert.Contains(resultErrors, error => error.Code == "NM0005");
    }

    [Fact]
    public void Requirement_must_match_the_catalog_export()
    {
        var signature = RotateSignature();
        var different = new OperationSignature("Test.Rotate", SemanticTypes.Scalar,
            SemanticTypes.Angle, SemanticTypes.Scalar);
        var descriptor = new ModuleDescriptor("Test", null,
            new SemanticModule("Test", [], [], [different]), [], [signature]);
        var binding = new HostOperationBinding(signature, (Func<object?>)(() => null));

        Assert.False(ModuleComposer.TryCompose([descriptor], [binding], out _, out var errors));
        Assert.Contains(errors, error => error.Code == "NM0005");
    }

    [Fact]
    public void Duplicate_binding_id_with_incompatible_signatures_is_rejected()
    {
        var signature = RotateSignature();
        var different = new OperationSignature("Test.Rotate", SemanticTypes.Bool);
        var descriptor = Requires("Test", signature, export: true);
        var first = new HostOperationBinding(signature, (Func<object?>)(() => null));
        var second = new HostOperationBinding(different, (Func<object?>)(() => null));

        Assert.False(ModuleComposer.TryCompose([descriptor], [first, second], out _, out var forward));
        Assert.False(ModuleComposer.TryCompose([descriptor], [second, first], out _, out var reverse));
        Assert.Contains(forward, error => error.Code == "NM0006");
        Assert.Equal(forward.Select(error => (error.Code, error.Message)),
            reverse.Select(error => (error.Code, error.Message)));
    }

    [Fact]
    public void Equivalent_requirements_coalesce_and_unused_binding_is_allowed()
    {
        var signature = RotateSignature();
        var one = Requires("One", signature, export: true);
        var two = Requires("Two", new OperationSignature("Test.Rotate", SemanticTypes.Angle,
            SemanticTypes.Angle, SemanticTypes.Scalar), export: true);
        var needed = new HostOperationBinding(signature, (Func<object?>)(() => null));
        var extra = new HostOperationBinding(new OperationSignature("Other.Read", SemanticTypes.Bool),
            (Func<object?>)(() => null));

        Assert.True(ModuleComposer.TryCompose([two, one], [extra, needed], out var composed, out var errors));
        Assert.Empty(errors);
        Assert.Same(needed, composed!.HostBindings[signature.Id]);
        Assert.Same(extra, composed.HostBindings["Other.Read"]);
    }

    [Fact]
    public void Static_syntax_conflict_is_a_structured_composition_diagnostic()
    {
        var calc = new ModuleDescriptor("Calc", CalcModule.Instance, null, [], []);
        var clash = new ModuleDescriptor("Clash", ClashModule.Instance, null, [], []);
        var copy = new ModuleDescriptor("Copy", ClashCopyModule.Instance, null, [], []);

        Assert.False(ModuleComposer.TryCompose([copy, calc, clash], [], out var composed, out var diagnostics));
        Assert.Null(composed);
        var conflict = Assert.Single(diagnostics);
        Assert.Equal("NM0007", conflict.Code);
        Assert.Contains("Calc.Clash", conflict.Message);
        Assert.Contains("Calc.ClashCopy", conflict.Message);
    }

    [Fact]
    public void Start_rule_error_does_not_hide_an_independent_missing_host_binding()
    {
        var signature = new OperationSignature("Test.Read", SemanticTypes.Scalar);
        var grammar = new ModuleDescriptor("Base", BaseModule.Instance, null, [], []);
        var broken = new ModuleDescriptor("Uses", UsesModule.Instance,
            new SemanticModule("Test", [], [], [signature]), ["Missing"], [signature]);

        Assert.False(ModuleComposer.TryCompose([broken, grammar], [], out var composed, out var diagnostics));
        Assert.Null(composed);
        Assert.Equal(["NM0002", "NM0004"], diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void Syntax_conflict_does_not_attribute_an_unrelated_substring_module()
    {
        var calc = new ModuleDescriptor("Calc", CalcModule.Instance, null, [], []);
        var clash = new ModuleDescriptor("Clash", ClashModule.Instance, null, [], []);
        var copy = new ModuleDescriptor("Copy", ClashCopyModule.Instance, null, [], []);
        var unrelated = new ModuleDescriptor("Unrelated", new EmptySyntaxModule("Clash"), null, [], []);

        Assert.False(ModuleComposer.TryCompose([unrelated, copy, calc, clash], [], out _, out var diagnostics));
        Assert.Equal(["Clash", "Copy"], Assert.Single(diagnostics).Modules);
    }

    [Fact]
    public void Registered_module_parses_binds_and_lowers_with_an_origin()
    {
        var signature = new OperationSignature("Test.Doc", SemanticTypes.Scalar);
        var lowerer = new LoweringRegistration(UsesKinds.Doc, signature.Id,
            (context, node) => new HirConstant(1, SemanticTypes.Scalar, context.Origin(node)));
        var grammar = new ModuleDescriptor("Base", BaseModule.Instance, null, [], []);
        var units = new ModuleDescriptor("Units", null,
            new SemanticModule("Units", [], [SemanticTypes.Angle], []), [], []);
        var uses = new ModuleDescriptor("Uses", UsesModule.Instance,
            new SemanticModule("Test", ["Units"], [], [signature], [lowerer]), ["Doc"], [signature]);
        var host = new HostOperationBinding(signature, (Func<object?>)(() => 1));

        var composed = ModuleComposer.Compose([uses, grammar, units], [host]);
        Assert.Same(host, composed.HostBindings[signature.Id]);
        using var parsed = composed.Language.Parse(CrossModuleTests.Sample, composed.StartRules[("Uses", "Doc")]);
        Assert.True(parsed.Success);
        var project = new Project(composed.Language);
        project.Set("sample.uses", parsed.Tree);
        var lowered = HirLowering.Lower(new ProjectSemantics(project)["sample.uses"],
            composed.Language.SemanticCatalog);

        Assert.Empty(lowered.Diagnostics);
        Assert.Equal("sample.uses", Assert.Single(Assert.Single(lowered.Roots).Origins).Path);
    }

    static OperationSignature RotateSignature() => new("Test.Rotate", SemanticTypes.Angle,
        SemanticTypes.Angle, SemanticTypes.Scalar);

    static ModuleDescriptor Requires(string id, OperationSignature signature, bool export) =>
        new(id, null, new SemanticModule(id, [], [], export ? [signature] : []), [], [signature]);

    sealed class EmptySyntaxModule(string name) : SyntaxModule(name)
    {
        public override string GetKindName(int localKind) => $"Empty#{localKind}";
        public override void Register(ExtensionRegistry registry) { }
    }
}
