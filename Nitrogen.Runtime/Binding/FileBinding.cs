using System.Text;

namespace Nitrogen.Binding;

/// <summary>
/// One file's binding (issue 237): its declarations, references and scopes, and the duplicates
/// found within one scope. Depends only on the file's tree; a <see cref="Project"/> resolves
/// references across files. The tree must stay alive (undisposed) while the binding is used.
/// </summary>
public sealed class FileBinding
{
    internal FileBinding(string path, SyntaxTree tree, BindingScope[] scopes, Symbol[] declarations, Reference[] references,
        Hole[] holes, BindingDiagnostic[] diagnostics)
    {
        Path = path;
        Tree = tree;
        Scopes = scopes;
        Declarations = declarations;
        References = references;
        Holes = holes;
        Diagnostics = diagnostics;
    }

    public string Path { get; }

    public SyntaxTree Tree { get; }

    /// <summary>Declarations in text order.</summary>
    public IReadOnlyList<Symbol> Declarations { get; }

    /// <summary>References in text order; an optional reference precedes the references inside it.</summary>
    public IReadOnlyList<Reference> References { get; }

    /// <summary>References whose name is Missing, in text order: where completion may offer names.</summary>
    public IReadOnlyList<Hole> Holes { get; }

    /// <summary>Duplicates within one scope; the project adds the rest.</summary>
    public IReadOnlyList<BindingDiagnostic> Diagnostics { get; }

    /// <summary>Scope 0 is the file; a scope's parent always precedes it.</summary>
    internal BindingScope[] Scopes { get; }

    /// <summary>Walks <paramref name="tree"/> once, following its modules' binding tables.</summary>
    public static FileBinding Bind(string path, SyntaxTree tree) => new Binder(path, tree).Run();
}

internal sealed class BindingScope(int parent, int node, int nodeKind, TextSpan span)
{
    readonly Dictionary<(string Kind, string Name), List<Symbol>> _byName = new();
    readonly List<Symbol> _symbols = new();
    HashSet<string>? _open;

    public int Parent { get; } = parent;

    public int Node { get; } = node;

    /// <summary>The kind of the node that opened the scope: scoped built-ins look for it.</summary>
    public int NodeKind { get; } = nodeKind;

    public TextSpan Span { get; } = span;

    public IReadOnlyList<Symbol> Symbols => _symbols;

    public IEnumerable<List<Symbol>> Groups => _byName.Values;

    public void Add(Symbol symbol)
    {
        if (!_byName.TryGetValue((symbol.Kind, symbol.Name), out var list)) _byName[(symbol.Kind, symbol.Name)] = list = new List<Symbol>();
        list.Add(symbol);
        _symbols.Add(symbol);
    }

    public List<Symbol>? Find(string kind, string name) => _byName.TryGetValue((kind, name), out var list) ? list : null;

    /// <summary>A dynamic declaration of <paramref name="kind"/> was made here: unresolved references of that kind are unknown, not errors.</summary>
    public void Open(string kind) => (_open ??= new HashSet<string>(StringComparer.Ordinal)).Add(kind);

    public bool IsOpen(string kind) => _open is not null && _open.Contains(kind);
}

internal sealed class Binder
{
    readonly string _path;
    readonly SyntaxTree _tree;
    readonly Language _language;
    readonly List<BindingScope> _scopes = new();
    readonly List<Symbol> _declarations = new();
    readonly List<Reference> _references = new();
    readonly List<Hole> _holes = new();
    readonly StringBuilder _name = new();

    public Binder(string path, SyntaxTree tree)
    {
        _path = path;
        _tree = tree;
        _language = tree.Language ?? throw new ArgumentException("The tree has no language; parse it with a Language to bind it.", nameof(tree));
    }

