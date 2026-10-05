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
    readonly HashSet<int> _activeInitializers = new();
    readonly HashSet<int> _typing = new();
    readonly List<SemanticDiagnostic> _diagnostics = new();
    readonly bool[] _checkedNodes;
    bool _allChecked;

    internal DeclarativeTypes(FileSemantics file, DeclarativeLowering lowering, SemanticCatalog catalog)
    {
        _file = file;
        _lowering = lowering;
        _catalog = catalog;
        _checkedNodes = new bool[file.Tree.NodeCount];
    }

    /// <summary>The node's type; null when untyped, <see cref="SemanticTypes.Error"/> when invalid.</summary>
    public SemanticType? TypeOf(int node)
    {
        if (_types.TryGetValue(node, out var cached)) return cached;
        if (!_typing.Add(node)) return SemanticTypes.Error; // a cyclic expansion: lowering reports it
        SemanticType? type;
        try { type = Compute(node); }
        finally { _typing.Remove(node); }
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

    /// <summary>
    /// The type the enclosing operation expects of <paramref name="node"/>: the input it is passed as,
    /// through parents without lowering clauses (parentheses); the element type for a sequence item.
    /// Null when no operation takes it, or its computed operation is not known yet.
    /// </summary>
    public SemanticType? ExpectedTypeOf(int node)
    {
        var tree = _file.Tree;
        if (node < 0 || node >= tree.NodeCount) return null;
        for (int below = -1, child = node, parent = tree.Parent(node); parent >= 0; below = child, child = parent, parent = tree.Parent(parent))
        {
            if (_lowering.RuleFor(tree.Kind(parent)) is not { } rule || rule.Rule.Form == DeclarativeForm.None) continue;
            if (rule.Rule.Form != DeclarativeForm.Operation || OperationFor(parent, rule) is not { } operation) return null;
            for (int i = 0; i < rule.Rule.Arguments.Count && i < operation.Inputs.Count; i++)
            {
                if (rule.Rule.ArgumentTexts[i]) continue;
                if (tree.Child(parent, rule.Rule.Arguments[i]) != child) continue;
                if (rule.ArgumentSequenceTypes[i] is null && !rule.Rule.ArgumentInferredSequences[i])
                    return rule.ArgumentOptionalTypes[i] ?? operation.Inputs[i];
                // A sequence: the node is (inside) one of its items.
                return below >= 0 ? rule.ArgumentSequenceTypes[i] ?? DeclarativeLowering.SequenceElement(operation.Inputs[i]) : null;
            }
            return null;
        }
        return null;
    }

    SemanticType? Compute(int node)
    {
        var tree = _file.Tree;
        if ((tree.Flags(node) & NodeFlags.Missing) != 0) return SemanticTypes.Error;
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Ambiguous)
            return tree.ChildCount(node) > 0 ? TypeOf(tree.Child(node, 0)) : SemanticTypes.Error;
        if (_lowering.RuleFor(kind) is { } rule && rule.Rule.Form != DeclarativeForm.None)
            return rule.Rule.Form switch
            {
                DeclarativeForm.Operation => OperationFor(node, rule)?.Result ??
                    (MissingOptionalOperation(node, rule) ? null : SemanticTypes.Error),
                DeclarativeForm.Literal => TryLiteral(node, rule.Rule, out _) ? rule.LiteralType : SemanticTypes.Error,
                DeclarativeForm.Text => Spelled(FieldNode(node, rule.Rule)) is not null
                    ? SemanticTypes.Text : SemanticTypes.Error,
                DeclarativeForm.Sequence => SemanticTypes.SequenceOf(rule.LiteralType!),
                DeclarativeForm.Repeat => SemanticTypes.SequenceOf(SemanticTypes.SequenceOf(rule.LiteralType!)),
                DeclarativeForm.Value => TryValue(node, rule.Rule, out _) ? ValueType(node, rule) : SemanticTypes.Error,
                DeclarativeForm.Reference => ReferenceType(node, rule),
                DeclarativeForm.Template => null,
                DeclarativeForm.Expand => TemplateOf(node) is { } template
                    ? template.Types.TypeOf(template.Body) : SemanticTypes.Error,
                _ => SemanticTypes.Error,
            };
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
        if (!_allChecked) { CheckNodes(_file.Tree.Root, ancestors: false); _allChecked = true; }
        return _diagnostics;
    }

    internal IReadOnlyList<SemanticDiagnostic> DiagnosticsForSubtree(int node)
    {
        if (!_allChecked) CheckNodes(node, ancestors: true);
        return _diagnostics;
    }

    void CheckNodes(int root, bool ancestors)
    {
        if (_lowering.IsEmpty) return;
        var tree = _file.Tree;
        foreach (int node in SemanticCheckTraversal.Nodes(tree, root, ancestors))
            if (!_checkedNodes[node])
            {
                _checkedNodes[node] = true;
                if ((tree.Flags(node) & NodeFlags.Missing) == 0 && _lowering.RuleFor(tree.Kind(node)) is { } rule) Check(node, rule);
            }
    }

    void Check(int node, DeclarativeLowering.ResolvedRule rule)
    {
        var tree = _file.Tree;
        if (rule.Rule.Form == DeclarativeForm.Repeat)
        {
            int count = tree.Child(node, rule.Rule.Arguments[0]);
            int iterator = tree.Child(node, rule.Rule.Arguments[1]);
            var symbol = _file.Binding.Declarations.FirstOrDefault(declaration => declaration.Node == iterator);
            if (TypeOf(count)?.Equals(SemanticTypes.Scalar) != true)
                Report("NT0001", count, "repeat count needs Core.Scalar");
            if (symbol is null || TypeOfSymbol(symbol)?.Equals(SemanticTypes.Scalar) != true)
                Report("NT0004", iterator, "repeat iterator needs a declared Core.Scalar symbol");
            foreach (var item in SequenceItems(tree.Child(node, rule.Rule.Arguments[2]), rule.Rule.SequenceStride))
                if (TypeOf(item)?.Equals(rule.LiteralType) != true)
                    Report("NT0001", item, $"repeat template needs {rule.LiteralType}");
        }
        else if (rule.Rule.Form == DeclarativeForm.Operation)
        {
            var operation = OperationFor(node, rule);
            if (operation is null && MissingOptionalOperation(node, rule)) return;
            if (operation is null || operation.Inputs.Count != rule.Rule.Arguments.Count)
            {
                Report("NT0006", node, "computed operation is absent or differs from the semantic catalog");
                return;
            }
            for (int i = 0; i < rule.Rule.Arguments.Count; i++)
            {
                int argument = tree.Child(node, rule.Rule.Arguments[i]);
                var expected = operation.Inputs[i];
                if (rule.Rule.ArgumentTexts[i])
                {
                    if (Spelled(argument) is null)
                        Report("NT0005", argument, "text argument has no complete source text");
                    else if (!expected.Equals(SemanticTypes.Text))
                        Report("NT0001", argument, $"'{operation.Id}' needs {expected}, not {SemanticTypes.Text}");
                    continue;
                }
                if (rule.ArgumentOptionalTypes[i] is { } optionalType)
                {
                    if (!expected.Equals(SemanticTypes.OptionalOf(optionalType)))
                        Report("NT0001", argument, $"'{operation.Id}' needs {expected}, not an optional of {optionalType}");
                    if (tree.Kind(argument) != SyntaxKinds.Empty)
                    {
                        var itemActual = TypeOf(argument);
                        if (itemActual is null)
                            Report("NT0004", argument, $"'{Text(argument)}' has no declared type; optional needs {optionalType}");
                        else if (!itemActual.Equals(SemanticTypes.Error) && !itemActual.Equals(optionalType))
                            Report("NT0001", argument, $"optional needs {optionalType}, not {itemActual}");
                    }
                    continue;
                }
                var sequenceType = rule.Rule.ArgumentInferredSequences[i]
                    ? DeclarativeLowering.SequenceElement(expected) : rule.ArgumentSequenceTypes[i];
                if (rule.Rule.ArgumentInferredSequences[i] && sequenceType is null)
                {
                    Report("NT0001", argument, $"'{operation.Id}' needs {expected}, which is not a sequence");
                    continue;
                }
                if (sequenceType is { } itemType)
                {
                    if (!expected.Equals(SemanticTypes.SequenceOf(itemType)))
                        Report("NT0001", argument, $"'{operation.Id}' needs {expected}, not a sequence of {itemType}");
                    foreach (var item in SequenceItems(argument, rule.Rule.ArgumentSequenceStrides[i]))
                    {
                        var itemActual = TypeOf(item);
                        if (itemActual is null)
                            Report("NT0004", item, $"'{Text(item)}' has no declared type; sequence needs {itemType}");
                        else if (!itemActual.Equals(SemanticTypes.Error) && !itemActual.Equals(itemType))
                            Report("NT0001", item, $"sequence needs {itemType}, not {itemActual}");
                    }
                    continue;
                }
                var actual = TypeOf(argument);
                if (actual is null)
                {
                    if (!UncoveredOperation(argument))
                        Report("NT0004", argument, $"'{Text(argument)}' has no declared type; '{operation.Id}' needs {expected}");
                }
                else if (!actual.Equals(SemanticTypes.Error) && !actual.Equals(expected))
                    Report("NT0001", argument, $"'{operation.Id}' needs {expected}, not {actual}");
            }
        }
        else if (rule.Rule.Form == DeclarativeForm.Expand)
        {
            CheckExpansion(node, rule.Rule);
        }
        else if (rule.Rule.Form == DeclarativeForm.Literal && !TryLiteral(node, rule.Rule, out _))
        {
            Report("NT0003", node, $"'{Text(node)}' is not a finite number");
        }
        else if (rule.Rule.Form == DeclarativeForm.Value && !TryValue(node, rule.Rule, out _))
        {
            Report("NT0003", node, "computed value is not a finite number");
        }
        else if (rule.Rule.Form == DeclarativeForm.Value && rule.Rule.TypeProperty is not null &&
                 ValueType(node, rule).Equals(SemanticTypes.Error))
        {
            Report("NT0002", node, "computed value type is not in the semantic catalog");
        }
        else if (rule.Rule.Form == DeclarativeForm.Reference &&
                 ReferenceType(node, rule)?.Equals(SemanticTypes.Error) == true)
        {
            Report("NT0002", node, "computed reference type is not in the semantic catalog");
        }
        else if (rule.Rule.Form == DeclarativeForm.Sequence)
        {
            foreach (var item in SequenceItems(node, rule.Rule))
            {
                var actual = TypeOf(item);
                if (actual is null)
                    Report("NT0004", item, $"'{Text(item)}' has no declared type; sequence needs {rule.LiteralType}");
                else if (!actual.Equals(SemanticTypes.Error) && !actual.Equals(rule.LiteralType))
                    Report("NT0001", item, $"sequence needs {rule.LiteralType}, not {actual}");
            }
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
            if (_lowering.RuleFor(tree.Kind(parent)) is { Rule.Form: DeclarativeForm.Operation or DeclarativeForm.Template }) return null;
        return Lower(context, node);
    }

    internal HirNode? LowerNested(LoweringContext context, int node) => Lower(context, node);

    HirNode? Lower(LoweringContext context, int node)
    {
        if (_lowering.RuleFor(_file.Tree.Kind(node)) is { Rule.Form: DeclarativeForm.Expand } expand)
            return LowerExpansion(context, node, expand.Rule);
        var type = TypeOf(node);
        if (type is null || type.Equals(SemanticTypes.Error)) return null;
        var tree = _file.Tree;
        int kind = tree.Kind(node);
        if (kind == SyntaxKinds.Ambiguous) return Lower(context, tree.Child(node, 0));
        if (_lowering.RuleFor(kind) is { } rule && rule.Rule.Form != DeclarativeForm.None)
        {
            if (rule.Rule.Form == DeclarativeForm.Repeat)
            {
                var count = Lower(context, tree.Child(node, rule.Rule.Arguments[0]));
                int iterator = tree.Child(node, rule.Rule.Arguments[1]);
                var symbol = _file.Binding.Declarations.FirstOrDefault(declaration => declaration.Node == iterator);
                if (count is null || symbol is null || TypeOfSymbol(symbol)?.Equals(SemanticTypes.Scalar) != true) return null;
                var items = new List<HirNode>();
                int template = tree.Child(node, rule.Rule.Arguments[2]);
                foreach (var item in SequenceItems(template, rule.Rule.SequenceStride))
                {
                    var lowered = Lower(context, item);
                    if (lowered is null) return null;
                    items.Add(lowered);
                }
                return new HirRepeat(count, SemanticSymbol.From(symbol, ModuleOf(symbol, iterator), SemanticTypes.Scalar),
                    new HirSequence(rule.LiteralType!, items, context.Origin(template)), context.Origin(node));
            }
            if (rule.Rule.Form == DeclarativeForm.Literal)
                return TryLiteral(node, rule.Rule, out var value) ? new HirConstant(value, type, context.Origin(node)) : null;
            if (rule.Rule.Form == DeclarativeForm.Value)
                return TryValue(node, rule.Rule, out var value) ? new HirConstant(value, type, context.Origin(node)) : null;
            if (rule.Rule.Form == DeclarativeForm.Reference)
            {
                var binding = _file.SymbolOf(node);
                if (binding is not null && Substitute(context, binding, node, type) is { } argument) return argument;
                if (binding is not null && rule.Rule.InitializerProperty?.Read(_file, node) is { } source)
                {
                    if (source is not int initializer || initializer < 0 || initializer >= tree.NodeCount)
                    {
                        context.Report("NH0006", context.Origin(node), "Reference initializer is not a node in this file.");
                        return null;
                    }
                    if (!_activeInitializers.Add(initializer))
                    {
                        context.Report("NH0005", context.Origin(node), "Cyclic reference initializer prevents lowering.");
                        return null;
                    }
                    try
                    {
                        var value = HirLowering.LowerNested(context, initializer);
                        if (value is not null && !value.Type.Equals(type))
                        {
                            context.Report("NH0006", context.Origin(node), "Reference initializer has the wrong semantic type.");
                            return null;
                        }
                        return value;
                    }
                    finally { _activeInitializers.Remove(initializer); }
                }
                return binding is null ? null :
                    new HirSymbolRef(SemanticSymbol.From(binding, ModuleOf(binding, node), type), context.Origin(node));
            }
            if (rule.Rule.Form == DeclarativeForm.Text)
                return Spelled(FieldNode(node, rule.Rule)) is { } text
                    ? new HirText(text, context.Origin(node)) : null;
            if (rule.Rule.Form == DeclarativeForm.Sequence)
            {
                var items = new List<HirNode>();
                foreach (var item in SequenceItems(node, rule.Rule))
                {
                    var lowered = Lower(context, item);
                    if (lowered is null || !lowered.Type.Equals(rule.LiteralType)) return null;
                    items.Add(lowered);
                }
                return new HirSequence(rule.LiteralType!, items, context.Origin(node));
            }
            var operation = OperationFor(node, rule);
            if (operation is null || operation.Inputs.Count != rule.Rule.Arguments.Count) return null;
            var arguments = new HirNode[rule.Rule.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                int source = tree.Child(node, rule.Rule.Arguments[i]);
                HirNode? argument;
                if (rule.Rule.ArgumentTexts[i])
                    argument = Spelled(source) is { } text ? new HirText(text, context.Origin(source)) : null;
                else if (rule.ArgumentOptionalTypes[i] is { } optionalType)
                {
                    HirNode? item = null;
                    if (tree.Kind(source) != SyntaxKinds.Empty)
                    {
                        item = Lower(context, source);
                        if (item is null || !item.Type.Equals(optionalType)) return null;
                    }
                    argument = new HirOptional(optionalType, item, context.Origin(source));
                }
                else if ((rule.Rule.ArgumentInferredSequences[i]
                    ? DeclarativeLowering.SequenceElement(operation.Inputs[i]) : rule.ArgumentSequenceTypes[i]) is { } itemType)
                {
                    var items = new List<HirNode>();
                    foreach (var item in SequenceItems(source, rule.Rule.ArgumentSequenceStrides[i]))
                    {
                        var lowered = Lower(context, item);
                        if (lowered is null || !lowered.Type.Equals(itemType)) return null;
                        items.Add(lowered);
                    }
                    argument = new HirSequence(itemType, items, context.Origin(source));
                }
                else argument = Lower(context, source);
                if (argument is null || !argument.Type.Equals(operation.Inputs[i])) return null;
                arguments[i] = argument;
            }
            return new HirOperation(operation, arguments, [context.Origin(node)]);
        }
        if (_file.HasReference(node))
        {
            var symbol = _file.SymbolOf(node)!;
            return Substitute(context, symbol, node, type) ??
                new HirSymbolRef(SemanticSymbol.From(symbol, ModuleOf(symbol, node), type), context.Origin(node));
        }
        for (int k = 0; k < tree.ChildCount(node); k++)
        {
            int child = tree.Child(node, k);
            if (TypeOf(child) is not null) return Lower(context, child);
        }
        return null;
    }

    /// <summary>The template an expansion names: its file's types, declaring node, body, and parameter items.</summary>
    sealed record Template(DeclarativeTypes Types, int Node, int Body, IReadOnlyList<int> Parameters);

    /// <summary>The template the expansion at <paramref name="node"/> names; null when unresolved or not a template.</summary>
    Template? TemplateOf(int node)
    {
        if (_file.SymbolOf(node) is not { IsBuiltin: false, Path: { } path } symbol) return null;
        var types = path == _file.Path ? this : _file.RelatedFile(path).DeclarativeTypes;
        var tree = types._file.Tree;
        if (types._lowering.RuleFor(tree.Kind(symbol.Node)) is not { Rule: { Form: DeclarativeForm.Template } rule }) return null;
        return new Template(types, symbol.Node, tree.Child(symbol.Node, rule.Arguments[0]),
            types.SequenceItems(tree.Child(symbol.Node, rule.Arguments[1]), rule.SequenceStride).ToArray());
    }

    /// <summary>The parameter a template's parameter item declares; null when it declares none.</summary>
    Symbol? ParameterAt(int item) => _file.Binding.Declarations.FirstOrDefault(declaration => declaration.Node == item);

    TextSpan NameSpan(int node) =>
        _file.Binding.References.FirstOrDefault(reference => reference.Node == node)?.NameSpan ?? _file.Tree.Span(node);

    void CheckExpansion(int node, DeclarativeRule rule)
    {
        if (_file.SymbolOf(node) is not { } symbol) return; // the binder reports an unresolved name
        if (TemplateOf(node) is not { } template)
        {
            Report("NT0007", NameSpan(node), $"'{symbol.Name}' is not a template");
            return;
        }
        var arguments = SequenceItems(_file.Tree.Child(node, rule.Arguments[0]), rule.SequenceStride).ToArray();
        if (arguments.Length != template.Parameters.Count)
        {
            Report("NT0008", NameSpan(node), $"'{symbol.Name}' takes {template.Parameters.Count} arguments, not {arguments.Length}");
            return;
        }
        for (int i = 0; i < arguments.Length; i++)
        {
            if (template.Types.ParameterAt(template.Parameters[i]) is not { } parameter) continue;
            var expected = TypeOfSymbol(parameter);
            if (expected is null || expected.Equals(SemanticTypes.Error)) continue;
            var actual = TypeOf(arguments[i]);
            if (actual is null)
            {
                if (!UncoveredOperation(arguments[i]))
                    Report("NT0004", arguments[i], $"'{Text(arguments[i])}' has no declared type; '{parameter.Name}' needs {expected}");
            }
            else if (!actual.Equals(SemanticTypes.Error) && !actual.Equals(expected))
                Report("NT0001", arguments[i], $"'{parameter.Name}' of '{symbol.Name}' needs {expected}, not {actual}");
        }
    }

    /// <summary>
    /// Lowers the template body with each parameter bound to its argument, lowered here. The result's origins
    /// start with the expansion's. Blocks, reporting in the template's file, on errors within the template
    /// and on a cycle (NH0007, at the name of the expansion that closes it).
    /// </summary>
    HirNode? LowerExpansion(LoweringContext context, int node, DeclarativeRule rule)
    {
        if (TemplateOf(node) is not { } template) return null;
        var argumentNodes = SequenceItems(_file.Tree.Child(node, rule.Arguments[0]), rule.SequenceStride).ToArray();
        if (argumentNodes.Length != template.Parameters.Count) return null;
        var key = (template.Types._file.Path, template.Node);
        if (!context.ActiveTemplates.Add(key))
        {
            context.Report("NH0007", new SourceOrigin(_file.Path, context.SnapshotId, node, NameSpan(node)),
                "Cyclic template expansion prevents lowering.");
            return null;
        }
        try
        {
            var arguments = new Dictionary<Symbol, HirNode>(ReferenceEqualityComparer.Instance);
            var body = context.Expanding(template.Types._file, arguments);
            if (!HirLowering.Admit(body, template.Node)) return null;
            for (int i = 0; i < argumentNodes.Length; i++)
            {
                if (template.Types.ParameterAt(template.Parameters[i]) is not { } parameter) return null;
                var argument = Lower(context, argumentNodes[i]);
                if (argument is null || !argument.Type.Equals(TypeOfSymbol(parameter))) return null;
                arguments[parameter] = argument;
            }
            var lowered = template.Types.Lower(body, template.Body);
            return lowered is null ? null
                : HirTraversal.WithOrigins(lowered, lowered.Origins.Prepend(context.Origin(node)).Distinct().ToArray());
        }
        finally { context.ActiveTemplates.Remove(key); }
    }

    /// <summary>The argument bound to a template parameter reference, keeping the reference's origin first.</summary>
    HirNode? Substitute(LoweringContext context, Symbol symbol, int node, SemanticType type) =>
        context.Arguments is { } arguments && arguments.TryGetValue(symbol, out var argument)
            ? HirTraversal.Rewrite(new HirSymbolRef(SemanticSymbol.From(symbol, ModuleOf(symbol, node), type), context.Origin(node)), _ => argument)
            : null;

    string ModuleOf(Symbol symbol, int referenceNode)
    {
        if (symbol.IsBuiltin || symbol.Path is null)
            return _file.Tree.Language!.ModuleById(SyntaxKinds.ModuleOf(_file.Tree.Kind(referenceNode)))!.Name;
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

    bool TryValue(int node, DeclarativeRule rule, out float value)
    {
        var computed = rule.Property!.Read(_file, node);
        value = computed is float number ? number : 0f;
        return computed is float && float.IsFinite(value);
    }

    SemanticType ValueType(int node, DeclarativeLowering.ResolvedRule rule)
    {
        if (rule.Rule.TypeProperty is null) return rule.LiteralType!;
        var computed = rule.Rule.TypeProperty.Read(_file, node);
        return computed is SemanticType type && _catalog.Types.TryGetValue(type.Id, out var exported) &&
            exported.Equals(type) ? type : SemanticTypes.Error;
    }

    SemanticType? ReferenceType(int node, DeclarativeLowering.ResolvedRule rule)
    {
        var computed = rule.Rule.TypeProperty!.Read(_file, node);
        if (computed is null) return null;
        return computed is SemanticType type && _catalog.Types.TryGetValue(type.Id, out var exported) &&
            exported.Equals(type) ? type : SemanticTypes.Error;
    }

    OperationSignature? OperationFor(int node, DeclarativeLowering.ResolvedRule rule)
    {
        if (rule.Rule.OperationProperty is null) return rule.Operation;
        var computed = rule.Rule.OperationProperty.Read(_file, node);
        return computed is OperationSignature operation &&
            _catalog.Operations.TryGetValue(operation.Id, out var exported) && exported.Equals(operation)
            ? operation : null;
    }

    bool MissingOptionalOperation(int node, DeclarativeLowering.ResolvedRule rule) =>
        rule.Rule.OptionalOperation && rule.Rule.OperationProperty!.Read(_file, node) is null;

    /// <summary>
    /// A missing optional computed operation, perhaps in parentheses: an operand combination its language
    /// does not cover, which the language's own checks explain. An operation it is passed to does not
    /// report it again as untyped.
    /// </summary>
    bool UncoveredOperation(int node)
    {
        var tree = _file.Tree;
        if (_lowering.RuleFor(tree.Kind(node)) is { } rule && rule.Rule.Form != DeclarativeForm.None)
            return rule.Rule.Form == DeclarativeForm.Operation && MissingOptionalOperation(node, rule);
        if (_file.HasReference(node)) return false;
        for (int k = 0; k < tree.ChildCount(node); k++)
            if (UncoveredOperation(tree.Child(node, k))) return true;
        return false;
    }

    int FieldNode(int node, DeclarativeRule rule) =>
        rule.Arguments[0] < 0 ? node : _file.Tree.Child(node, rule.Arguments[0]);

    IEnumerable<int> SequenceItems(int node, DeclarativeRule rule)
    {
        var tree = _file.Tree;
        int list = FieldNode(node, rule);
        return SequenceItems(list, rule.SequenceStride);
    }

    IEnumerable<int> SequenceItems(int list, int stride)
    {
        var tree = _file.Tree;
        for (int i = 0; i < tree.ChildCount(list); i += stride)
            yield return tree.Child(list, i);
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

    void Report(string code, int node, string message) => Report(code, _file.Tree.Span(node), message);

    void Report(string code, TextSpan span, string message) =>
        _diagnostics!.Add(new SemanticDiagnostic(code, span, message));
}
