using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace Nitrogen.Workspace.Admission;

public sealed record AdmissionResult(bool Accepted, string? Sha256, IReadOnlyList<AdmissionDiagnostic> Diagnostics);

internal sealed record CandidateResult(AdmissionCandidate? Candidate,
    IReadOnlyList<AdmissionDiagnostic> Diagnostics);

internal sealed class AdmissionCandidate(WorkspaceSnapshot snapshot, ModuleComposition composition,
    HostModuleProfile profile, Rule startRule) : IDisposable
{
    WorkspaceSnapshot? _snapshot = snapshot;
    public ModuleComposition Composition { get; } = composition;
    public HostModuleProfile Profile { get; } = profile;
    public Rule StartRule { get; } = startRule;
    public WorkspaceSnapshot TakeSnapshot()
    {
        var owned = _snapshot ?? throw new ObjectDisposedException(nameof(AdmissionCandidate));
        _snapshot = null;
        return owned;
    }
    public void Dispose()
    {
        _snapshot?.Dispose();
        _snapshot = null;
    }
}

public sealed class AcceptedModule
{
    readonly object _gate = new();
    WorkspaceSnapshot? _snapshot;
    ModuleComposition? _composition;
    Rule _startRule;
    int _leases;
    bool _retired;

    internal AcceptedModule(string sha256, WorkspaceSnapshot snapshot, ModuleComposition composition, Rule startRule)
    {
        Sha256 = sha256;
        _snapshot = snapshot;
        _composition = composition;
        _startRule = startRule;
    }

    public string Sha256 { get; }

    public AcceptedModuleLease Acquire()
    {
        lock (_gate)
        {
            if (_retired || _composition is null) throw new ObjectDisposedException(nameof(AcceptedModule));
            _leases++;
            return new AcceptedModuleLease(this, _composition, _startRule);
        }
    }

    internal WeakReference LoadContextReference()
    {
        lock (_gate) return _snapshot?.LoadContextReference() ?? new WeakReference(null);
    }

    internal void Retire()
    {
        lock (_gate)
        {
            _retired = true;
            if (_leases == 0) ReleaseSnapshot();
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            _leases--;
            if (_leases == 0 && _retired) ReleaseSnapshot();
        }
    }

    void ReleaseSnapshot()
    {
        _snapshot?.Dispose();
        _snapshot = null;
        _composition = null;
        _startRule = default;
    }
}

public sealed class AcceptedModuleLease : IDisposable
{
    AcceptedModule? _module;
    ModuleComposition? _composition;
    Rule _startRule;

    internal AcceptedModuleLease(AcceptedModule module, ModuleComposition composition, Rule startRule)
    {
        _module = module;
        _composition = composition;
        _startRule = startRule;
    }

    public AcceptedModule Module => _module ?? throw new ObjectDisposedException(nameof(AcceptedModuleLease));
    public ModuleComposition Composition => _composition ?? throw new ObjectDisposedException(nameof(AcceptedModuleLease));
    public Rule StartRule => _module is null ? throw new ObjectDisposedException(nameof(AcceptedModuleLease)) : _startRule;

    public void Dispose()
    {
        var module = Interlocked.Exchange(ref _module, null);
        _composition = null;
        _startRule = default;
        module?.Release();
    }
}

/// <summary>Composes a reviewed candidate without publishing it until examples pass.</summary>
public sealed class ModuleAdmissionService : IDisposable
{
    readonly object _gate = new();
    AcceptedModule? _active;
    bool _disposed;

    public AcceptedModule? Active { get { lock (_gate) return _active; } }

