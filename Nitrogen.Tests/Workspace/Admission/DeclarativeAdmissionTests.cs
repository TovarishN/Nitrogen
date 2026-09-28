using System.Globalization;
using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class DeclarativeAdmissionTests
{
    static readonly SemanticType Angle = SemanticType.Named("Units", "Angle");
    static readonly OperationSignature Area = new("Shapes.Area", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
    static readonly OperationSignature Turn = new("Shapes.Turn", Angle, Angle);

    const string Grammar = """
        syntax module Shapes
        {
          token Digits = ['0'..'9']+;
          syntax Document = Shape:Rect;
          syntax Rect = "rect" Width:Num Height:Num ";" lowers Shapes.Area(Width, Height);
          syntax Num = Text:Digits lowers literal Core.Scalar Text;
        }
        """;

    static Dictionary<string, HostModuleProfile> Profiles() => new()
    {
        ["shapes"] = new HostModuleProfile("shapes", "Shapes",
            syntax => new ModuleDescriptor("Shapes", syntax,
                new SemanticModule("Shapes", [], [Angle], [Area, Turn]), ["Document"], [Area]),
            [],
            new HostCapabilitySet([new HostCapability(Area, new HostOperationBinding(Area,
                (Func<IReadOnlyList<ExecutionValue>, ExecutionValue>)(arguments =>
                    new ExecutionValue(SemanticTypes.Scalar, arguments[0].Number * arguments[1].Number))),
                CapabilityEffect.Pure)]),
            Probe),
    };

    static string? Probe(ModuleComposition composition, FileSemantics file, IReadOnlyList<HirNode> roots, string? expected)
    {
        var registry = HostOperationRegistry.Bind(composition.Language.SemanticCatalog, composition.HostBindings.Values);
        var result = HirEvaluator.Evaluate(Assert.Single(roots), registry, new Dictionary<Symbol, ExecutionValue>());
        string actual = result.Value?.Number.ToString(CultureInfo.InvariantCulture) ?? "error";
        return actual == expected ? null : $"expected {expected}, got {actual}";
    }

    static ModulePackage Package(string grammar, string hash) => new("shapes-demo", "shapes", "Shapes", "Shapes.Document",
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["shapes.ngr"] = grammar },
        [new ModuleExample("area", "area.shapes", "rect 2 3;", [], "6")], ["Shapes.Area"], hash);

    static string Describe(AdmissionResult result) =>
        string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Message}"));

    [Fact]
    public void Declarative_package_is_admitted_and_executes()
    {
        using var service = new ModuleAdmissionService();
        var result = service.Admit(Package(Grammar, "good"), Profiles());
        Assert.True(result.Accepted, Describe(result));
        Assert.Equal("good", service.Active!.Sha256);
    }

    [Fact]
    public void Type_mismatch_in_an_example_is_rejected_with_NT0001()
    {
        var mismatched = Grammar.Replace("lowers literal Core.Scalar Text", "lowers literal Units.Angle Text");
        using var service = new ModuleAdmissionService();
        var result = service.Admit(Package(mismatched, "mismatch"), Profiles());
        Assert.False(result.Accepted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("NA0004", diagnostic.Code);
        Assert.Contains("NT0001", diagnostic.Message);
        Assert.Null(service.Active);
    }

    [Fact]
    public void Lowering_an_unrequested_operation_is_NA0006_and_keeps_the_active_module()
    {
        using var service = new ModuleAdmissionService();
        Assert.True(service.Admit(Package(Grammar, "good"), Profiles()).Accepted);
        var turning = Grammar
            .Replace("syntax Document = Shape:Rect;", "syntax Document = Shape:(Rect / Spin);")
            .Replace("syntax Num =", "syntax Spin = \"spin\" Amount:Deg \";\" lowers Shapes.Turn(Amount);\n  syntax Deg = Text:Digits \"deg\" lowers literal Units.Angle Text;\n  syntax Num =");
        var result = service.Admit(Package(turning, "turning"), Profiles());
        Assert.False(result.Accepted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("NA0006", diagnostic.Code);
        Assert.Contains("Shapes.Turn", diagnostic.Message);
        Assert.Equal("good", service.Active!.Sha256);
    }
}
