using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace Nitrogen.Workspace.Admission;

/// <summary>Trusted application code that defines the only semantics and host operations a package may use.</summary>
public sealed record HostModuleProfile(string Id, string ModuleName,
    Func<SyntaxModule, ModuleDescriptor> Describe,
    IReadOnlyList<ModuleDescriptor> Dependencies,
    HostCapabilitySet Capabilities,
    Func<ModuleComposition, FileSemantics, IReadOnlyList<HirNode>, string?, string?> Probe);
