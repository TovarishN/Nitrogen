using System.Globalization;
using System.Text;
using Nitrogen.Binding;
using Nitrogen.Semantics;

namespace Nitrogen.Semantic;

/// <summary>
/// One file's declarative types (issue 251). A node is typed by its own lowers clause, by the
/// symbol its reference resolves to, or, with neither, by its single typed child. Only lowers
/// arguments, literals and declared types are checked. <see cref="SemanticTypes.Error"/> marks a
/// node whose problem is reported elsewhere, so errors do not cascade.
/// </summary>
public sealed class DeclarativeTypes
{
    readonly FileSemantics _file;
    readonly DeclarativeLowering _lowering;
    readonly SemanticCatalog _catalog;
    readonly Dictionary<int, SemanticType?> _types = new();
    List<SemanticDiagnostic>? _diagnostics;

    internal DeclarativeTypes(FileSemantics file, DeclarativeLowering lowering, SemanticCatalog catalog)
    {
        _file = file;
        _lowering = lowering;
        _catalog = catalog;
    }

    /// <summary>The node's type; null when untyped, <see cref="SemanticTypes.Error"/> when invalid.</summary>
    public SemanticType? TypeOf(int node)
    {
        if (_types.TryGetValue(node, out var cached)) return cached;
        var type = Compute(node);
        _types[node] = type;
        return type;
    }

    /// <summary>The type the symbol's declaring clause gives it; null when it has none.</summary>
    public SemanticType? TypeOfSymbol(Symbol symbol)
    {
        if (symbol.IsBuiltin || symbol.Path is null) return null;
        var declaring = symbol.Path == _file.Path ? this : _file.RelatedFile(symbol.Path).DeclarativeTypes;
        return declaring.DeclaredTypeAt(symbol.Node);
    }

    SemanticType? Compute(int node)
    {
        var tree = _file.Tree;
        if ((tree.Flags(node) & NodeFlags.Missing) != 0) return SemanticTypes.Error;
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Ambiguous)
            return tree.ChildCount(node) > 0 ? TypeOf(tree.Child(node, 0)) : SemanticTypes.Error;
        if (_lowering.RuleFor(kind) is { } rule && rule.Rule.Form != DeclarativeForm.None)
            return rule.Rule.Form == DeclarativeForm.Operation
                ? rule.Operation!.Result
                : TryLiteral(node, rule.Rule, out _) ? rule.LiteralType : SemanticTypes.Error;
        if (_file.HasReference(node))
            return _file.SymbolOf(node) is { } symbol ? TypeOfSymbol(symbol) : SemanticTypes.Error;

