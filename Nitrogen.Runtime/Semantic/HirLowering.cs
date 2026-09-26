using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

public sealed record LoweringRegistration(int SyntaxKind, string OperationId,
    Func<LoweringContext, int, HirNode?> Lower, bool HandlesUnresolvedReferences = false);

public sealed class LoweringContext(FileSemantics file, Guid snapshotId)
{
    readonly List<LoweringDiagnostic> _reported = new();
    public FileSemantics File { get; } = file;
    public Guid SnapshotId { get; } = snapshotId;
    public SourceOrigin Origin(int node) => new(File.Path, SnapshotId, node, File.Tree.Span(node));
    public void Report(string code, SourceOrigin origin, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        _reported.Add(new LoweringDiagnostic(code, origin, message));
    }
    internal IReadOnlyList<LoweringDiagnostic> Reported => _reported;
}

public sealed record LoweringDiagnostic(string Code, SourceOrigin Origin, string Message);
public sealed record LoweringResult(IReadOnlyList<HirNode> Roots, IReadOnlyList<LoweringDiagnostic> Diagnostics);

public static class HirLowering
{
    public static LoweringResult Lower(FileSemantics file, SemanticCatalog catalog) =>
        Lower(file, catalog, Guid.NewGuid());

    public static LoweringResult Lower(FileSemantics file, SemanticCatalog catalog, Guid snapshotId)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(catalog);
        if (snapshotId == Guid.Empty) throw new ArgumentException("A snapshot ID is required.", nameof(snapshotId));
        var context = new LoweringContext(file, snapshotId);
        var roots = new List<HirNode>();
        var diagnostics = new List<LoweringDiagnostic>();
        var tree = file.Tree;
        foreach (var node in PreOrder(tree))
        {
            foreach (var registration in catalog.LowerersFor(tree.Kind(node)))
            {
                var origin = context.Origin(node);
                var span = origin.Span;
                if (HasRecovery(tree, node))
                {
                    diagnostics.Add(new LoweringDiagnostic("NH0001", origin, "Recovered syntax cannot be lowered."));
                    continue;
                }
                var unresolved = file.Binding.References.FirstOrDefault(reference => Contains(span, reference.NameSpan) &&
                    !reference.IsOptional && file.SymbolOf(reference.Node) is null);
                if (unresolved is not null && !registration.HandlesUnresolvedReferences)
                {
                    diagnostics.Add(new LoweringDiagnostic("NH0002",
                        new SourceOrigin(file.Path, context.SnapshotId, unresolved.Node, unresolved.NameSpan),
                        "An unresolved symbol prevents lowering."));
                    continue;
                }
                var bindingError = file.Binding.Diagnostics.FirstOrDefault(diagnostic => Contains(span, diagnostic.Span));
                var semanticError = file.Diagnostics().FirstOrDefault(diagnostic => Contains(span, diagnostic.Span));
                if (bindingError is not null || semanticError is not null)
                {
                    var site = bindingError?.Span ?? semanticError!.Span;
                    int sourceNode = Enumerable.Range(0, tree.NodeCount)
                        .FirstOrDefault(candidate => tree.Span(candidate) == site, node);
                    diagnostics.Add(new LoweringDiagnostic("NH0003",
                        new SourceOrigin(file.Path, context.SnapshotId, sourceNode, site),
                        "Invalid semantics prevents lowering."));
                    continue;
                }
                try
                {
                    var root = registration.Lower(context, node);
                    if (root is null) continue;
                    if (root.Type.Equals(SemanticTypes.Error) ||
                        !catalog.Operations.TryGetValue(registration.OperationId, out var signature) ||
                        !root.Type.Equals(signature.Result) ||
                        root is HirOperation operation && !operation.Signature.Equals(signature))
                    {
                        diagnostics.Add(new LoweringDiagnostic("NH0003", root.Origins[0],
                            "The lowered value does not match its registered operation."));
                        continue;
                    }
                    roots.Add(root);
                }
                catch (Exception error)
                {
                    diagnostics.Add(new LoweringDiagnostic("NH0004", origin,
                        $"Lowerer '{registration.OperationId}' failed: {error.GetType().Name}: {error.Message}"));
                }
            }
        }
        diagnostics.AddRange(context.Reported);
        return new LoweringResult(roots.AsReadOnly(), diagnostics
            .OrderBy(diagnostic => diagnostic.Origin.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Origin.Span.Start)
            .ToList().AsReadOnly());
    }

    static bool Contains(TextSpan outer, TextSpan inner) =>
        inner.Start >= outer.Start && inner.End <= outer.End;

    static bool HasRecovery(SyntaxTree tree, int node)
    {
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if ((tree.Flags(current) & NodeFlags.Missing) != 0 || !tree.SkippedTrivia(current).IsEmpty)
                return true;
            for (var i = 0; i < tree.ChildCount(current); i++) stack.Push(tree.Child(current, i));
        }
        return false;
    }

    static IEnumerable<int> PreOrder(SyntaxTree tree)
    {
        var stack = new Stack<int>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = tree.ChildCount(node) - 1; i >= 0; i--) stack.Push(tree.Child(node, i));
        }
    }
}
