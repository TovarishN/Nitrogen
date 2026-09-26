namespace Nitrogen.Binding;

/// <summary>
/// Documents bound together (issue 237). A reference resolves in its scope chain, then among the
/// exported declarations of every document, then among the language's built-ins. Replacing a
/// document re-binds only that document; resolutions are cached until the next change. Not
/// thread-safe. The trees must stay alive while they are in the project.
/// </summary>
public sealed class Project
{
    readonly Language _language;
    readonly SortedDictionary<string, FileBinding> _documents = new(StringComparer.Ordinal);
    readonly Dictionary<(string Kind, string Name), List<(Symbol Symbol, int ScopeKind)>> _builtins = new();
    readonly Dictionary<Reference, IReadOnlyList<Symbol>> _resolved = new();
    Dictionary<(string Kind, string Name), List<Symbol>>? _exports;

    public Project(Language language)
    {
        _language = language;
        foreach (var module in language.Modules)
            foreach (var builtin in module.Builtins)
            {
                int scopeKind = builtin.ScopeKind == 0 ? 0 : module.KindBase | builtin.ScopeKind;
                foreach (string name in builtin.Names)
                {
                    if (!_builtins.TryGetValue((builtin.Kind, name), out var entries)) _builtins[(builtin.Kind, name)] = entries = new();
                    if (!entries.Exists(e => e.ScopeKind == scopeKind))
                        entries.Add((new Symbol(builtin.Kind, name, null, default, -1, -1, false), scopeKind));
                }
            }
    }

    /// <summary>Counts changes; a query's answer holds until it moves.</summary>
    public int Version { get; private set; }

    public IReadOnlyCollection<string> Paths => _documents.Keys;

    public FileBinding this[string path] => _documents[path];

    /// <summary>Binds <paramref name="tree"/> as <paramref name="path"/>, replacing an earlier version.</summary>
    public FileBinding Set(string path, SyntaxTree tree)
    {
        if (!ReferenceEquals(tree.Language, _language))
            throw new ArgumentException("The tree was parsed with a different language.", nameof(tree));
        var binding = FileBinding.Bind(path, tree);
        _documents[path] = binding;
        Changed();
        return binding;
    }

    public bool Remove(string path)
    {
        if (!_documents.Remove(path)) return false;
        Changed();
        return true;
    }

    void Changed()
    {
        Version++;
        _exports = null;
        _resolved.Clear();
    }

    /// <summary>The symbols <paramref name="reference"/> names: empty when unresolved, several when ambiguous.</summary>
    public IReadOnlyList<Symbol> Resolve(Reference reference)
    {
        var binding = Owner(reference);
        if (_resolved.TryGetValue(reference, out var cached)) return cached;
        var result = reference.Qualifier is null
            ? Lookup(binding, reference.Scope, reference.Kinds, reference.Name)
            : Qualified(binding, reference);
        _resolved[reference] = result;
        return result;
    }

    /// <summary>
    /// Whether a reference counts. An optional reference counts when it resolves; a reference
    /// inside an optional one that resolves is hidden by it (<c>sys.clock</c> is one reference).
    /// </summary>
    public bool IsEffective(Reference reference)
    {
        var binding = Owner(reference);
        for (int g = reference.Guard; g >= 0; g = binding.References[g].Guard)
            if (Resolve(binding.References[g]).Count > 0) return false;
        return !reference.IsOptional || Resolve(reference).Count > 0;
    }

    /// <summary>Duplicates, unresolved and not-visible references, and exports another document also makes, in text order.</summary>
    public IReadOnlyList<BindingDiagnostic> Diagnostics(string path)
    {
        var binding = _documents[path];
        var diagnostics = new List<BindingDiagnostic>(binding.Diagnostics);
        foreach (var reference in binding.References)
        {
            if (reference.IsOptional || !IsEffective(reference) || Resolve(reference).Count > 0) continue;
            if (reference.Qualifier is not null)
            {
                // Under an unknown or ambiguous qualifier the name is unknown, not an error (issue 239).
                if (QualifierOf(binding, reference.Enclosing, reference.Qualifier) is { } owner)
                    diagnostics.Add(new BindingDiagnostic(BindingCodes.Unresolved, reference.NameSpan,
                        $"unresolved {string.Join(" or ", reference.Kinds)} '{reference.Name}' in {owner.Kind} '{owner.Name}'"));
                continue;
            }
            if (IsOpen(binding, reference)) continue;
            string kinds = string.Join(" or ", reference.Kinds);
            bool elsewhere = binding.Declarations.Any(d => d.Name == reference.Name && reference.Kinds.Contains(d.Kind));
            diagnostics.Add(elsewhere
                ? new BindingDiagnostic(BindingCodes.NotVisible, reference.NameSpan, $"{kinds} '{reference.Name}' is declared in this file but not visible here")
                : new BindingDiagnostic(BindingCodes.Unresolved, reference.NameSpan, $"unresolved {kinds} '{reference.Name}'"));
        }
        var exports = Exports();
        foreach (var symbol in binding.Declarations)
        {
            if (!symbol.IsExported) continue;
            var others = exports[(symbol.Kind, symbol.Name)].Where(s => s.Path != symbol.Path).Select(s => s.Path).Distinct().ToList();
            if (others.Count > 0)
                diagnostics.Add(new BindingDiagnostic(BindingCodes.AmbiguousExport, symbol.NameSpan,
                    $"{symbol.Kind} '{symbol.Name}' is also exported by {string.Join(", ", others)}"));
        }
        return diagnostics.OrderBy(d => d.Span.Start).ToList();
    }