        SemanticType? found = null;
        int count = 0;
        for (int k = 0; k < tree.ChildCount(node); k++)
        {
            var child = TypeOf(tree.Child(node, k));
            if (child is null) continue;
            if (child.Equals(SemanticTypes.Error)) return SemanticTypes.Error;
            found = child;
            count++;
        }
        return count == 1 ? found : null;
    }

    SemanticType? DeclaredTypeAt(int node)
    {
        if (_lowering.RuleFor(_file.Tree.Kind(node)) is not { } rule) return null;
        if (rule.DeclaredType is { } fixedType) return fixedType;
        if (rule.Rule.DeclaredTypeChild < 0) return null;
        var text = Spelled(_file.Tree.Child(node, rule.Rule.DeclaredTypeChild));
        return text is not null && DeclarativeLowering.TryResolveType(_catalog, text, out var type) == TypeLookup.Found
            ? type
            : SemanticTypes.Error;
    }

    internal IReadOnlyList<SemanticDiagnostic> Diagnostics()
    {
        if (_diagnostics is not null) return _diagnostics;
        _diagnostics = new List<SemanticDiagnostic>();
        if (_lowering.IsEmpty) return _diagnostics;
        var tree = _file.Tree;
        var stack = new Stack<int>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            int kind = tree.Kind(node);
            if (kind == SyntaxKinds.Ambiguous)
            {
                if (tree.ChildCount(node) > 0) stack.Push(tree.Child(node, 0));
                continue;
            }
            if ((tree.Flags(node) & NodeFlags.Missing) == 0 && _lowering.RuleFor(kind) is { } rule) Check(node, rule);
            for (int k = tree.ChildCount(node) - 1; k >= 0; k--) stack.Push(tree.Child(node, k));
        }
        return _diagnostics;
    }

    void Check(int node, DeclarativeLowering.ResolvedRule rule)
    {
        var tree = _file.Tree;
        if (rule.Rule.Form == DeclarativeForm.Operation)
        {
            var operation = rule.Operation!;
            for (int i = 0; i < rule.Rule.Arguments.Count; i++)
            {
                int argument = tree.Child(node, rule.Rule.Arguments[i]);
                var expected = operation.Inputs[i];
                var actual = TypeOf(argument);
                if (actual is null)
                    Report("NT0004", argument, $"'{Text(argument)}' has no declared type; '{operation.Id}' needs {expected}");
                else if (!actual.Equals(SemanticTypes.Error) && !actual.Equals(expected))
                    Report("NT0001", argument, $"'{operation.Id}' needs {expected}, not {actual}");
            }
        }
        else if (rule.Rule.Form == DeclarativeForm.Literal && !TryLiteral(node, rule.Rule, out _))
        {
            Report("NT0003", node, $"'{Text(node)}' is not a finite number");
        }

        if (rule.Rule.DeclaredTypeChild >= 0)
        {
            int child = tree.Child(node, rule.Rule.DeclaredTypeChild);
            if (Spelled(child) is { } text)
            {
                var lookup = DeclarativeLowering.TryResolveType(_catalog, text, out _);
                if (lookup == TypeLookup.Missing) Report("NT0002", child, $"'{text}' is not a type in the semantic catalog");
                else if (lookup == TypeLookup.Ambiguous) Report("NT0002", child, $"'{text}' names more than one type; qualify it");
            }
        }
    }

    internal HirNode? LowerRoot(LoweringContext context, int node)
    {
        var tree = _file.Tree;
        for (int parent = tree.Parent(node); parent >= 0; parent = tree.Parent(parent))
            if (_lowering.RuleFor(tree.Kind(parent)) is { Rule.Form: DeclarativeForm.Operation }) return null;
        return Lower(context, node);
    }

    HirNode? Lower(LoweringContext context, int node)
    {
        var type = TypeOf(node);
        if (type is null || type.Equals(SemanticTypes.Error)) return null;
        var tree = _file.Tree;
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Ambiguous) return Lower(context, tree.Child(node, 0));
        if (_lowering.RuleFor(kind) is { } rule && rule.Rule.Form != DeclarativeForm.None)
        {
            if (rule.Rule.Form == DeclarativeForm.Literal)
                return TryLiteral(node, rule.Rule, out var value) ? new HirConstant(value, type, context.Origin(node)) : null;
            var operation = rule.Operation!;
            var arguments = new HirNode[rule.Rule.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                var argument = Lower(context, tree.Child(node, rule.Rule.Arguments[i]));
                if (argument is null || !argument.Type.Equals(operation.Inputs[i])) return null;
                arguments[i] = argument;
            }
            return new HirOperation(operation, arguments, [context.Origin(node)]);
        }
        if (_file.HasReference(node))
        {
            var symbol = _file.SymbolOf(node)!;
            return new HirSymbolRef(SemanticSymbol.From(symbol, ModuleOf(symbol), type), context.Origin(node));
        }
        for (int k = 0; k < tree.ChildCount(node); k++)
        {
            int child = tree.Child(node, k);
            if (TypeOf(child) is not null) return Lower(context, child);
        }
        return null;
    }

    string ModuleOf(Symbol symbol)
    {
        var declaring = symbol.Path == _file.Path ? _file : _file.RelatedFile(symbol.Path!);
        int kind = declaring.Tree.Kind(symbol.Node);
        return declaring.Tree.Language!.ModuleById(SyntaxKinds.ModuleOf(kind))!.Name;
    }

    bool TryLiteral(int node, DeclarativeRule rule, out float value)
    {
        int child = rule.Arguments[0];
        var text = Spelled(child < 0 ? node : _file.Tree.Child(node, child));
        value = 0;
        return text is not null &&
               float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               float.IsFinite(value);
    }

    /// <summary>The node's tokens without trivia; null when empty or any part is Missing.</summary>
    string? Spelled(int node)
    {
        var text = new StringBuilder();
        return Append(node, text) && text.Length > 0 ? text.ToString() : null;
    }

    bool Append(int node, StringBuilder text)
    {
        var tree = _file.Tree;
        if ((tree.Flags(node) & NodeFlags.Missing) != 0) return false;
        int count = tree.ChildCount(node);
        if (count == 0)
        {
            int kind = tree.Kind(node);
            if (kind != SyntaxKinds.Empty && kind != SyntaxKinds.List) text.Append(tree.GetText(node));
            return true;
        }
        for (int k = 0; k < count; k++)
            if (!Append(tree.Child(node, k), text)) return false;
        return true;
    }

    string Text(int node) => Spelled(node) ?? "";

    void Report(string code, int node, string message) =>
        _diagnostics!.Add(new SemanticDiagnostic(code, _file.Tree.Span(node), message));
}
