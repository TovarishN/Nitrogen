using System.Text;

namespace Nitrogen.Grammar;

/// <summary>Emits typed views for rules and alternatives, union views for extension points, and the visitor.</summary>
internal sealed class ViewWriter
{
    static readonly HashSet<string> MemberNames = new(StringComparer.Ordinal) { "Tree", "Index", "Span", "Kind", "Is", "Create", "As", "IsMissing", "IsSkipped" };

    readonly EmitModel _model;
    readonly ModuleInfo _info;

    public ViewWriter(EmitModel model, ModuleInfo info)
    {
        _model = model;
        _info = info;
    }

    public void Write(StringBuilder b)
    {
        foreach (var rule in _model.Analysis.PrimaryRules(_info.Module))
            if (rule is ExtensibleRule extensible) WriteUnion(b, extensible.Name + "Node");
        foreach (var kind in _info.Kinds)
        {
            if (kind.Rule is SyntaxRule syntax) WriteNode(b, kind, SyntaxCodeWriter.Elements(syntax.Body), null);
            else if (kind.Alternative is { } alternative) WriteNode(b, kind, SyntaxCodeWriter.Elements(alternative.Body), kind.ImplicitModule);
        }
        WriteVisitor(b);
    }

    static void WriteHeader(StringBuilder b, string name)
    {
        b.Append($"public readonly struct {name} : ISyntaxView<{name}>\n{{\n")
            .Append($"    public {name}(SyntaxTree tree, int index)\n    {{\n        Tree = tree;\n        Index = index;\n    }}\n\n")
            .Append("    public SyntaxTree Tree { get; }\n\n")
            .Append("    public int Index { get; }\n\n")
            .Append("    public TextSpan Span => Tree.Span(Index);\n\n")
            .Append("    public bool IsMissing => (Tree.Flags(Index) & NodeFlags.Missing) != 0;\n\n")
            .Append("    public bool IsSkipped => (Tree.Flags(Index) & NodeFlags.Skipped) != 0;\n");
    }

    static void WriteUnion(StringBuilder b, string name)
    {
        WriteHeader(b, name);
        b.Append("\n    public int Kind => Tree.Kind(Index);\n")
            .Append("\n    public T As<T>() where T : struct, ISyntaxView<T> => SyntaxView.Cast<T>(Tree, Index);\n")
            .Append("\n    public static bool Is(SyntaxTree tree, int index) => true;\n")
            .Append($"\n    public static {name} Create(SyntaxTree tree, int index) => new(tree, index);\n}}\n\n");
    }

    void WriteNode(StringBuilder b, KindInfo kind, IReadOnlyList<Expr> elements, string? implicitModule)
    {
        string name = kind.ViewName;
        WriteHeader(b, name);
        foreach (var (property, type, child) in Properties(elements, implicitModule, name))
            b.Append($"\n    public {type} {property} => new(Tree, Tree.Child(Index, {child}));\n");
        b.Append($"\n    public static bool Is(SyntaxTree tree, int index) => tree.Kind(index) == {_info.KindsName}.{CSharpText.Identifier(kind.Name)};\n")
            .Append($"\n    public static {name} Create(SyntaxTree tree, int index) => new(tree, index);\n}}\n\n");
    }