    public AdmissionResult Admit(ModulePackage package, IReadOnlyDictionary<string, HostModuleProfile> profiles)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var outcome = TryCandidate(package, profiles);
            if (outcome.Candidate is null) return new AdmissionResult(false, package.Sha256, outcome.Diagnostics);
            using var candidate = outcome.Candidate;
            var diagnostics = VerifyExamples(package, candidate);
            if (diagnostics.Count > 0) return new AdmissionResult(false, package.Sha256, diagnostics);
            var accepted = new AcceptedModule(package.Sha256, candidate.TakeSnapshot(), candidate.Composition,
                candidate.StartRule);
            var previous = _active;
            _active = accepted;
            previous?.Retire();
            return new AdmissionResult(true, package.Sha256, []);
        }
    }

    static IReadOnlyList<AdmissionDiagnostic> VerifyExamples(ModulePackage package, AdmissionCandidate candidate)
    {
        var errors = new List<AdmissionDiagnostic>();
        var parsed = new Dictionary<string, ParseResult>(StringComparer.Ordinal);
        if (!package.Examples.Any(example => example.ExpectedDiagnostics.Count == 0 &&
                (example.StartRule ?? package.StartRule).StartsWith(candidate.Profile.ModuleName + ".",
                    StringComparison.Ordinal)))
            errors.Add(new AdmissionDiagnostic("NA0004", "example", "module.json", 0, 0,
                "at least one successful candidate-module example is required"));
        try
        {
            var project = new Project(candidate.Composition.Language);
            foreach (var example in package.Examples)
            {
                string start = example.StartRule ?? package.StartRule;
                int dot = start.LastIndexOf('.');
                if (dot <= 0 || start[..dot] != candidate.Profile.ModuleName ||
                    !candidate.Composition.StartRules.TryGetValue((start[..dot], start[(dot + 1)..]), out var rule))
                {
                    errors.Add(new AdmissionDiagnostic("NA0004", "example", example.Path, 0, 0,
                        $"start rule '{start}' is not registered by the host"));
                    continue;
                }
                var result = candidate.Composition.Language.Parse(example.Source, rule);
                parsed.Add(example.Path, result);
                if (!result.HasErrors) project.Set(example.Path, result.Tree);
            }

            var semantics = new ProjectSemantics(project);
            foreach (var example in package.Examples)
            {
                if (!parsed.TryGetValue(example.Path, out var result)) continue;
                var actual = result.Diagnostics.ToArray().Select(d => d.Code.ToString()).ToList();
                FileSemantics? file = null;
                IReadOnlyList<HirNode> roots = [];
                if (!result.HasErrors)
                {
                    actual.AddRange(project.Diagnostics(example.Path).Select(d => d.Code));
                    file = semantics[example.Path];
                    actual.AddRange(file.Diagnostics().Select(d => d.Code));
                    if (actual.Count == 0)
                    {
                        var lowered = HirLowering.Lower(file, candidate.Composition.Language.SemanticCatalog);
                        actual.AddRange(lowered.Diagnostics.Select(d => d.Code));
                        roots = lowered.Roots;
                    }
                }
                if (!actual.Order(StringComparer.Ordinal).SequenceEqual(
                        example.ExpectedDiagnostics.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    errors.Add(new AdmissionDiagnostic("NA0004", "example", example.Path, 0, 0,
                        $"expected diagnostics [{string.Join(", ", example.ExpectedDiagnostics)}], got [{string.Join(", ", actual)}]"));
                    continue;
                }
                if (actual.Count != 0 || file is null) continue;
                try
                {
                    string? mismatch = candidate.Profile.Probe(candidate.Composition, file, roots, example.ExpectedResult);
                    if (mismatch is not null)
                        errors.Add(new AdmissionDiagnostic("NA0005", "probe", example.Path, 0, 0, mismatch));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    errors.Add(new AdmissionDiagnostic("NA0005", "probe", example.Path, 0, 0,
                        $"host probe failed: {error.GetType().Name}: {error.Message}"));
                }
            }
        }
        finally
        {
            foreach (var result in parsed.Values) result.Dispose();
        }
        return errors;
    }

    internal CandidateResult TryCandidate(ModulePackage package,
        IReadOnlyDictionary<string, HostModuleProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(profiles);
        if (!profiles.TryGetValue(package.ProfileId, out var profile))
            return Reject("NA0003", "profile", $"unknown host profile '{package.ProfileId}'");
        if (!string.Equals(profile.Id, package.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(profile.ModuleName, package.ModuleName, StringComparison.Ordinal))
            return Reject("NA0003", "profile", "profile and syntax module do not match");

        var policy = DeclarativeGrammarPolicy.Validate(package);
        if (policy.Count > 0) return new CandidateResult(null, policy);

        var workspace = new GrammarWorkspace();
        foreach (var (path, source) in package.Grammars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            workspace.SetGrammar(path, source);
        var snapshot = workspace.Compile();
        if (!snapshot.Succeeded)
        {
            var diagnostics = snapshot.Diagnostics.Select(d => new AdmissionDiagnostic(d.Code, "compile",
                d.Path, d.Line, d.Column, d.Message)).ToArray();
            snapshot.Dispose();
            return new CandidateResult(null, diagnostics);
        }

        var modules = snapshot.Language!.Modules;
        if (modules.Count != 1 || modules[0].Name != package.ModuleName ||
            !package.StartRule.StartsWith(package.ModuleName + ".", StringComparison.Ordinal) ||
            snapshot.FindRule(package.StartRule) is not { } start)
        {
            snapshot.Dispose();
            return Reject("NA0003", "module", "compiled module or start rule does not match the package");
        }

        try
        {
            var descriptor = profile.Describe(modules[0]);
            var startName = package.StartRule[(package.ModuleName.Length + 1)..];
            if (!ReferenceEquals(descriptor.Syntax, modules[0]) ||
                !descriptor.StartRules.Contains(startName, StringComparer.Ordinal))
            {
                snapshot.Dispose();
                return Reject("NA0003", "module", "host descriptor does not allow the package start rule");
            }
            var unrequested = modules[0].DeclarativeRules
                .Where(rule => rule.Form == DeclarativeForm.Operation)
                .Select(rule => rule.Target!)
                .Distinct(StringComparer.Ordinal)
                .Where(id => !package.RequestedCapabilities.Contains(id, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (unrequested.Length > 0)
            {
                snapshot.Dispose();
                return new CandidateResult(null, unrequested.Select(id =>
                    CapabilityError($"operation '{id}' is lowered by the grammar but not requested")).ToArray());
            }
            ModuleDescriptor[] descriptors = [descriptor, ..profile.Dependencies];
            if (!TrySelectCapabilities(package.RequestedCapabilities, profile.Capabilities, descriptors,
                    out var bindings, out var capabilityDiagnostics))
            {
                snapshot.Dispose();
                return new CandidateResult(null, capabilityDiagnostics);
            }
            if (!ModuleComposer.TryCompose(descriptors, bindings,
                    out var composition, out var compositionDiagnostics))
            {
                snapshot.Dispose();
                return new CandidateResult(null, compositionDiagnostics.Select(d => new AdmissionDiagnostic(
                    d.Code, "composition", string.Join(",", d.Modules), 0, 0, d.Message)).ToArray());
            }
            return new CandidateResult(new AdmissionCandidate(snapshot, composition!, profile, start), []);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            snapshot.Dispose();
            return Reject("NA0003", "profile", $"host profile failed: {error.GetType().Name}: {error.Message}");
        }
    }

    static bool TrySelectCapabilities(IReadOnlyList<string> requested, HostCapabilitySet catalog,
        IReadOnlyList<ModuleDescriptor> descriptors, out IReadOnlyList<HostOperationBinding> bindings,
        out IReadOnlyList<AdmissionDiagnostic> diagnostics)
    {
        var errors = new List<AdmissionDiagnostic>();
        var requirements = descriptors.SelectMany(descriptor => descriptor.RequiredOperations.Select(signature =>
            (descriptor.Id, Signature: signature))).GroupBy(entry => entry.Signature.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var (id, group) in requirements.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (group.Any(entry => !entry.Signature.Equals(group[0].Signature)))
                errors.Add(new AdmissionDiagnostic("NM0005", "composition", id, 0, 0,
                    $"Modules require incompatible signatures for operation '{id}'."));

        var selected = new List<HostOperationBinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in requested.Order(StringComparer.Ordinal))
        {
            if (!seen.Add(id))
                errors.Add(CapabilityError($"capability '{id}' is requested more than once"));
            else if (!catalog.TryGet(id, out var grant))
                errors.Add(CapabilityError($"capability '{id}' is not granted by this host profile"));
            else if (!requirements.TryGetValue(id, out var required))
                errors.Add(CapabilityError($"capability '{id}' is not required by the module"));
            else if (required.Any(entry => !entry.Signature.Equals(grant.Signature)))
                errors.Add(CapabilityError($"capability '{id}' has a different required signature"));
            else selected.Add(grant.Binding);
        }
        foreach (string id in requirements.Keys.Order(StringComparer.Ordinal))
            if (!seen.Contains(id))
                errors.Add(CapabilityError($"required capability '{id}' was not requested"));
        bindings = errors.Count == 0 ? Array.AsReadOnly(selected.ToArray()) : [];
        diagnostics = errors;
        return errors.Count == 0;
    }

    static AdmissionDiagnostic CapabilityError(string message) =>
        new("NA0006", "capability", "module.json", 0, 0, message);

    static CandidateResult Reject(string code, string stage, string message) =>
        new(null, [new AdmissionDiagnostic(code, stage, "module.json", 0, 0, message)]);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Retire();
            _active = null;
        }
    }
}