    /// <summary>
    /// The name at <paramref name="position"/> and what it names: the innermost effective reference
    /// there with its resolution (empty when unresolved), or the declaration whose name is there.
    /// A name's end counts, so a cursor just after it still finds it. Null when there is no name.
    /// </summary>
    public (TextSpan Span, IReadOnlyList<Symbol> Symbols)? NameAt(string path, int position)
    {
        var binding = _documents[path];
        Reference? best = null;
        foreach (var reference in binding.References)
            if (Contains(reference.NameSpan, position) && IsEffective(reference)
                && (best is null || reference.NameSpan.Length < best.NameSpan.Length))
                best = reference;
        if (best is not null) return (best.NameSpan, Resolve(best));
        foreach (var symbol in binding.Declarations)
            if (Contains(symbol.NameSpan, position)) return (symbol.NameSpan, new[] { symbol });
        return null;
    }

    /// <summary>What the name at <paramref name="position"/> refers to or declares; empty when there is none.</summary>
    public IReadOnlyList<Symbol> DefinitionAt(string path, int position) => NameAt(path, position)?.Symbols ?? Array.Empty<Symbol>();

    /// <summary>Every effective reference in the project that resolves to <paramref name="symbol"/>, by path, then in text order.</summary>
    public IReadOnlyList<Reference> ReferencesTo(Symbol symbol)
    {
        var found = new List<Reference>();
        foreach (var binding in _documents.Values)
            foreach (var reference in binding.References)
                if (IsEffective(reference) && Resolve(reference).Contains(symbol)) found.Add(reference);
        return found;
    }

    /// <summary>
    /// The symbols of <paramref name="kinds"/> visible at <paramref name="position"/>: the innermost
    /// scope outward, then exports, then built-ins. A name hides later ones of the same kind.
    /// </summary>
    public IReadOnlyList<Symbol> VisibleAt(string path, int position, params string[] kinds)
    {
        var binding = _documents[path];
        int scope = 0;
        for (int s = 1; s < binding.Scopes.Length; s++) // pre-order: the last scope containing the position is the innermost
            if (binding.Scopes[s].Span.Start < position && position < binding.Scopes[s].Span.End) scope = s;

        var seen = new HashSet<(string, string)>();
        var visible = new List<Symbol>();
        void Offer(Symbol symbol)
        {
            if (Array.IndexOf(kinds, symbol.Kind) >= 0 && seen.Add((symbol.Kind, symbol.Name))) visible.Add(symbol);
        }

        for (int s = scope; s >= 0; s = binding.Scopes[s].Parent)
            foreach (var symbol in binding.Scopes[s].Symbols) Offer(symbol);
        foreach (var exported in Exports().Values)
            foreach (var symbol in exported) Offer(symbol);
        foreach (var entries in _builtins.Values)
            foreach (var (symbol, scopeKind) in entries)
                if (Sees(binding, scope, scopeKind)) Offer(symbol);
        return visible;
    }

    static bool Contains(TextSpan span, int position) => position >= span.Start && position <= span.End;

    FileBinding Owner(Reference reference) =>
        _documents.TryGetValue(reference.Path, out var binding) && reference.Index < binding.References.Count
            && ReferenceEquals(binding.References[reference.Index], reference)
            ? binding
            : throw new ArgumentException("The reference belongs to a document that is no longer in the project.", nameof(reference));

