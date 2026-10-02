using Nitrogen.Binding;
using System.Runtime.CompilerServices;

namespace Nitrogen.Semantics;

/// <summary>
/// One file's semantics (issue 239). Properties are evaluated on demand and memoized per node.
/// <list type="bullet">
/// <item>An <c>out</c> property comes from the node's own delegate.</item>
/// <item>An <c>in</c> property comes from the parent's assignment, found through group, list and
/// ambiguity wrappers.</item>
/// </list>
/// A re-entrant request is a cycle (NS0001) and an exception is NS0002; both yield the default.
/// Owned by a <see cref="ProjectSemantics"/>, which replaces it on any project change. Not thread-safe.
/// </summary>
public sealed class FileSemantics
{
    const byte Computing = 1, Done = 2;

    readonly ProjectSemantics _project;
    readonly Language _language;
    readonly Dictionary<Property, object> _slots = new();
    readonly List<SemanticDiagnostic> _evaluation = new();
    readonly HashSet<(int Node, string Property, string Code)> _reported = new();
    Dictionary<int, Reference>? _references;
    readonly List<SemanticDiagnostic> _checks = new();
    readonly bool[] _checkedNodes;
    bool _allChecked;
    Nitrogen.Semantic.DeclarativeTypes? _declarative;

    internal FileSemantics(ProjectSemantics project, FileBinding binding)
    {
        _project = project;
        Binding = binding;
        Tree = binding.Tree;
        _checkedNodes = new bool[Tree.NodeCount];
        _language = binding.Tree.Language!;
    }

    public FileBinding Binding { get; }

    public SyntaxTree Tree { get; }

    public string Path => Binding.Path;

    /// <summary>A document already bound in the same project, for cross-file lowering.</summary>
    public FileSemantics RelatedFile(string path) => _project[path];

    /// <summary>Types from the language's declarative clauses (issue 251).</summary>
    public Nitrogen.Semantic.DeclarativeTypes DeclarativeTypes =>
        _declarative ??= new Nitrogen.Semantic.DeclarativeTypes(this, _language.Declarative, _language.SemanticCatalog);

    sealed class Slot<T>
    {
        readonly int _count;
        Dictionary<int, Entry>? _sparse;
        T[]? _values;
        byte[]? _states;
        readonly record struct Entry(T Value, byte State);

        public Slot(int count)
        {
            _count = count;
            if (count <= 512) { _values = new T[count]; _states = new byte[count]; }
            else _sparse = new Dictionary<int, Entry>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(int node, out byte state, out T value)
        {
            if ((uint)node >= (uint)_count) throw new IndexOutOfRangeException();
            if (_states is not null) { state = _states[node]; value = _values![node]; }
            else if (_sparse!.TryGetValue(node, out var entry)) { state = entry.State; value = entry.Value; }
            else { state = 0; value = default!; }
        }

        public void Write(int node, byte state, T value)
        {
            if (_states is not null) { _states[node] = state; _values![node] = value; return; }
            _sparse![node] = new Entry(value, state);
            if (_sparse.Count < _count / 8) return;
            _values = new T[_count];
            _states = new byte[_count];
            foreach (var (index, entry) in _sparse) { _values[index] = entry.Value; _states[index] = entry.State; }
            _sparse = null;
        }
    }

    public T Get<T>(int node, Property<T> property)
    {
        if (!_slots.TryGetValue(property, out var boxed)) _slots[property] = boxed = new Slot<T>(Tree.NodeCount);
        var slot = (Slot<T>)boxed;
        slot.Read(node, out byte state, out T cached);
        if (state == Done) return cached;
        if (state == Computing)
        {
            Report(node, property.Name, SemanticCodes.Cycle, $"'{property.Name}' depends on itself");
            return property.Default();
        }

        slot.Write(node, Computing, default!);
        T value;
        try
        {
            value = property.IsInherited ? Inherited(node, property) : Synthesized(node, property);
        }
        catch (Exception error)
        {
            Report(node, property.Name, SemanticCodes.Failed, $"'{property.Name}' failed: {error.GetType().Name}: {error.Message}");
            value = property.Default();
        }
        slot.Write(node, Done, value);
        return value;
    }

    T Synthesized<T>(int node, Property<T> property)
    {
        if ((Tree.Flags(node) & NodeFlags.Missing) != 0) return property.Default();
        int kind = Tree.Kind(node);
        if (kind == SyntaxKinds.Ambiguous) return Tree.ChildCount(node) > 0 ? Get(Tree.Child(node, 0), property) : property.Default();
        if (RuleOf(kind) is { } rule)
            foreach (var entry in rule.Outs)
                if (ReferenceEquals(entry.Property, property)) return ((SemanticsOut<T>)entry).Compute(this, node);
        return property.Default();
    }

    T Inherited<T>(int node, Property<T> property)
    {
        int child = node, inner = -1, parent = Tree.Parent(node);
        while (parent >= 0)
        {
            int kind = Tree.Kind(parent);
            if (kind == SyntaxKinds.Group) inner = IndexOf(parent, child);
            else if (kind != SyntaxKinds.List && kind != SyntaxKinds.Ambiguous) break;
            child = parent;
            parent = Tree.Parent(parent);
        }
        if (parent < 0 || RuleOf(Tree.Kind(parent)) is not { } rule) return property.Default();
        int index = IndexOf(parent, child);
        foreach (var entry in rule.Ins)
            if (ReferenceEquals(entry.Property, property) && entry.Child == index && entry.Inner == inner)
                return ((SemanticsIn<T>)entry).Compute(this, parent);
        return property.Default();
    }