    /// <summary>
    /// A node's named children as its view names them: labels, else the referenced rule's name,
    /// numbered when it repeats or clashes with a label. Semantics blocks (issue 239) use these names.
    /// </summary>
    internal static List<(string Name, Expr Element, int Child)> ChildSlots(IReadOnlyList<Expr> elements, string viewName)
    {
        var slots = new List<(Expr Element, int Child)>();
        int child = 0;
        foreach (var element in elements)
        {
            var bare = element is LabeledExpr labeled ? labeled.Inner : element;
            if (bare is PredicateExpr) continue; // predicates push no node
            slots.Add((element, child++));
        }

        var labels = new HashSet<string>(
            slots.Select(s => s.Element).OfType<LabeledExpr>().Select(l => l.Label), StringComparer.Ordinal);
        var counts = slots.Select(s => s.Element).OfType<ReferenceExpr>()
            .GroupBy(r => CSharpText.LastSegment(r.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        var named = new List<(string, Expr, int)>();
        foreach (var (element, index) in slots)
        {
            string name;
            if (element is LabeledExpr labeled)
            {
                name = labeled.Label;
            }
            else if (element is ReferenceExpr reference)
            {
                string rule = CSharpText.LastSegment(reference.Name);
                seen.TryGetValue(rule, out int n);
                seen[rule] = ++n;
                name = counts[rule] > 1 || labels.Contains(rule) ? rule + n : rule;
            }
            else
            {
                continue;
            }
            if (MemberNames.Contains(name) || name == viewName) name += "_";
            named.Add((name, element, index));
        }
        return named;
    }

    List<(string Name, string Type, int Child)> Properties(IReadOnlyList<Expr> elements, string? implicitModule, string viewName) =>
        ChildSlots(elements, viewName)
            .Select(s => (CSharpText.Identifier(s.Name), TypeOf(s.Element, implicitModule), s.Child))
            .ToList();

    string TypeOf(Expr expr, string? implicitModule)
    {
        switch (expr)
        {
            case LabeledExpr labeled:
                return TypeOf(labeled.Inner, implicitModule);
            case LiteralExpr:
            case CharClassExpr:
            case AnyCharExpr:
                return "Token";
            case ReferenceExpr reference:
            {
                var symbol = _model.Resolve(reference.Name, _info.Module, implicitModule);
                var target = _model.Info(symbol.Module);
                return symbol.Rule switch
                {
                    TokenRule => "Token",
                    SyntaxRule syntax when EmitModel.IsAlias(syntax) => "SyntaxNode",
                    _ => target.TypeRef(_info, symbol.Rule.Name + "Node"),
                };
            }
            case RepeatExpr { Kind: RepeatKind.Optional } optional:
                return $"Optional<{ItemType(optional.Inner, implicitModule)}>";
            case RepeatExpr repeat:
                return $"SyntaxList<{ItemType(repeat.Inner, implicitModule)}>";
            case SeparatedListExpr list:
                return $"SeparatedList<{ItemType(list.Item, implicitModule)}>";
            default:
                return "SyntaxNode";
        }
    }

    string ItemType(Expr expr, string? implicitModule)
    {
        var bare = expr is LabeledExpr labeled ? labeled.Inner : expr;
        return bare is RepeatExpr or SeparatedListExpr ? "SyntaxNode" : TypeOf(bare, implicitModule);
    }

    void WriteVisitor(StringBuilder b)
    {
        var visited = _info.Kinds.Where(k => k.Rule is not TokenRule).ToList();
        if (visited.Count == 0) return;
        string cls = _info.ClassName;
        b.Append($"public abstract class {_info.VisitorName}<TResult>\n{{\n")
            .Append("    public virtual TResult Visit(SyntaxTree tree, int node)\n    {\n")
            .Append("        int kind = tree.Kind(node);\n")
            .Append($"        if (SyntaxKinds.ModuleOf(kind) == {cls}.Instance.Id)\n        {{\n")
            .Append("            switch (SyntaxKinds.LocalOf(kind))\n            {\n");
        foreach (var kind in visited)
            b.Append($"                case {cls}.L{kind.Name}: return Visit{kind.Name}(new {kind.ViewName}(tree, node));\n");
        b.Append("            }\n        }\n        return DefaultVisit(tree, node);\n    }\n\n")
            .Append("    public TResult Visit<TView>(TView view) where TView : struct, ISyntaxView<TView> => Visit(view.Tree, view.Index);\n");
        foreach (var kind in visited)
            b.Append($"\n    public virtual TResult Visit{kind.Name}({kind.ViewName} node) => DefaultVisit(node.Tree, node.Index);\n");
        b.Append("\n    protected virtual TResult DefaultVisit(SyntaxTree tree, int node) => default!;\n}\n\n");
    }
}