    IReadOnlyList<Symbol> Lookup(FileBinding binding, int scope, IReadOnlyList<string> kinds, string name)
    {
        for (int s = scope; s >= 0; s = binding.Scopes[s].Parent)
            foreach (string kind in kinds)
                if (binding.Scopes[s].Find(kind, name) is { } local) return local.ToArray();
        var exports = Exports();
        foreach (string kind in kinds)
            if (exports.TryGetValue((kind, name), out var exported)) return exported.ToArray();
        foreach (string kind in kinds)
            if (_builtins.TryGetValue((kind, name), out var entries))
                foreach (var (symbol, scopeKind) in entries)
                    if (Sees(binding, scope, scopeKind)) return new[] { symbol };
        return Array.Empty<Symbol>();
    }

    /// <summary>
    /// A qualified reference's symbols (issue 239): its name among the declarations of the scope that
    /// its qualifier symbol opens; empty when the qualifier is unknown.
    /// </summary>
    IReadOnlyList<Symbol> Qualified(FileBinding binding, Reference reference)
    {
        if (QualifierOf(binding, reference.Enclosing, reference.Qualifier!) is not { } owner) return Array.Empty<Symbol>();
        var home = _documents[owner.Path!];
        foreach (var scope in home.Scopes)
            if (scope.Node == owner.Node)
            {
                foreach (string kind in reference.Kinds)
                    if (scope.Find(kind, reference.Name) is { } found) return found.ToArray();
                break;
            }
        return Array.Empty<Symbol>();
    }

    /// <summary>
    /// The symbol a qualified reference looks into: the single declared resolution of the nearest
    /// enclosing reference that can name the qualifier's kind. Null when there is none, or it is
    /// unresolved, ambiguous, built in, or of another kind.
    /// </summary>
    Symbol? QualifierOf(FileBinding binding, int enclosing, string qualifier)
    {
        for (int e = enclosing; e >= 0; e = binding.References[e].Enclosing)
        {
            var outer = binding.References[e];
            if (!outer.Kinds.Contains(qualifier)) continue;
            var symbols = Resolve(outer);
            return symbols.Count == 1 && symbols[0].Kind == qualifier && !symbols[0].IsBuiltin ? symbols[0] : null;
        }
        return null;
    }

    /// <summary>
    /// What a reference's name could be (issue 239): for a qualified reference, the symbols of its
    /// kinds declared in its qualifier's scope; otherwise <see cref="VisibleAt"/> at its name.
    /// </summary>
    public IReadOnlyList<Symbol> CandidatesFor(Reference reference) =>
        reference.Qualifier is null
            ? VisibleAt(reference.Path, reference.NameSpan.Start, reference.Kinds.ToArray())
            : InScopeOf(QualifierOf(Owner(reference), reference.Enclosing, reference.Qualifier), reference.Kinds);

    /// <summary>What could fill a hole, as for <see cref="CandidatesFor(Reference)"/>.</summary>
    public IReadOnlyList<Symbol> CandidatesFor(Hole hole) =>
        hole.Qualifier is null
            ? VisibleAt(hole.Path, hole.Position, hole.Kinds.ToArray())
            : InScopeOf(QualifierOf(_documents[hole.Path], hole.Enclosing, hole.Qualifier), hole.Kinds);

    IReadOnlyList<Symbol> InScopeOf(Symbol? owner, IReadOnlyList<string> kinds)
    {
        if (owner is null) return Array.Empty<Symbol>();
        foreach (var scope in _documents[owner.Path!].Scopes)
            if (scope.Node == owner.Node) return scope.Symbols.Where(s => kinds.Contains(s.Kind)).ToList();
        return Array.Empty<Symbol>();
    }

    /// <summary>Whether <paramref name="scope"/> sees a built-in scoped to <paramref name="scopeKind"/> (0: everywhere).</summary>
    static bool Sees(FileBinding binding, int scope, int scopeKind)
    {
        if (scopeKind == 0) return true;
        for (int s = scope; s >= 0; s = binding.Scopes[s].Parent)
            if (binding.Scopes[s].NodeKind == scopeKind) return true;
        return false;
    }

    static bool IsOpen(FileBinding binding, Reference reference)
    {
        for (int s = reference.Scope; s >= 0; s = binding.Scopes[s].Parent)
            foreach (string kind in reference.Kinds)
                if (binding.Scopes[s].IsOpen(kind)) return true;
        return false;
    }

    Dictionary<(string Kind, string Name), List<Symbol>> Exports()
    {
        if (_exports is not null) return _exports;
        var exports = new Dictionary<(string Kind, string Name), List<Symbol>>();
        foreach (var binding in _documents.Values)
            foreach (var symbol in binding.Declarations)
            {
                if (!symbol.IsExported) continue;
                if (!exports.TryGetValue((symbol.Kind, symbol.Name), out var list)) exports[(symbol.Kind, symbol.Name)] = list = new List<Symbol>();
                list.Add(symbol);
            }
        return _exports = exports;
    }
}
