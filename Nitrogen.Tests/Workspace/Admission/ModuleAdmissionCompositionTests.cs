using Nitrogen.Semantic;
using Nitrogen.Geometry;
using Gravity.RagdollEditor;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class ModuleAdmissionCompositionTests
{
    const string Grammar = """
        syntax module GeneratedBox
        {
          token Digits = ['0'..'9']+;
          syntax Document = "box" Digits;
        }
        """;
    static readonly OperationSignature Signature = new("Generated.Box", SemanticTypes.Scalar, SemanticTypes.Scalar);
    static readonly Func<float, float> Handler = value => value;

    static ModulePackage Package(string grammar = Grammar, string module = "GeneratedBox",
        string start = "GeneratedBox.Document", string profile = "box") =>
        new("candidate", profile, module, start,
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["box.ngr"] = grammar },
            [new ModuleExample("valid", "valid.box", "box 1", [], "1")], [Signature.Id], "hash");

    static HostModuleProfile Profile(IEnumerable<HostOperationBinding>? bindings = null) =>
        new("box", "GeneratedBox", syntax => new ModuleDescriptor("GeneratedBox", syntax,
                new SemanticModule("GeneratedBox", [], [], [Signature]), ["Document"], [Signature]),
            [], new HostCapabilitySet((bindings ?? [new HostOperationBinding(Signature, Handler)])
                .Select(binding => new HostCapability(binding.Signature, binding, CapabilityEffect.Pure))),
            (_, _, _, _) => null);

    [Fact]
    public void Valid_candidate_composes_without_becoming_active()
    {
        using var service = new ModuleAdmissionService();
        var result = service.TryCandidate(Package(), new Dictionary<string, HostModuleProfile> { ["box"] = Profile() });
        using var candidate = result.Candidate;
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(candidate);
        Assert.Equal("GeneratedBox", Assert.Single(candidate.Composition.Language.Modules).Name);
        Assert.Contains(("GeneratedBox", "Document"), candidate.Composition.StartRules.Keys);
        Assert.Null(service.Active);
    }

    [Theory]
    [InlineData("unknown", "GeneratedBox", "GeneratedBox.Document", "NA0003")]
    [InlineData("box", "Other", "GeneratedBox.Document", "NA0003")]
    [InlineData("box", "GeneratedBox", "GeneratedBox.Missing", "NA0003")]
    public void Unknown_profile_module_or_start_is_rejected(string profile, string module, string start, string code)
    {
        using var service = new ModuleAdmissionService();
        var result = service.TryCandidate(Package(module: module, start: start, profile: profile),
            new Dictionary<string, HostModuleProfile> { ["box"] = Profile() });
        Assert.Null(result.Candidate);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Null(service.Active);
    }

    [Fact]
    public void Missing_or_changed_host_signature_is_rejected_by_capability_gate()
    {
        using var service = new ModuleAdmissionService();
        var missing = service.TryCandidate(Package(),
            new Dictionary<string, HostModuleProfile> { ["box"] = Profile([]) });
        Assert.Null(missing.Candidate);
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == "NA0006");
        var changed = new HostOperationBinding(
            new OperationSignature(Signature.Id, SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar), Handler);
        var wrong = service.TryCandidate(Package(),
            new Dictionary<string, HostModuleProfile> { ["box"] = Profile([changed]) });
        Assert.Null(wrong.Candidate);
        Assert.Contains(wrong.Diagnostics, diagnostic => diagnostic.Code == "NA0006");
        Assert.Null(service.Active);
    }

    [Fact]
    public void Unknown_missing_and_unused_requests_are_rejected_before_composition()
    {
        using var service = new ModuleAdmissionService();
        var otherSignature = new OperationSignature("Other.Operation", SemanticTypes.Scalar);
        var otherBinding = new HostOperationBinding(otherSignature, (Func<float>)(() => 1f));
        var profiles = new Dictionary<string, HostModuleProfile>
        {
            ["box"] = Profile(),
            ["other"] = Profile() with
            {
                Id = "other",
                Capabilities = new HostCapabilitySet([new HostCapability(otherSignature, otherBinding,
                    CapabilityEffect.Pure)])
            }
        };
        foreach (var requested in new IReadOnlyList<string>[]
                 { ["Other.Operation"], [] })
        {
            var package = Package() with { RequestedCapabilities = requested };
            var outcome = service.TryCandidate(package, profiles);
            Assert.Null(outcome.Candidate);
            Assert.Contains(outcome.Diagnostics, d => d.Code == "NA0006");
        }
        var extraGrant = Profile([new HostOperationBinding(Signature, Handler), GeometryBoxMeshHost.Binding]);
        var unused = service.TryCandidate(Package() with
            { RequestedCapabilities = [Signature.Id, BoxMeshModule.BoxSignature.Id] },
            new Dictionary<string, HostModuleProfile> { ["box"] = extraGrant });
        Assert.Null(unused.Candidate);
        Assert.Contains(unused.Diagnostics, d => d.Code == "NA0006" &&
            d.Message.Contains("not required", StringComparison.Ordinal));
    }

    [Fact]
    public void Extra_host_grant_is_not_exposed_by_accepted_composition()
    {
        using var service = new ModuleAdmissionService();
        var profile = Profile([new HostOperationBinding(Signature, Handler), GeometryBoxMeshHost.Binding]);
        var result = service.TryCandidate(Package(),
            new Dictionary<string, HostModuleProfile> { ["box"] = profile });
        using var candidate = result.Candidate;
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(candidate);
        Assert.Equal([Signature.Id], candidate.Composition.HostBindings.Keys);
        Assert.False(candidate.Composition.HostBindings.ContainsKey(BoxMeshModule.BoxSignature.Id));
    }

    [Fact]
    public void Unrequested_dependency_requirement_rejects_candidate()
    {
        using var service = new ModuleAdmissionService();
        var profile = Profile([new HostOperationBinding(Signature, Handler), GeometryBoxMeshHost.Binding])
            with { Dependencies = [BoxMeshModule.Descriptor] };
        var outcome = service.TryCandidate(Package(),
            new Dictionary<string, HostModuleProfile> { ["box"] = profile });
        Assert.Null(outcome.Candidate);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "NA0006");
    }

    [Fact]
    public void Policy_rejection_precedes_workspace_compilation()
    {
        using var service = new ModuleAdmissionService();
        var source = Grammar.Replace("syntax Document = \"box\" Digits;",
            "syntax Document = \"box\" Digits { out Value : int = 0; Value = 1; }", StringComparison.Ordinal);
        var result = service.TryCandidate(Package(grammar: source),
            new Dictionary<string, HostModuleProfile> { ["box"] = Profile() });
        Assert.Null(result.Candidate);
        Assert.Equal("NA0002", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Host_descriptor_failure_rejects_candidate_without_publishing()
    {
        using var service = new ModuleAdmissionService();
        var profile = Profile() with
        {
            Describe = _ => throw new InvalidOperationException("bad host descriptor")
        };
        var result = service.TryCandidate(Package(),
            new Dictionary<string, HostModuleProfile> { ["box"] = profile });
        Assert.Null(result.Candidate);
        Assert.Contains(result.Diagnostics, item => item.Code == "NA0003");
        Assert.Null(service.Active);
    }
}
