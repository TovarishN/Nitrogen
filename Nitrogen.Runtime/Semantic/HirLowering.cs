using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

public sealed record LoweringRegistration(int SyntaxKind, string OperationId,
    Func<LoweringContext, int, HirNode?> Lower, bool HandlesUnresolvedReferences = false,
    bool DynamicOperation = false);

public enum SemanticCheckScope { WholeFile, SubtreeAndAncestors }

public sealed class LoweringContext(FileSemantics file, Guid snapshotId, SemanticCheckScope checkScope = SemanticCheckScope.WholeFile)
{
    readonly List<LoweringDiagnostic> _reported = new();
    public FileSemantics File { get; } = file;
    public Guid SnapshotId { get; } = snapshotId;
    public SemanticCheckScope CheckScope { get; } = checkScope;
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
    /// <summary>Lower one checked declarative value inside a host operation. Reports subtree errors
    /// on the supplied context so the enclosing lowering result retains source-linked diagnostics.</summary>
    public static HirNode? LowerNested(LoweringContext context, int node)
    {
        ArgumentNullException.ThrowIfNull(context);
        var file = context.File;
        var tree = file.Tree;
        if (node < 0 || node >= tree.NodeCount) throw new ArgumentOutOfRangeException(nameof(node));
        var origin = context.Origin(node);
        if (HasRecovery(tree, node))
        {
            context.Report("NH0001", origin, "Recovered syntax cannot be lowered.");
            return null;
        }
        var span = origin.Span;
        var unresolved = file.Binding.References.FirstOrDefault(reference => Contains(span, reference.NameSpan) &&
            !reference.IsOptional && file.SymbolOf(reference.Node) is null);
        if (unresolved is not null)
        {
            context.Report("NH0002", new SourceOrigin(file.Path, context.SnapshotId,
                unresolved.Node, unresolved.NameSpan), "An unresolved symbol prevents lowering.");
            return null;
        }
        var bindingError = file.Binding.Diagnostics.FirstOrDefault(diagnostic => Contains(span, diagnostic.Span));
        var semanticError = (context.CheckScope == SemanticCheckScope.SubtreeAndAncestors
            ? file.DiagnosticsForSubtree(node) : file.Diagnostics()).FirstOrDefault(diagnostic => Contains(span, diagnostic.Span));
        if (bindingError is not null || semanticError is not null)
        {
            var site = bindingError?.Span ?? semanticError!.Span;
            int sourceNode = Enumerable.Range(0, tree.NodeCount)
                .FirstOrDefault(candidate => tree.Span(candidate) == site, node);
            context.Report("NH0003", new SourceOrigin(file.Path, context.SnapshotId, sourceNode, site),
                "Invalid semantics prevents lowering.");
            return null;
        }
        return file.DeclarativeTypes.LowerNested(context, node);
    }

    public static LoweringResult Lower(FileSemantics file, SemanticCatalog catalog) =>
        Lower(file, catalog, Guid.NewGuid());

    /// <summary>Lower selected declarative syntax kinds in source order, without adding registered
    /// roots from other language regions. Unsupported selected nodes receive a diagnostic.</summary>
    public static LoweringResult LowerSelected(FileSemantics file, IReadOnlySet<int> syntaxKinds, Guid snapshotId,
        SemanticCheckScope checkScope = SemanticCheckScope.WholeFile)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(syntaxKinds);
        if (snapshotId == Guid.Empty) throw new ArgumentException("A snapshot ID is required.", nameof(snapshotId));
        var kinds = syntaxKinds.ToHashSet();
        var context = new LoweringContext(file, snapshotId, checkScope);
        var roots = new List<HirNode>();
        foreach (int node in PreOrder(file.Tree))
        {
            if (!kinds.Contains(file.Tree.Kind(node))) continue;
            int previous = context.Reported.Count;
            try
            {
                var root = LowerNested(context, node);
                if (root is not null) roots.Add(root);
                else if (context.Reported.Count == previous)
                    context.Report("NH0005", context.Origin(node), "Selected syntax has no supported declarative lowering.");
            }
            catch (Exception error)
            {
                context.Report("NH0004", context.Origin(node),
                    $"Selected lowering failed: {error.GetType().Name}: {error.Message}");
            }
        }
        return new LoweringResult(roots.AsReadOnly(), Array.AsReadOnly(context.Reported.ToArray()));
    }

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
                    var dynamicOperation = root as HirOperation;
                    var operationId = registration.DynamicOperation ? dynamicOperation?.Signature.Id : registration.OperationId;
                    if (root.Type.Equals(SemanticTypes.Error) || operationId is null ||
                        !catalog.Operations.TryGetValue(operationId, out var signature) ||
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