    /// <summary>The nearest ancestor that is not a group, list or ambiguity wrapper; -1 for the root.</summary>
    public int ParentOf(int node)
    {
        int parent = Tree.Parent(node);
        while (parent >= 0 && Tree.Kind(parent) is SyntaxKinds.Group or SyntaxKinds.List or SyntaxKinds.Ambiguous) parent = Tree.Parent(parent);
        return parent;
    }

    /// <summary>Whether the node's own rule defines the <c>out</c> <paramref name="property"/>: hover shows the innermost such node.</summary>
    public bool Defines(int node, Property property) =>
        RuleOf(Tree.Kind(node)) is { } rule && rule.Outs.Any(o => ReferenceEquals(o.Property, property));

    /// <summary>The symbol the node's own reference resolves to; null when it has none, or it is unresolved or ambiguous.</summary>
    public Symbol? SymbolOf(int node)
    {
        if (!ReferencesByNode().TryGetValue(node, out var found)) return null;
        var symbols = _project.Project.Resolve(found);
        return symbols.Count == 1 ? symbols[0] : null;
    }

    /// <summary>Whether the node carries a reference clause (issue 251).</summary>
    internal bool HasReference(int node) => ReferencesByNode().ContainsKey(node);

    Dictionary<int, Reference> ReferencesByNode()
    {
        if (_references is null)
        {
            _references = new Dictionary<int, Reference>();
            foreach (var reference in Binding.References) _references.TryAdd(reference.Node, reference);
        }
        return _references;
    }

    public T GetSymbol<T>(Symbol symbol, SymbolProperty<T> property) => _project.GetSymbol(symbol, property);

    /// <summary>What the declaring node's block sets for <paramref name="property"/> of <paramref name="symbol"/>, declared in this file.</summary>
    internal T Declared<T>(Symbol symbol, SymbolProperty<T> property)
    {
        if (RuleOf(Tree.Kind(symbol.Node)) is { } rule)
            foreach (var entry in rule.Symbols)
                if (ReferenceEquals(entry.Property, property))
                {
                    try
                    {
                        return ((SemanticsSymbol<T>)entry).Compute(this, symbol.Node);
                    }
                    catch (Exception error)
                    {
                        Report(symbol.Node, property.Name, SemanticCodes.Failed, $"'{property.Name}' failed: {error.GetType().Name}: {error.Message}");
                        return property.Default(symbol.Kind, symbol.Name);
                    }
                }
        return property.Default(symbol.Kind, symbol.Name);
    }

    /// <summary>
    /// Every check of every node (one reading of an ambiguity, no Missing node), with the NS
    /// diagnostics evaluation has raised in this file so far, in text order.
    /// </summary>
    public IReadOnlyList<SemanticDiagnostic> Diagnostics()
    {
        if (!_allChecked) { CheckNodes(Tree.Root, ancestors: false); _allChecked = true; }
        return _checks.Concat(_evaluation).Concat(DeclarativeTypes.Diagnostics())
            .OrderBy(d => d.Span.Start)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Check a subtree and its ancestors, returning diagnostics inside its span.
    /// Other checks remain pending for Diagnostics(). Property dependencies still evaluate lazily.
    /// Use only when unrelated sibling checks cannot report errors at this subtree's source span.</summary>
    public IReadOnlyList<SemanticDiagnostic> DiagnosticsForSubtree(int node)
    {
        if (node < 0 || node >= Tree.NodeCount) throw new ArgumentOutOfRangeException(nameof(node));
        if (!_allChecked) CheckNodes(node, ancestors: true);
        var declarative = DeclarativeTypes.DiagnosticsForSubtree(node);
        var span = Tree.Span(node);
        return _checks.Concat(_evaluation).Concat(declarative)
            .Where(error => error.Span.Start >= span.Start && error.Span.End <= span.End)
            .OrderBy(error => error.Span.Start).ThenBy(error => error.Code, StringComparer.Ordinal).ToList();
    }

    void CheckNodes(int root, bool ancestors)
    {
        foreach (int node in SemanticCheckTraversal.Nodes(Tree, root, ancestors))
            if (!_checkedNodes[node])
            {
                _checkedNodes[node] = true;
                if ((Tree.Flags(node) & NodeFlags.Missing) == 0 && RuleOf(Tree.Kind(node)) is { } rule) RunChecks(node, rule);
            }
    }

    void RunChecks(int node, SemanticsRule rule)
    {
        foreach (var check in rule.Checks)
        {
            try
            {
                if (!check.Holds(this, node)) _checks!.Add(new SemanticDiagnostic(check.Code, check.Span(this, node), check.Message(this, node)));
            }
            catch (Exception error)
            {
                Report(node, "check " + check.Code, SemanticCodes.Failed, $"check {check.Code} failed: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    internal void Report(int node, string property, string code, string message)
    {
        if (_reported.Add((node, property, code))) _evaluation.Add(new SemanticDiagnostic(code, Tree.Span(node), message));
    }

    SemanticsRule? RuleOf(int kind)
    {
        int module = SyntaxKinds.ModuleOf(kind);
        return module == 0 ? null : _language.ModuleById(module)?.GetSemantics(SyntaxKinds.LocalOf(kind));
    }

    int IndexOf(int parent, int child)
    {
        for (int k = 0; k < Tree.ChildCount(parent); k++)
            if (Tree.Child(parent, k) == child) return k;
        return -1;
    }
}
