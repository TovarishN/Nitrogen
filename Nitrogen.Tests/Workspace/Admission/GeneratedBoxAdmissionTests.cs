using System.Globalization;
using System.Numerics;
using Gravity.RagdollEditor;
using Nitrogen.Binding;
using Nitrogen.Geometry;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class GeneratedBoxAdmissionTests
{
    const string Grammar = """
        syntax module GeneratedBox
        {
          token Digits = ['0'..'9']+;
          syntax Document = Boxes:Box*;
          syntax Box = "box" Width:Digits Height:Digits Depth:Digits ";";
        }
        """;

    static ModulePackage Package(string grammar = Grammar, string hash = "box-v1") =>
        new("generated-box", "geometry-box", "GeneratedBox", "GeneratedBox.Document",
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["box.ngr"] = grammar },
            [new ModuleExample("valid", "valid.box", "box 1 2 3;", [], "mesh"),
             new ModuleExample("invalid", "invalid.box", "box no 2 3;", ["Expected", "Missing"], null)],
            [BoxMeshModule.BoxSignature.Id], hash);

    static IReadOnlyDictionary<string, HostModuleProfile> Profiles(Action? onProbe = null,
        Action? onHost = null)
    {
        var editorHost = (Func<IReadOnlyList<ExecutionValue>, GeometryMesh>)GeometryBoxMeshHost.Binding.Handler;
        var boxBinding = onHost is null ? GeometryBoxMeshHost.Binding :
            new HostOperationBinding(BoxMeshModule.BoxSignature,
                (Func<IReadOnlyList<ExecutionValue>, GeometryMesh>)(values =>
                {
                    onHost();
                    return editorHost(values);
                }));
        return new Dictionary<string, HostModuleProfile>(StringComparer.Ordinal)
        {
            ["geometry-box"] = new HostModuleProfile("geometry-box", "GeneratedBox", syntax =>
            {
                var boxKind = Enumerable.Range(1, 32)
                    .Where(local => syntax.GetKindName(local) == "Box")
                    .Select(local => syntax.KindBase | local).Single();
                var lowerer = new LoweringRegistration(boxKind, BoxMeshModule.BoxSignature.Id, (context, node) =>
                {
                    var tree = context.File.Tree;
                    var digits = Enumerable.Range(0, tree.NodeCount)
                        .Where(child => tree.Parent(child) == node &&
                            tree.GetText(child).ToString().All(char.IsDigit))
                        .ToArray();
                    if (digits.Length != 3) return null;
                    var arguments = new List<HirNode>();
                    foreach (var digit in digits)
                    {
                        if (!float.TryParse(tree.GetText(digit), NumberStyles.None,
                                CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value) || value <= 0)
                            return null;
                        arguments.Add(new HirConstant(value, SemanticTypes.Scalar, context.Origin(digit)));
                    }
                    return new HirOperation(BoxMeshModule.BoxSignature, arguments, [context.Origin(node)]);
                });
                return new ModuleDescriptor("GeneratedBox", syntax,
                    new SemanticModule("GeneratedBox", [], [BoxMeshModule.MeshType], [BoxMeshModule.BoxSignature],
                        [lowerer]), ["Document"], [BoxMeshModule.BoxSignature]);
            }, [], new HostCapabilitySet([new HostCapability(BoxMeshModule.BoxSignature,
                boxBinding, CapabilityEffect.Pure)]), (composition, _, roots, expected) =>
            {
                onProbe?.Invoke();
                if (expected != "mesh" || roots.Count != 1 || roots[0] is not HirOperation operation ||
                    !operation.Signature.Equals(BoxMeshModule.BoxSignature)) return "expected one box mesh operation";
                var result = GeometryExecutor.Execute(operation, composition);
                if (result.Diagnostics.Count != 0 || result.Mesh is not { } mesh) return "box mesh execution failed";
                var (vertices, indices) = MeshGenerator.GenerateBox(new Vector3(0.5f, 1f, 1.5f));
                return mesh.Vertices.SequenceEqual(vertices.Select(v => new MeshVertex(v.Position, v.Normal))) &&
                    mesh.Indices.SequenceEqual(indices) ? null : "box mesh differs from the editor generator";
            })
        };
    }

    [Fact]
    public void Reviewed_generated_box_admits_and_produces_exact_editor_mesh()
    {
        using var service = new ModuleAdmissionService();
        var result = service.Admit(Package(), Profiles());
        Assert.True(result.Accepted, string.Join("; ", result.Diagnostics));
        Assert.Equal("box-v1", result.Sha256);
        using var lease = service.Active!.Acquire();
        var binding = Assert.Single(lease.Composition.HostBindings);
        Assert.Equal("Geometry.BoxMesh", binding.Key);
        Assert.Equal(BoxMeshModule.BoxSignature, binding.Value.Signature);
        using var parsed = lease.Composition.Language.Parse("box 1 2 3;", lease.StartRule);
        Assert.True(parsed.Success);
    }

    [Fact]
    public void Unknown_box_request_never_reaches_probe_or_editor_host_and_keeps_old_lease()
    {
        using var service = new ModuleAdmissionService();
        var probes = 0;
        var hostCalls = 0;
        var profiles = Profiles(() => probes++, () => hostCalls++);
        Assert.True(service.Admit(Package(), profiles).Accepted);
        using var lease = service.Active!.Acquire();
        Assert.Equal(1, probes);
        Assert.Equal(1, hostCalls);

        var replacement = Package(hash: "box-v2") with { RequestedCapabilities = ["Geometry.Unknown"] };
        var outcome = service.Admit(replacement, profiles);
        Assert.False(outcome.Accepted);
        Assert.Equal("box-v2", outcome.Sha256);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "NA0006");
        Assert.Equal(1, probes);
        Assert.Equal(1, hostCalls);
        Assert.Same(lease.Module, service.Active);
        using var parsed = lease.Composition.Language.Parse("box 1 2 3;", lease.StartRule);
        Assert.True(parsed.Success);
        var project = new Project(lease.Composition.Language);
        project.Set("retained.box", parsed.Tree);
        var file = new ProjectSemantics(project)["retained.box"];
        Assert.Empty(file.Diagnostics());
        var lowered = HirLowering.Lower(file, lease.Composition.Language.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var executed = GeometryExecutor.Execute(Assert.Single(lowered.Roots), lease.Composition);
        Assert.Empty(executed.Diagnostics);
        var mesh = Assert.IsType<GeometryMesh>(executed.Mesh);
        var (vertices, indices) = MeshGenerator.GenerateBox(new Vector3(0.5f, 1f, 1.5f));
        Assert.Equal(indices, mesh.Indices);
        Assert.Equal(vertices.Select(vertex => new MeshVertex(vertex.Position, vertex.Normal)), mesh.Vertices);
        Assert.Equal(2, hostCalls);
    }

    [Fact]
    public void Invalid_replacement_keeps_the_accepted_box_module()
    {
        using var service = new ModuleAdmissionService();
        Assert.True(service.Admit(Package(), Profiles()).Accepted);
        using var lease = service.Active!.Acquire();
        var replacement = Package(Grammar.Replace("\"box\"", "\"cube\"", StringComparison.Ordinal), "box-v2");
        var rejected = service.Admit(replacement, Profiles());
        Assert.False(rejected.Accepted);
        Assert.Contains(rejected.Diagnostics, d => d.Code == "NA0004");
        Assert.Same(lease.Module, service.Active);
        using var parsed = lease.Composition.Language.Parse("box 1 2 3;", lease.StartRule);
        Assert.True(parsed.Success);
    }
}
