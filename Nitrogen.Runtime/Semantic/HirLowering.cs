using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

public sealed record LoweringRegistration(int SyntaxKind, string OperationId,
    Func<LoweringContext, int, HirNode?> Lower, bool HandlesUnresolvedReferences = false,
    bool DynamicOperation = false);

public enum SemanticCheckScope { WholeFile, SubtreeAndAncestors }

/// <summary>What blocks lowering a node besides its own lowering rules.</summary>
public enum LoweringAdmission
{
    /// <summary>Recovered syntax, unresolved names, and binding or semantic errors in the subtree block it.</summary>
    Full,
    /// <summary>Only recovered syntax blocks it; for callers that validate the lowered result themselves.</summary>
    SyntaxOnly,
}

public sealed class LoweringContext(FileSemantics file, Guid snapshotId, SemanticCheckScope checkScope = SemanticCheckScope.WholeFile,
    LoweringAdmission admission = LoweringAdmission.Full)
{
    readonly List<LoweringDiagnostic> _reported = new();
    readonly HashSet<(string Path, int Node)> _activeTemplates = new();

    /// <summary>The context of a template body in <paramref name="file"/>: its parameters bound to the arguments; reports go to <paramref name="caller"/>.</summary>
    LoweringContext(FileSemantics file, LoweringContext caller, IReadOnlyDictionary<Binding.Symbol, HirNode> arguments)
        : this(file, caller.SnapshotId, caller.CheckScope, caller.Admission)
    {
        _reported = caller._reported;
        _activeTemplates = caller._activeTemplates;
        Arguments = arguments;
    }

    /// <summary>The template parameters bound in this context to their expansion's arguments; null outside a template.</summary>
    internal IReadOnlyDictionary<Binding.Symbol, HirNode>? Arguments { get; }

    /// <summary>The templates being expanded, by file and declaring node, to stop cycles.</summary>
    internal HashSet<(string Path, int Node)> ActiveTemplates => _activeTemplates;

    internal LoweringContext Expanding(FileSemantics template, IReadOnlyDictionary<Binding.Symbol, HirNode> arguments) =>
        new(template, this, arguments);

    public FileSemantics File { get; } = file;
    public Guid SnapshotId { get; } = snapshotId;
    public SemanticCheckScope CheckScope { get; } = checkScope;
    public LoweringAdmission Admission { get; } = admission;
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
        return Admit(context, node) ? file.DeclarativeTypes.LowerNested(context, node) : null;
    }

    /// <summary>Whether the subtree at <paramref name="node"/> of the context's file can lower; otherwise reports
    /// what blocks it: recovered syntax, an unresolved name, or a binding or semantic error within it.</summary>
    internal static bool Admit(LoweringContext context, int node)
    {
        var file = context.File;
        var tree = file.Tree;
        var origin = context.Origin(node);
        if (HasRecovery(tree, node))
        {
            context.Report("NH0001", origin, "Recovered syntax cannot be lowered.");
            return false;
        }
        if (context.Admission == LoweringAdmission.SyntaxOnly) return true;
        var span = origin.Span;
        var unresolved = FirstWithin(file.UnresolvedReferences(), reference => reference.NameSpan, span);
        if (unresolved is not null)
        {
            context.Report("NH0002", new SourceOrigin(file.Path, context.SnapshotId,
                unresolved.Node, unresolved.NameSpan), "An unresolved symbol prevents lowering.");
            return false;
        }
        var bindingError = FirstWithin(file.BindingDiagnosticsByStart(), diagnostic => diagnostic.Span, span);
        var semanticError = context.CheckScope == SemanticCheckScope.SubtreeAndAncestors
            ? file.DiagnosticsForSubtree(node).FirstOrDefault(diagnostic => Contains(span, diagnostic.Span))
            : FirstWithin(file.Diagnostics(), diagnostic => diagnostic.Span, span);
        if (bindingError is not null || semanticError is not null)
        {
            var site = bindingError?.Span ?? semanticError!.Span;
            int sourceNode = file.NodeAt(site) ?? node;
            context.Report("NH0003", new SourceOrigin(file.Path, context.SnapshotId, sourceNode, site),
                "Invalid semantics prevents lowering.");
            return false;
        }
        return true;
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

    /// <summary>Lower every registered root of the file; <paramref name="admission"/> chooses what blocks a root.</summary>
    public static LoweringResult Lower(FileSemantics file, SemanticCatalog catalog, Guid snapshotId,
        LoweringAdmission admission = LoweringAdmission.Full)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(catalog);
        if (snapshotId == Guid.Empty) throw new ArgumentException("A snapshot ID is required.", nameof(snapshotId));
        var context = new LoweringContext(file, snapshotId, admission: admission);
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
                if (admission == LoweringAdmission.Full)
                {
                    var unresolved = FirstWithin(file.UnresolvedReferences(), reference => reference.NameSpan, span);
                    if (unresolved is not null && !registration.HandlesUnresolvedReferences)
                    {
                        diagnostics.Add(new LoweringDiagnostic("NH0002",
                            new SourceOrigin(file.Path, context.SnapshotId, unresolved.Node, unresolved.NameSpan),
                            "An unresolved symbol prevents lowering."));
                        continue;
                    }
                    var bindingError = FirstWithin(file.BindingDiagnosticsByStart(), diagnostic => diagnostic.Span, span);
                    var semanticError = FirstWithin(file.Diagnostics(), diagnostic => diagnostic.Span, span);
                    if (bindingError is not null || semanticError is not null)
                    {
                        var site = bindingError?.Span ?? semanticError!.Span;
                        int sourceNode = file.NodeAt(site) ?? node;
                        diagnostics.Add(new LoweringDiagnostic("NH0003",
                            new SourceOrigin(file.Path, context.SnapshotId, sourceNode, site),
                            "Invalid semantics prevents lowering."));
                        continue;
                    }
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

    /// <summary>
    /// The first item of <paramref name="byStart"/> (sorted by span start) whose span lies within
    /// <paramref name="outer"/>: a binary search to the first start inside it, then a walk while starts stay inside.
    /// </summary>
    static T? FirstWithin<T>(IReadOnlyList<T> byStart, Func<T, TextSpan> spanOf, TextSpan outer) where T : class
    {
        int low = 0, high = byStart.Count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (spanOf(byStart[middle]).Start < outer.Start) low = middle + 1;
            else high = middle;
        }
        for (int i = low; i < byStart.Count && spanOf(byStart[i]).Start <= outer.End; i++)
            if (spanOf(byStart[i]).End <= outer.End) return byStart[i];
        return null;
    }

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
