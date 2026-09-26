using Nitrogen.Binding;
using Nitrogen.Geometry.Syntax;
using Nitrogen.Semantic;
using Nitrogen.Semantics;

namespace Nitrogen.Geometry;

/// <summary>Expands a bound geometry call into the existing box operation.</summary>
public static class GeometryDefinitionExpander
{
    public static LoweringRegistration Registration { get; } =
        new(GeometryKinds.Make, BoxMeshModule.BoxSignature.Id, Lower, HandlesUnresolvedReferences: true);

    static HirNode? Lower(LoweringContext context, int node)
    {
        var tree = context.File.Tree;
        for (int parent = tree.Parent(node); parent >= 0; parent = tree.Parent(parent))
            if (tree.Kind(parent) == GeometryKinds.Definition) return null;

        return ExpandCall(context, context.File, node, new Dictionary<Symbol, HirNode>(ReferenceEqualityComparer.Instance),
            new HashSet<Symbol>(ReferenceEqualityComparer.Instance), []);
    }

    static HirNode? ExpandCall(LoweringContext context, FileSemantics caller, int node,
        IReadOnlyDictionary<Symbol, HirNode> callerArguments, HashSet<Symbol> active,
        IReadOnlyList<SourceOrigin> route)
    {
        var call = new MakeNode(caller.Tree, node);
        var symbol = caller.SymbolOf(node);
        if (symbol is null || symbol.Kind != "shape" || symbol.Path is null)
        {
            context.Report("GD0002", AtName(caller, context.SnapshotId, call.Name),
                $"geometry definition '{call.Name}' is missing or ambiguous");
            return null;
        }

        if (!active.Add(symbol))
        {
            context.Report("GD0004", AtName(caller, context.SnapshotId, call.Name),
                "cyclic geometry definition");
            return null;
        }

        try { return ExpandBoundCall(context, caller, call, symbol, callerArguments, active, route); }
        finally { active.Remove(symbol); }
    }

    static HirNode? ExpandBoundCall(LoweringContext context, FileSemantics caller, MakeNode call,
        Symbol symbol, IReadOnlyDictionary<Symbol, HirNode> callerArguments, HashSet<Symbol> active,
        IReadOnlyList<SourceOrigin> route)
    {
        var definitionFile = context.File.RelatedFile(symbol.Path!);
        var definitionSpan = definitionFile.Tree.Span(symbol.Node);
        if (definitionFile.Binding.Diagnostics.Any(diagnostic => Within(definitionSpan, diagnostic.Span)) ||
            definitionFile.Diagnostics().Any(diagnostic => Within(definitionSpan, diagnostic.Span)))
            return null;
        var definition = new DefinitionNode(definitionFile.Tree, symbol.Node);
        var parameters = new[] { definition.Width, definition.Height, definition.Depth };
        if (call.Args.Count != parameters.Length) return null;

        var arguments = new Dictionary<Symbol, HirNode>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = definitionFile.Binding.Declarations.SingleOrDefault(candidate =>
                candidate.Kind == "parameter" && candidate.Node == parameters[i].Index);
            var value = Dimension(context, caller, call.Args[i].Index, callerArguments);
            if (parameter is null || value is null) return null;
            var declaration = new HirSymbolRef(
                Nitrogen.Semantic.SemanticSymbol.From(parameter, "Geometry", SemanticTypes.Scalar),
                Origin(definitionFile, context.SnapshotId, parameter.Node));
            arguments.Add(parameter, HirTraversal.Rewrite(declaration, _ => value));
        }

        var nextRoute = route.Append(Origin(caller, context.SnapshotId, call.Index)).ToArray();
        int boxNode = Find(definitionFile.Tree, definition.Body.Index, GeometryKinds.TemplateBox);
        if (boxNode < 0)
        {
            int nestedCall = Find(definitionFile.Tree, definition.Body.Index, GeometryKinds.Make);
            return nestedCall < 0 ? null : ExpandCall(context, definitionFile, nestedCall, arguments, active, nextRoute);
        }
        var box = new TemplateBoxNode(definitionFile.Tree, boxNode);
        var dimensions = new[] { box.Width, box.Height, box.Depth };
        var templateArgs = dimensions.Select(dimension =>
            Dimension(context, definitionFile, dimension.Index, arguments)).ToArray();
        if (templateArgs.Any(argument => argument is null)) return null;
        return new HirOperation(BoxMeshModule.BoxSignature, templateArgs!,
            nextRoute.Append(Origin(definitionFile, context.SnapshotId, boxNode)).Distinct().ToArray());
    }

    static HirNode? Dimension(LoweringContext context, FileSemantics file, int node,
        IReadOnlyDictionary<Symbol, HirNode> arguments)
    {
        var tree = file.Tree;
        int number = Find(tree, node, GeometryKinds.Num);
        if (number >= 0)
        {
            var amount = new NumNodeSemantics(file, number).Amount;
            return GeometryValues.Positive(amount)
                ? new HirConstant(amount!.Value, SemanticTypes.Scalar, Origin(file, context.SnapshotId, number))
                : null;
        }
        int referenceNode = Find(tree, node, GeometryKinds.ParameterRef);
        if (referenceNode < 0) return null;
        var binding = file.SymbolOf(referenceNode);
        if (binding is null || !arguments.TryGetValue(binding, out var replacement)) return null;
        var reference = new HirSymbolRef(Nitrogen.Semantic.SemanticSymbol.From(binding, "Geometry", SemanticTypes.Scalar),
            Origin(file, context.SnapshotId, referenceNode));
        return HirTraversal.Rewrite(reference, _ => replacement);
    }

    static int Find(SyntaxTree tree, int node, int kind)
    {
        if (tree.Kind(node) == kind) return node;
        for (int i = 0; i < tree.ChildCount(node); i++)
        {
            int found = Find(tree, tree.Child(node, i), kind);
            if (found >= 0) return found;
        }
        return -1;
    }

    static SourceOrigin Origin(FileSemantics file, Guid snapshot, int node) =>
        new(file.Path, snapshot, node, file.Tree.Span(node));

    static SourceOrigin AtName(FileSemantics file, Guid snapshot, Token name) =>
        new(file.Path, snapshot, name.Index, name.Span);

    static bool Within(TextSpan outer, TextSpan inner) =>
        inner.Start >= outer.Start && inner.End <= outer.End;
}
