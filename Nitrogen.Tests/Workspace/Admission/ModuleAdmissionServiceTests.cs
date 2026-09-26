using System.Runtime.CompilerServices;
using Gravity.RagdollEditor;
using Nitrogen.Geometry;
using Nitrogen.Workspace.Admission;
using Xunit;

namespace Nitrogen.Tests.Workspace.Admission;

public sealed class ModuleAdmissionServiceTests
{
    const string Grammar = "syntax module GeneratedBox { token Digits = ['0'..'9']+; syntax Document = \"box\" Digits; }";

    static ModulePackage Package(string hash = "A", string grammar = Grammar, string source = "box 1",
        IReadOnlyList<string>? expected = null, string? result = "ok") =>
        new("candidate", "box", "GeneratedBox", "GeneratedBox.Document",
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["box.ngr"] = grammar },
            [new ModuleExample("sample", "sample.box", source, expected ?? [], result)], [], hash);

    static Dictionary<string, HostModuleProfile> Profiles(Func<int, string?, string?>? probe = null) =>
        new(StringComparer.Ordinal)
        {
            ["box"] = new HostModuleProfile("box", "GeneratedBox",
                syntax => new ModuleDescriptor("GeneratedBox", syntax, null, ["Document"], []), [],
                new HostCapabilitySet([]),
                (_, _, roots, expected) => probe?.Invoke(roots.Count, expected) ??
                    (roots.Count == 0 && expected == "ok" ? null : "unexpected sample result"))
        };

    [Fact]
    public void Failed_candidates_leave_last_accepted_module_and_lease_usable()
    {
        using var service = new ModuleAdmissionService();
        var profiles = Profiles();
        Assert.True(service.Admit(Package(), profiles).Accepted);
        using var old = service.Active!.Acquire();
        Assert.Equal("A", old.Module.Sha256);

        var syntaxError = Package("B", grammar: "syntax module GeneratedBox { syntax Document = ; }");
        Assert.False(service.Admit(syntaxError, profiles).Accepted);
        Assert.Same(old.Module, service.Active);

        var wrongCode = Package("C", expected: ["Expected"], result: null);
        var mismatch = service.Admit(wrongCode, profiles);
        Assert.False(mismatch.Accepted);
        Assert.Contains(mismatch.Diagnostics, diagnostic => diagnostic.Code == "NA0004");
        Assert.Same(old.Module, service.Active);

        var badProbe = Profiles((_, _) => "wrong result");
        var resultMismatch = service.Admit(Package("D"), badProbe);
        Assert.False(resultMismatch.Accepted);
        Assert.Contains(resultMismatch.Diagnostics, diagnostic => diagnostic.Code == "NA0005");
        Assert.Same(old.Module, service.Active);

        Assert.True(service.Admit(Package("E", grammar: Grammar + "\n// next version"), profiles).Accepted);
        Assert.NotSame(old.Module, service.Active);
        using var parsed = old.Composition.Language.Parse("box 1", old.StartRule);
        Assert.True(parsed.Success);
        Assert.Equal("E", service.Active!.Sha256);
    }

    [Fact]
    public void Expected_negative_sample_does_not_run_profile_probe()
    {
        using var service = new ModuleAdmissionService();
        var calls = 0;
        var profiles = Profiles((_, _) => { calls++; return null; });
        var package = Package() with
        {
            Examples = [new ModuleExample("positive", "positive.box", "box 1", [], "ok"),
                new ModuleExample("negative", "negative.box", "no", ["Expected"], null)]
        };
        var outcome = service.Admit(package, profiles);
        Assert.True(outcome.Accepted, string.Join("; ", outcome.Diagnostics));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Per_example_start_must_be_in_the_host_descriptor()
    {
        using var service = new ModuleAdmissionService();
        var package = Package() with
        {
            Examples = [new ModuleExample("sample", "sample.box", "box 1", [], "ok", "GeneratedBox.Other")]
        };
        var result = service.Admit(package, Profiles());
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NA0004");
    }

    [Fact]
    public void Disposed_service_cannot_issue_an_active_lease()
    {
        var service = new ModuleAdmissionService();
        Assert.True(service.Admit(Package(), Profiles()).Accepted);
        var active = service.Active!;
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(() => active.Acquire());
    }

    [Fact]
    public void Retired_snapshot_releases_its_load_context_after_last_lease()
    {
        var context = RetireOldSnapshot();
        for (int i = 0; i < 20 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(context.IsAlive);
    }

    [Fact]
    public void A_package_without_a_positive_candidate_example_cannot_be_admitted()
    {
        using var service = new ModuleAdmissionService();
        var noExamples = Package() with { Examples = [] };
        Assert.False(service.Admit(noExamples, Profiles()).Accepted);

        var negativeOnly = Package(source: "no", expected: ["Expected"], result: null);
        var outcome = service.Admit(negativeOnly, Profiles());
        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "NA0004");
        Assert.Equal(negativeOnly.Sha256, outcome.Sha256);
    }

    [Fact]
    public void A_dependency_start_cannot_replace_the_candidate_example()
    {
        using var service = new ModuleAdmissionService();
        var profile = Profiles()["box"] with
        {
            Dependencies = [BoxMeshModule.Descriptor],
            Capabilities = new HostCapabilitySet([new HostCapability(BoxMeshModule.BoxSignature,
                GeometryBoxMeshHost.Binding, CapabilityEffect.Pure)]),
            Probe = (_, _, _, _) => null
        };
        var package = Package() with
        {
            RequestedCapabilities = [BoxMeshModule.BoxSignature.Id],
            Examples = [new ModuleExample("other", "other.geom", "box 1 2 3;", [], "ok", "Geometry.Document")]
        };
        var outcome = service.Admit(package,
            new Dictionary<string, HostModuleProfile> { ["box"] = profile });
        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "NA0004");
    }

    [Fact]
    public void Capability_rejection_preserves_previous_active_module_and_skips_probe()
    {
        using var service = new ModuleAdmissionService();
        var calls = 0;
        var profiles = Profiles((_, _) => { calls++; return null; });
        Assert.True(service.Admit(Package(), profiles).Accepted);
        using var lease = service.Active!.Acquire();
        var rejected = Package("B") with { RequestedCapabilities = ["Other.Operation"] };
        var outcome = service.Admit(rejected, profiles);
        Assert.False(outcome.Accepted);
        Assert.Equal("B", outcome.Sha256);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "NA0006");
        Assert.Equal(1, calls);
        Assert.Same(lease.Module, service.Active);
        using var parsed = lease.Composition.Language.Parse("box 1", lease.StartRule);
        Assert.True(parsed.Success);
    }

    [Fact]
    public void Admission_does_not_expose_a_public_package_constructor_or_mutable_properties()
    {
        Assert.Empty(typeof(ModulePackage).GetConstructors());
        Assert.All(typeof(ModulePackage).GetProperties(), property =>
            Assert.Null(property.SetMethod?.IsPublic == true ? property.SetMethod : null));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference RetireOldSnapshot()
    {
        using var service = new ModuleAdmissionService();
        Assert.True(service.Admit(Package(), Profiles()).Accepted);
        var old = service.Active!;
        var context = old.LoadContextReference();
        using (var lease = old.Acquire())
        {
            Assert.True(service.Admit(Package("B", grammar: Grammar + "\n// changed"), Profiles()).Accepted);
            using var parsed = lease.Composition.Language.Parse("box 1", lease.StartRule);
            Assert.True(parsed.Success);
        }
        return context;
    }
}