    public FileBinding Run()
    {
        _scopes.Add(new BindingScope(-1, _tree.Root, _tree.Kind(_tree.Root), _tree.Span(_tree.Root)));
        var stack = new Stack<(int Node, int Scope, int Guard, int Enclosing)>();
        stack.Push((_tree.Root, 0, -1, -1));
        while (stack.Count > 0)
        {
            var (node, scope, guard, enclosing) = stack.Pop();
            int kind = _tree.Kind(node);
            if (kind == SyntaxKinds.Ambiguous)
            {
                // Tied alternatives: bind one reading, so no name is declared twice.
                if (_tree.ChildCount(node) > 0) stack.Push((_tree.Child(node, 0), scope, guard, enclosing));
                continue;
            }

            int childScope = scope, childGuard = guard, childEnclosing = enclosing;
            if (RuleOf(kind) is { } rule)
            {
                if (rule.Declares is { } declares) Declare(node, declares, scope);
                if (rule.References is { } references)
                {
                    int index = Reference(node, references, scope, guard, enclosing);
                    if (index >= 0)
                    {
                        if (references.Optional) childGuard = index;
                        childEnclosing = index;
                    }
                }
                if (rule.Scope)
                {
                    childScope = _scopes.Count;
                    _scopes.Add(new BindingScope(scope, node, kind, _tree.Span(node)));
                }
            }
            for (int k = _tree.ChildCount(node) - 1; k >= 0; k--) stack.Push((_tree.Child(node, k), childScope, childGuard, childEnclosing));
        }
        return new FileBinding(_path, _tree, _scopes.ToArray(), _declarations.ToArray(), _references.ToArray(), _holes.ToArray(), Duplicates());
    }

    BindingRule? RuleOf(int kind)
    {
        int module = SyntaxKinds.ModuleOf(kind);
        return module == 0 ? null : _language.ModuleById(module)?.GetBinding(SyntaxKinds.LocalOf(kind));
    }

    void Declare(int node, BindingDeclaration declares, int scope)
    {
        string? name = NameOf(node, declares.Child, out var span, out bool dynamic, out _);
        if (name is null) return;
        if (dynamic)
        {
            _scopes[scope].Open(declares.Kind);
            return;
        }
        var symbol = new Symbol(declares.Kind, name, _path, span, node, scope, declares.Export);
        _declarations.Add(symbol);
        _scopes[scope].Add(symbol);
    }

    int Reference(int node, BindingReference references, int scope, int guard, int enclosing)
    {
        string? name = NameOf(node, references.Child, out var span, out _, out bool missing);
        if (name is null)
        {
            if (missing) _holes.Add(new Hole(_path, references.Kinds, span.Start, scope, node, enclosing, references.Qualifier));
            return -1;
        }
        int index = _references.Count;
        _references.Add(new Reference(_path, index, references.Kinds, name, span, node, scope, references.Optional, guard, enclosing, references.Qualifier));
        return index;
    }

    /// <summary>
    /// The name a node's child (or the node, for -1) spells: its tokens' text without trivia. Null
    /// when it is absent or any part of it is Missing, so a parse error never becomes a binding error.
    /// </summary>
    string? NameOf(int node, int child, out TextSpan span, out bool dynamic, out bool missing)
    {
        int target = child < 0 ? node : _tree.Child(node, child);
        span = _tree.Span(target);
        dynamic = false;
        missing = false;
        _name.Clear();
        if (!Append(target, ref dynamic))
        {
            missing = true;
            return null;
        }
        return _name.Length == 0 ? null : _name.ToString();
    }

    bool Append(int node, ref bool dynamic)
    {
        if ((_tree.Flags(node) & NodeFlags.Missing) != 0) return false;
        int kind = _tree.Kind(node);
        if (RuleOf(kind) is { Dynamic: true }) dynamic = true;
        int count = _tree.ChildCount(node);
        if (count == 0)
        {
            if (kind != SyntaxKinds.Empty && kind != SyntaxKinds.List) _name.Append(_tree.GetText(node));
            return true;
        }
        for (int k = 0; k < count; k++)
            if (!Append(_tree.Child(node, k), ref dynamic)) return false;
        return true;
    }

    BindingDiagnostic[] Duplicates()
    {
        var diagnostics = new List<BindingDiagnostic>();
        foreach (var scope in _scopes)
            foreach (var group in scope.Groups)
                for (int i = 1; i < group.Count; i++)
                    diagnostics.Add(new BindingDiagnostic(BindingCodes.Duplicate, group[i].NameSpan,
                        $"duplicate {group[i].Kind} '{group[i].Name}'"));
        return diagnostics.OrderBy(d => d.Span.Start).ToArray();
    }
}
