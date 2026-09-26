using Nitrogen.Semantic;

namespace Nitrogen.LanguageService;

/// <summary>Value-only HIR and lowering diagnostics for one document snapshot.</summary>
public sealed record DocumentInspection(int Version, Guid SnapshotId,
    IReadOnlyList<HirNode> Roots, IReadOnlyList<LoweringDiagnostic> Diagnostics);

/// <summary>The most specific HIR node at a source position.</summary>
public sealed record SemanticInspection(int Version, Guid SnapshotId, HirNode Root,
    HirNode Node, DocumentRange Range, SemanticType Type, DocumentLocation? Declaration);
