using System.Runtime.CompilerServices;
using System.Text;

namespace Nitrogen.Grammar;

/// <summary>How a committed element's failure is known to be a syntax error (issue 235, spec §4).</summary>
internal enum CommitKind
{
    /// <summary>Not committed: a failure here may be backtracked into a successful parse.</summary>
    None = 0,

    /// <summary>
    /// Committed provided no other alternative of an extension point on the way, possibly from a
    /// module composed later, can start the same way. <c>LanguageBuilder</c> decides at composition.
    /// </summary>
    Composed = 1,

    /// <summary>Committed by this grammar alone.</summary>
    Static = 2,
}

internal enum TerminalKind
{
    Literal,
    Token,
    Any,
    End,
}

/// <summary>One lookahead terminal: a literal, a token rule (by key), anything, or end of input.</summary>
internal readonly record struct Terminal(TerminalKind Kind, string Value) : IComparable<Terminal>
{
    public static readonly Terminal Any = new(TerminalKind.Any, "");

    public static readonly Terminal End = new(TerminalKind.End, "");

    public static Terminal Literal(string text) => new(TerminalKind.Literal, text);

    public static Terminal Token(string key) => new(TerminalKind.Token, key);

    public int CompareTo(Terminal other)
    {
        int byKind = Kind.CompareTo(other.Kind);
        return byKind != 0 ? byKind : string.CompareOrdinal(Value, other.Value);
    }

    public override string ToString() => Kind switch
    {
        TerminalKind.Literal => "\"" + Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        TerminalKind.Token => Value.Substring(Value.LastIndexOf('.') + 1),
        TerminalKind.Any => "*",
        _ => "$",
    };
}

/// <summary>An immutable, sorted set of terminals: the FIRST and FOLLOW sets of the analysis.</summary>
internal sealed class Lookahead
{
    public static readonly Lookahead Empty = new(new Terminal[0]);

    readonly Terminal[] _items;
    string? _text;

    Lookahead(Terminal[] items)
    {
        _items = items;
    }

    public static Lookahead Of(params Terminal[] items) =>
        items.Length == 0 ? Empty : new Lookahead(items.Distinct().OrderBy(t => t).ToArray());

    public IReadOnlyList<Terminal> Items => _items;

    public bool IsEmpty => _items.Length == 0;

    public Lookahead Union(Lookahead other) =>
        other.IsEmpty ? this : IsEmpty ? other : Of(_items.Concat(other._items).ToArray());

    public override string ToString() => _text ??= "{" + string.Join(", ", _items) + "}";
}

/// <summary>A place a skip may stop: element <see cref="Index"/> of the site's sequence can start with <see cref="First"/>.</summary>
internal readonly record struct SyncTarget(int Index, Lookahead First)
{
    public override string ToString() => First + " @" + Index;
}

/// <summary>One committed element and what recovery needs to repair a failure there.</summary>
internal sealed class RecoverySite
{
    public RecoverySite(string owner, string path, SequenceExpr sequence, int index, CommitKind kind,
        Lookahead insert, IReadOnlyList<SyncTarget> sync, bool inAlternative)
    {
        Owner = owner;
        Path = path;
        Sequence = sequence;
        Index = index;
        Kind = kind;
        Insert = insert;
        Sync = sync;
        InAlternative = inAlternative;
    }

    /// <summary>The sequence belongs to an extension alternative: its kind comes from local analysis (Plan 2b).</summary>
    public bool InAlternative { get; }

    /// <summary>The syntax rule (<c>Getup</c>) or extension alternative (<c>Expr.Paren</c>) the sequence belongs to.</summary>
    public string Owner { get; }

    /// <summary>Where the sequence sits in its owner: <c>""</c> for the body, <c>/Label</c> per label, <c>#n</c> per choice alternative.</summary>
    public string Path { get; }

    public SequenceExpr Sequence { get; }

    public int Index { get; }

    public Expr Element => Sequence.Items[Index];

    public CommitKind Kind { get; }

    /// <summary>What may follow the element: input already starting with one of these means the element alone is missing.</summary>
    public Lookahead Insert { get; }

    /// <summary>Where a skip may stop: the element, every later element, and a list just before the element.</summary>
    public IReadOnlyList<SyncTarget> Sync { get; }

    /// <summary><c>Owner/Path:Index</c>, with <c>~</c> for a composed commit.</summary>
    public string Name => Owner + Path + ":" + Index + (Kind == CommitKind.Composed ? "~" : "");
}

/// <summary>
/// Commit points (issue 235, spec §4): the sequence elements whose failure, once the elements before
/// them have matched, cannot be backtracked into a successful parse. Only the recovery pass reads
/// them, so an imprecise answer can only worsen a repair, never change a valid parse. The rules are
/// written out in docs/superpowers/plans/2026-09-23-nitrogen-recovery-1-analysis.md.
/// </summary>
internal sealed class CommitAnalysis
{
    sealed class Root
    {
        public Root(ModuleDecl module, string? implicitModule, string owner, Expr body, string? ruleKey,
            string? pointKey, PointAlternative? alternative, bool isPostfix)
        {
            Module = module;
            ImplicitModule = implicitModule;
            Owner = owner;
            Body = body;
            RuleKey = ruleKey;
            PointKey = pointKey;
            Alternative = alternative;
            IsPostfix = isPostfix;
        }

        public ModuleDecl Module { get; }

        public string? ImplicitModule { get; }

        public string Owner { get; }

        public Expr Body { get; }

        /// <summary>The syntax rule's key; null for an extension alternative.</summary>
        public string? RuleKey { get; }

        /// <summary>The extension point's key; null for a syntax rule.</summary>
        public string? PointKey { get; }

        public PointAlternative? Alternative { get; }

        public bool IsPostfix { get; }
    }

    sealed class Occurrence
    {
        public Occurrence(Expr? parent, int index, Root root, string path)
        {
            Parent = parent;
            Index = index;
            Root = root;
            Path = path;
        }

        public Expr? Parent { get; }

        /// <summary>Position among the parent's children (sequence item, choice alternative, list item 0 / separator 1).</summary>
        public int Index { get; }

        public Root Root { get; }

        public string Path { get; }
    }

    /// <summary>Grammar expressions are value-equal records; occurrences are told apart by identity.</summary>
    sealed class Identity : IEqualityComparer<Expr>
    {
        public static readonly Identity Instance = new();

        public bool Equals(Expr? x, Expr? y) => ReferenceEquals(x, y);

        public int GetHashCode(Expr obj) => RuntimeHelpers.GetHashCode(obj);
    }

    readonly struct QueryKey : IEquatable<QueryKey>
    {
        readonly Expr _expr;
        readonly string _lookahead;
        readonly bool _after;
        readonly PointAlternative? _ignore;
        readonly bool _local;

        public QueryKey(Expr expr, Lookahead lookahead, bool after, PointAlternative? ignore, bool local)
        {
            _expr = expr;
            _lookahead = lookahead.ToString();
            _after = after;
            _ignore = ignore;
            _local = local;
        }

        public bool Equals(QueryKey other) =>
            ReferenceEquals(_expr, other._expr) && _lookahead == other._lookahead && _after == other._after
            && ReferenceEquals(_ignore, other._ignore) && _local == other._local;

        public override bool Equals(object? obj) => obj is QueryKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = RuntimeHelpers.GetHashCode(_expr);
                hash = hash * 31 + _lookahead.GetHashCode();
                hash = hash * 31 + (_after ? 1 : 0);
                return (hash * 31 + (_ignore is null ? 0 : RuntimeHelpers.GetHashCode(_ignore))) * 2 + (_local ? 1 : 0);
            }
        }
    }

    readonly GrammarAnalysis _analysis;
    readonly Dictionary<Expr, Occurrence> _occurrences = new(Identity.Instance);
    readonly List<SequenceExpr> _sequences = new();
    readonly Dictionary<string, Root> _ruleRoots = new(StringComparer.Ordinal);
    readonly Dictionary<string, (TokenRule Rule, ModuleDecl Module)> _tokens = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<ReferenceExpr>> _calls = new(StringComparer.Ordinal);
    readonly Dictionary<Expr, Lookahead> _first = new(Identity.Instance);
    readonly Dictionary<Expr, Lookahead> _concreteFirst = new(Identity.Instance);
    readonly Dictionary<string, Lookahead> _pointFirst = new(StringComparer.Ordinal);
    readonly HashSet<string> _pointFirstVisiting = new(StringComparer.Ordinal);
    readonly Dictionary<string, Lookahead> _ruleFirst = new(StringComparer.Ordinal);
    readonly HashSet<string> _ruleFirstVisiting = new(StringComparer.Ordinal);
    readonly Dictionary<string, CharSet> _tokenFirst = new(StringComparer.Ordinal);
    readonly Dictionary<Expr, Lookahead> _follow = new(Identity.Instance);
    readonly HashSet<Expr> _followVisiting = new(Identity.Instance);
    readonly Dictionary<QueryKey, CommitKind> _memo = new();
    readonly Dictionary<QueryKey, int> _active = new();
    readonly Dictionary<QueryKey, (CommitKind Kind, int DependsOn)> _provisional = new();
    readonly List<List<QueryKey>> _dependents = new(); // by depth: provisional results leaning on that query
    readonly List<RecoverySite> _sites = new();
    readonly Dictionary<Expr, RecoverySite?[]> _siteTable = new(Identity.Instance);
    int _depth;
    int _lowestAssumption = int.MaxValue;
    bool _local;
    readonly Dictionary<string, Lookahead> _failUnion = new(StringComparer.Ordinal);
    readonly Dictionary<string, Lookahead> _afterUnion = new(StringComparer.Ordinal);
    readonly Dictionary<Expr, int> _callFlags = new(Identity.Instance);

    public CommitAnalysis(GrammarAnalysis analysis)
    {
        _analysis = analysis;
        foreach (var module in analysis.PrimaryModules)
        {
            foreach (var rule in analysis.PrimaryRules(module))
            {
                string key = module.Name + "." + rule.Name;
                if (rule is TokenRule token)
                {
                    _tokens[key] = (token, module);
                }
                else if (rule is SyntaxRule syntax)
                {
                    var root = new Root(module, null, syntax.Name, syntax.Body, key, null, null, false);
                    _ruleRoots[key] = root;
                    Index(syntax.Body, null, 0, root, "", leftOperand: false);
                }
            }
        }
        foreach (string pointKey in analysis.PointKeys)
        {
            string pointName = pointKey.Substring(pointKey.LastIndexOf('.') + 1);
            foreach (var alternative in analysis.Point(pointKey))
            {
                var body = alternative.Alternative.Body;
                var root = new Root(alternative.Module, alternative.ImplicitModule,
                    pointName + "." + alternative.Alternative.Name, body, null, pointKey, alternative,
                    analysis.IsPostfix(alternative, pointKey));
                Index(body, null, 0, root, "", leftOperand: false);
            }
        }

        foreach (var sequence in _sequences)
        {
            for (int i = 0; i < sequence.Items.Count; i++)
            {
                if (Nullable(sequence.Items[i])) continue;
                var occurrence = _occurrences[sequence];
                bool inAlternative = occurrence.Root.PointKey is not null;
                // An alternative's sites are analysed as if its own failure were fatal; the call
                // site's bits and the composition check confirm it at run time (Plan 2b).
                _local = inAlternative;
                var kind = Commit(sequence, i);
                _local = false;
                if (kind == CommitKind.None) continue;
                _sites.Add(new RecoverySite(occurrence.Root.Owner, occurrence.Path, sequence, i, kind,
                    Insert(sequence, i), Sync(sequence, i), inAlternative));
            }
        }
        foreach (var site in _sites)
        {
            if (!_siteTable.TryGetValue(site.Sequence, out var row))
                _siteTable[site.Sequence] = row = new RecoverySite?[site.Sequence.Items.Count];
            row[site.Index] = site;
        }
    }

    /// <summary>Every commit point, in module, rule and source order.</summary>
    public IReadOnlyList<RecoverySite> Sites => _sites;

    /// <summary>The commit point at element <paramref name="index"/> of <paramref name="sequence"/>, if any.</summary>
    public RecoverySite? Site(SequenceExpr sequence, int index) =>
        _siteTable.TryGetValue(sequence, out var row) ? row[index] : null;

    /// <summary>The token rule behind a <see cref="TerminalKind.Token"/> terminal.</summary>
    public (TokenRule Rule, ModuleDecl Module) Token(string key) => _tokens[key];

    /// <summary>
    /// The recovery bits of a call of an extension point (Plan 2b): 1 when the point failing here
    /// fails the parse, 2 when the point stopping here before a postfix operator does. Tested with
    /// the union of every lookahead the point's alternatives commit under, so one bit serves all.
    /// </summary>
    public int CallFlags(ReferenceExpr call)
    {
        if (_callFlags.TryGetValue(call, out int known)) return known;
        int flags = 0;
        if (_occurrences.TryGetValue(call, out var occurrence) && Resolve(call, occurrence.Root) is { Rule: ExtensibleRule } point)
        {
            if (_failUnion.TryGetValue(point.Key, out var fail) && Query(call, fail, after: false, null) == CommitKind.Static)
                flags |= 1;
            if (_afterUnion.TryGetValue(point.Key, out var after) && Query(call, after, after: true, null) == CommitKind.Static)
                flags |= 2;
        }
        _callFlags[call] = flags;
        return flags;
    }

    /// <summary>Every call of an extension point that carries recovery bits, named like a site.</summary>
    public IReadOnlyList<(string Site, int Flags)> Calls
    {
        get
        {
            var calls = new List<(string Site, int Flags)>();
            foreach (string point in _analysis.PointKeys)
                if (_calls.TryGetValue(point, out var references))
                    foreach (var reference in references)
                        if (CallFlags(reference) is var flags and not 0) calls.Add((Locate(reference), flags));
            return calls;
        }
    }

    /// <summary><c>Owner/Path:Index</c> of the sequence element that contains <paramref name="expr"/>.</summary>
    string Locate(Expr expr)
    {
        var occurrence = _occurrences[expr];
        while (occurrence.Parent is { } parent and not SequenceExpr)
        {
            expr = parent;
            occurrence = _occurrences[expr];
        }
        return occurrence.Parent is SequenceExpr sequence
            ? occurrence.Root.Owner + _occurrences[sequence].Path + ":" + occurrence.Index
            : occurrence.Root.Owner + occurrence.Path;
    }

    public bool Overlaps(Lookahead a, Lookahead b)
    {
        foreach (var x in a.Items)
            foreach (var y in b.Items)
                if (Overlaps(x, y)) return true;
        return false;
    }

    /// <summary>A reviewable listing of every commit point, one block per sequence.</summary>
    public string Dump()
    {
        var text = new StringBuilder();
        int composed = _sites.Count(s => s.Kind == CommitKind.Composed);
        int sequences = _sites.Select(s => (Expr)s.Sequence).Distinct(Identity.Instance).Count();
        text.Append("// commit points: ").Append(_sites.Count).Append(" committed elements (").Append(composed)
            .Append(" composed) in ").Append(sequences).Append(" sequences\n");
        SequenceExpr? current = null;
        foreach (var site in _sites)
        {
            if (!ReferenceEquals(site.Sequence, current))
            {
                current = site.Sequence;
                text.Append(site.Owner).Append(site.Path).Append(": ").Append(Render(site.Sequence, nested: false)).Append('\n');
            }
            text.Append("  ").Append(site.Index).Append(' ').Append(Render(site.Element, nested: true))
                .Append("  ").Append(site.Kind == CommitKind.Static ? "static" : "composed")
                .Append("  insert ").Append(site.Insert)
                .Append("  sync ").Append(string.Join(", ", site.Sync)).Append('\n');
        }
        var calls = Calls;
        text.Append("// calls with recovery bits: ").Append(calls.Count).Append('\n');
        foreach (var (site, flags) in calls)
            text.Append("call ").Append(site)
                .Append((flags & 1) != 0 ? "  fail" : "")
                .Append((flags & 2) != 0 ? "  after" : "")
                .Append('\n');
        return text.ToString();
    }

    static string Render(Expr expr, bool nested)
    {
        switch (expr)
        {
            case SequenceExpr sequence:
            {
                string text = string.Join(" ", sequence.Items.Select(i => Render(i, nested: true)));
                return nested ? "(" + text + ")" : text;
            }
            case ChoiceExpr choice:
            {
                string text = string.Join(" / ", choice.Alternatives.Select(a => Render(a, nested: true)));
                return nested ? "(" + text + ")" : text;
            }
            case LabeledExpr labeled:
                return labeled.Label + ":" + Render(labeled.Inner, nested: true);
            case RepeatExpr repeat:
                return Render(repeat.Inner, nested: true)
                    + (repeat.Kind == RepeatKind.Optional ? "?" : repeat.Kind == RepeatKind.ZeroOrMore ? "*" : "+");
            case SeparatedListExpr list:
                return "(" + Render(list.Item, nested: false) + "; " + Render(list.Separator, nested: false) + ")"
                    + (list.AtLeastOne ? "+" : "*");
            case LiteralExpr literal:
                return Terminal.Literal(literal.Value).ToString();
            case ReferenceExpr reference:
                return reference.Name;
            case PredicateExpr predicate:
                return (predicate.Kind == PredicateKind.Not ? "!" : "&") + Render(predicate.Inner, nested: true);
            case CharClassExpr:
                return "[...]";
            default:
                return "any";
        }
    }

    // ---- indexing ----

    void Index(Expr expr, Expr? parent, int index, Root root, string path, bool leftOperand)
    {
        _occurrences[expr] = new Occurrence(parent, index, root, path);
        switch (expr)
        {
            case SequenceExpr sequence:
                _sequences.Add(sequence);
                for (int i = 0; i < sequence.Items.Count; i++)
                    Index(sequence.Items[i], sequence, i, root, path,
                        leftOperand || (i == 0 && root.IsPostfix && ReferenceEquals(sequence, root.Body)));
                break;
            case ChoiceExpr choice:
                for (int k = 0; k < choice.Alternatives.Count; k++)
                    Index(choice.Alternatives[k], choice, k, root, path + "#" + (k + 1), leftOperand);
                break;
            case LabeledExpr labeled:
                Index(labeled.Inner, labeled, 0, root, path + "/" + labeled.Label, leftOperand);
                break;
            case RepeatExpr repeat:
                Index(repeat.Inner, repeat, 0, root, path, leftOperand);
                break;
            case SeparatedListExpr list:
                Index(list.Item, list, 0, root, path, leftOperand);
                Index(list.Separator, list, 1, root, path, leftOperand);
                break;
            case PredicateExpr predicate:
                Index(predicate.Inner, predicate, 0, root, path, leftOperand);
                break;
            case ReferenceExpr reference when !leftOperand:
                if (Resolve(reference, root) is { Rule: SyntaxRule or ExtensibleRule } symbol)
                {
                    if (!_calls.TryGetValue(symbol.Key, out var calls)) _calls[symbol.Key] = calls = new List<ReferenceExpr>();
                    calls.Add(reference);
                }
                break;
        }
    }

    GrammarSymbol? Resolve(ReferenceExpr reference, Root root) =>
        _analysis.Resolve(reference.Name, reference.Span, root.Module, root.ImplicitModule);

    // ---- FIRST ----

    bool Nullable(Expr expr)
    {
        var root = _occurrences[expr].Root;
        return _analysis.IsNullable(expr, root.Module, root.ImplicitModule);
    }

    bool NullableItems(IReadOnlyList<Expr> items, int from, int to)
    {
        for (int k = from; k < to; k++)
            if (!Nullable(items[k])) return false;
        return true;
    }

    Lookahead FirstOfItems(IReadOnlyList<Expr> items, int from, int to, bool concrete = false)
    {
        var result = Lookahead.Empty;
        for (int k = from; k < to; k++)
        {
            result = result.Union(concrete ? ConcreteFirst(items[k]) : First(items[k]));
            if (!Nullable(items[k])) break;
        }
        return result;
    }

    Lookahead First(Expr expr)
    {
        if (_first.TryGetValue(expr, out var known)) return known;
        var root = _occurrences[expr].Root;
        Lookahead result;
        switch (expr)
        {
            case LiteralExpr literal:
                result = Lookahead.Of(Terminal.Literal(literal.Value));
                break;
            case ReferenceExpr reference:
                var symbol = Resolve(reference, root);
                result = symbol?.Rule switch
                {
                    TokenRule => Lookahead.Of(Terminal.Token(symbol.Key)),
                    SyntaxRule => RuleFirst(symbol.Key),
                    ExtensibleRule => Lookahead.Of(Terminal.Any), // modules composed later may add alternatives
                    _ => Lookahead.Empty,
                };
                break;
            case LabeledExpr labeled:
                result = First(labeled.Inner);
                break;
            case RepeatExpr repeat:
                result = First(repeat.Inner);
                break;
            case SeparatedListExpr list:
                result = Nullable(list.Item) ? First(list.Item).Union(First(list.Separator)) : First(list.Item);
                break;
            case ChoiceExpr choice:
                result = Lookahead.Empty;
                foreach (var alternative in choice.Alternatives) result = result.Union(First(alternative));
                break;
            case SequenceExpr sequence:
                result = FirstOfItems(sequence.Items, 0, sequence.Items.Count);
                break;
            case PredicateExpr:
                result = Lookahead.Empty;
                break;
            default: // a character class written in a syntax rule
                result = Lookahead.Of(Terminal.Any);
                break;
        }
        _first[expr] = result;
        return result;
    }

    Lookahead RuleFirst(string key)
    {
        if (_ruleFirst.TryGetValue(key, out var known)) return known;
        if (!_ruleFirstVisiting.Add(key)) return Lookahead.Empty; // recursion through a nullable prefix adds nothing
        var result = First(_ruleRoots[key].Body);
        _ruleFirstVisiting.Remove(key);
        _ruleFirst[key] = result;
        return result;
    }

    /// <summary>
    /// FIRST for recovery data only (Plan 3b): an extension point contributes its known prefix
    /// alternatives' FIRST instead of <i>any</i>, so a skip can stop at an expression and a missing
    /// operand can be recognized. The commit queries keep <see cref="First"/>: soundness does not
    /// depend on this set.
    /// </summary>
    Lookahead ConcreteFirst(Expr expr)
    {
        if (_concreteFirst.TryGetValue(expr, out var known)) return known;
        var root = _occurrences[expr].Root;
        Lookahead result;
        switch (expr)
        {
            case ReferenceExpr reference when Resolve(reference, root) is { Rule: ExtensibleRule } point:
                result = PointFirst(point.Key);
                break;
            case ReferenceExpr reference when Resolve(reference, root) is { Rule: SyntaxRule } rule:
                result = ConcreteFirst(_ruleRoots[rule.Key].Body);
                break;
            case LabeledExpr labeled:
                result = ConcreteFirst(labeled.Inner);
                break;
            case RepeatExpr repeat:
                result = ConcreteFirst(repeat.Inner);
                break;
            case SeparatedListExpr list:
                result = Nullable(list.Item) ? ConcreteFirst(list.Item).Union(ConcreteFirst(list.Separator)) : ConcreteFirst(list.Item);
                break;
            case ChoiceExpr choice:
                result = Lookahead.Empty;
                foreach (var alternative in choice.Alternatives) result = result.Union(ConcreteFirst(alternative));
                break;
            case SequenceExpr sequence:
                result = FirstOfItems(sequence.Items, 0, sequence.Items.Count, concrete: true);
                break;
            default:
                result = First(expr);
                break;
        }
        _concreteFirst[expr] = result;
        return result;
    }

    /// <summary>The union of an extension point's known prefix alternatives' concrete FIRST; a cycle adds nothing.</summary>
    Lookahead PointFirst(string pointKey)
    {
        if (_pointFirst.TryGetValue(pointKey, out var known)) return known;
        if (!_pointFirstVisiting.Add(pointKey)) return Lookahead.Empty;
        var result = Lookahead.Empty;
        foreach (var alternative in _analysis.Point(pointKey))
        {
            if (_analysis.IsPostfix(alternative, pointKey)) continue;
            var body = alternative.Alternative.Body;
            result = result.Union(body is SequenceExpr sequence
                ? FirstOfItems(sequence.Items, 0, sequence.Items.Count, concrete: true)
                : ConcreteFirst(body));
        }
        _pointFirstVisiting.Remove(pointKey);
        _pointFirst[pointKey] = result;
        return result;
    }

    CharSet TokenFirst(string key)
    {
        if (_tokenFirst.TryGetValue(key, out var known)) return known;
        var set = new CharSet();
        _tokenFirst[key] = set; // a self-reference adds nothing further
        var (rule, module) = _tokens[key];
        AddTokenFirst(rule.Body, module, set);
        return set;
    }

    void AddTokenFirst(Expr expr, ModuleDecl module, CharSet set)
    {
        switch (expr)
        {
            case LiteralExpr literal:
                set.Add(literal.Value[0]);
                break;
            case CharClassExpr charClass:
                if (charClass.Negated) set.AddAny();
                else foreach (var range in charClass.Ranges) set.AddRange(range.First, range.Last);
                break;
            case AnyCharExpr:
                set.AddAny();
                break;
            case LabeledExpr labeled:
                AddTokenFirst(labeled.Inner, module, set);
                break;
            case RepeatExpr repeat:
                AddTokenFirst(repeat.Inner, module, set);
                break;
            case SeparatedListExpr list:
                AddTokenFirst(list.Item, module, set);
                if (_analysis.IsNullable(list.Item, module, null)) AddTokenFirst(list.Separator, module, set);
                break;
            case ChoiceExpr choice:
                foreach (var alternative in choice.Alternatives) AddTokenFirst(alternative, module, set);
                break;
            case SequenceExpr sequence:
                foreach (var item in sequence.Items)
                {
                    AddTokenFirst(item, module, set);
                    if (!_analysis.IsNullable(item, module, null)) break;
                }
                break;
            case ReferenceExpr reference:
                if (_analysis.Resolve(reference.Name, reference.Span, module, null) is { Rule: TokenRule } token)
                    set.Union(TokenFirst(token.Key));
                break;
        }
    }

    bool Overlaps(Terminal x, Terminal y)
    {
        if (x.Kind == TerminalKind.Any || y.Kind == TerminalKind.Any) return true;
        if (x.Kind == TerminalKind.End || y.Kind == TerminalKind.End) return x.Kind == y.Kind;
        if (x.Kind == TerminalKind.Literal && y.Kind == TerminalKind.Literal)
        {
            if (x.Value == y.Value) return true;
            if (IsKeywordLike(x.Value) && IsKeywordLike(y.Value)) return false;
            return x.Value.StartsWith(y.Value, StringComparison.Ordinal) || y.Value.StartsWith(x.Value, StringComparison.Ordinal);
        }
        if (x.Kind == TerminalKind.Token && y.Kind == TerminalKind.Token)
            return x.Value == y.Value || TokenFirst(x.Value).Overlaps(TokenFirst(y.Value));
        var literal = x.Kind == TerminalKind.Literal ? x : y;
        var token = x.Kind == TerminalKind.Token ? x : y;
        if (IsKeywordLike(literal.Value) && _tokens[token.Value].Rule.Except.Any(e => e.Value == literal.Value))
            return false; // a reserved word: the token rejects exactly the word the keyword accepts
        return TokenFirst(token.Value).Contains(literal.Value[0]);
    }

    // ---- commit queries ----

    /// <summary>An identifier-shaped literal: matched as a keyword, with a word boundary (as SyntaxCodeWriter emits it).</summary>
    static bool IsKeywordLike(string literal) =>
        GrammarLexer.IsIdentifierStart(literal[0]) && literal.All(GrammarLexer.IsIdentifierPart);

    static CommitKind Min(CommitKind a, CommitKind b) => a < b ? a : b;

    CommitKind AllCalls(string key, Func<ReferenceExpr, CommitKind> query)
    {
        if (!_calls.TryGetValue(key, out var calls)) return CommitKind.Static; // an entry rule
        var result = CommitKind.Static;
        foreach (var call in calls)
        {
            result = Min(result, query(call));
            if (result == CommitKind.None) break;
        }
        return result;
    }

    CommitKind Commit(SequenceExpr sequence, int index, PointAlternative? ignore = null)
    {
        var root = _occurrences[sequence].Root;
        if (root.IsPostfix && ReferenceEquals(sequence, root.Body))
        {
            // The left operand is parsed already; the operator part pins the alternative.
            if (index < 2 || NullableItems(sequence.Items, 1, index)) return CommitKind.None;
            return Query(sequence, FirstOfItems(sequence.Items, 1, index), after: false, root.Alternative);
        }
        if (index < 1 || NullableItems(sequence.Items, 0, index)) return CommitKind.None;
        return Query(sequence, FirstOfItems(sequence.Items, 0, index), after: false, ignore);
    }

    CommitKind Query(Expr expr, Lookahead lookahead, bool after, PointAlternative? ignore)
    {
        var key = new QueryKey(expr, lookahead, after, ignore, _local);
        if (_memo.TryGetValue(key, out var known)) return known;
        if (_provisional.TryGetValue(key, out var pending))
        {
            _lowestAssumption = Math.Min(_lowestAssumption, pending.DependsOn);
            return pending.Kind;
        }
        if (_active.TryGetValue(key, out int active))
        {
            // A cycle: assume fatal (greatest fixed point); every exit of the cycle must still be.
            _lowestAssumption = Math.Min(_lowestAssumption, active);
            return CommitKind.Static;
        }

        int saved = _lowestAssumption;
        _lowestAssumption = int.MaxValue;
        int depth = ++_depth;
        _active[key] = depth;
        while (_dependents.Count <= depth) _dependents.Add(new List<QueryKey>());
        var result = after ? After(expr, lookahead, ignore) : Fails(expr, lookahead, ignore);
        _active.Remove(key);
        _depth--;

        // Results that leaned on this query's assumption (Static) stand if the answer is Static,
        // and are dropped otherwise. Leaning on a caller makes this result provisional in turn.
        int lowest = _lowestAssumption;
        var dependents = _dependents[depth];
        bool provisional = lowest < depth;
        foreach (var dependent in dependents)
        {
            if (result != CommitKind.Static)
            {
                _provisional.Remove(dependent);
            }
            else if (provisional)
            {
                _provisional[dependent] = (_provisional[dependent].Kind, lowest);
                _dependents[lowest].Add(dependent);
            }
            else
            {
                _memo[dependent] = _provisional[dependent].Kind;
                _provisional.Remove(dependent);
            }
        }
        dependents.Clear();
        if (provisional)
        {
            _provisional[key] = (result, lowest);
            _dependents[lowest].Add(key);
        }
        else
        {
            _memo[key] = result;
        }
        _lowestAssumption = Math.Min(saved, provisional ? lowest : int.MaxValue);
        return result;
    }

    /// <summary><paramref name="expr"/> failed where the input starts with <paramref name="lookahead"/>: does the parse fail?</summary>
    CommitKind Fails(Expr expr, Lookahead lookahead, PointAlternative? ignore)
    {
        var occurrence = _occurrences[expr];
        switch (occurrence.Parent)
        {
            case null:
                return RootFails(occurrence.Root, lookahead, ignore);
            case SequenceExpr sequence:
            {
                int index = occurrence.Index;
                var root = occurrence.Root;
                if (root.IsPostfix && ReferenceEquals(sequence, root.Body)) return Commit(sequence, index);
                if (!NullableItems(sequence.Items, 0, index)) return Commit(sequence, index, ignore);
                return Query(sequence, lookahead.Union(FirstOfItems(sequence.Items, 0, index)), after: false, ignore);
            }
            case ChoiceExpr choice:
                for (int k = occurrence.Index + 1; k < choice.Alternatives.Count; k++)
                {
                    var later = choice.Alternatives[k];
                    if (Nullable(later) || Overlaps(First(later), lookahead)) return CommitKind.None;
                }
                return Query(choice, lookahead, after: false, ignore);
            case LabeledExpr labeled:
                return Query(labeled, lookahead, after: false, ignore);
            case RepeatExpr repeat:
            {
                var ends = Query(repeat, lookahead, after: true, ignore);
                return repeat.Kind == RepeatKind.OneOrMore ? Min(ends, Query(repeat, lookahead, after: false, ignore)) : ends;
            }
            case SeparatedListExpr list:
            {
                if (occurrence.Index == 1) return Query(list, lookahead, after: true, ignore);
                var first = list.AtLeastOne
                    ? Query(list, lookahead, after: false, ignore)
                    : Query(list, lookahead, after: true, ignore);
                // A later item's failure backtracks over its separator: the list ends before it.
                return Min(first, Query(list, First(list.Separator), after: true, ignore));
            }
            default:
                return CommitKind.None;
        }
    }

    /// <summary><paramref name="expr"/> matched and the input continues with <paramref name="lookahead"/>: does the parse fail?</summary>
    CommitKind After(Expr expr, Lookahead lookahead, PointAlternative? ignore)
    {
        var occurrence = _occurrences[expr];
        switch (occurrence.Parent)
        {
            case null:
                return RootAfter(occurrence.Root, lookahead, ignore);
            case SequenceExpr sequence:
                for (int k = occurrence.Index + 1; k < sequence.Items.Count; k++)
                {
                    var next = sequence.Items[k];
                    if (Overlaps(First(next), lookahead)) return CommitKind.None;
                    if (!Nullable(next)) return Query(next, lookahead, after: false, ignore);
                }
                return Query(sequence, lookahead, after: true, ignore);
            case ChoiceExpr choice:
                return Query(choice, lookahead, after: true, ignore);
            case LabeledExpr labeled:
                return Query(labeled, lookahead, after: true, ignore);
            case RepeatExpr repeat:
                if (repeat.Kind != RepeatKind.Optional && (Nullable(expr) || Overlaps(First(expr), lookahead)))
                    return CommitKind.None; // another iteration may start here
                return Query(repeat, lookahead, after: true, ignore);
            case SeparatedListExpr list:
                if (occurrence.Index == 1 || Overlaps(First(list.Separator), lookahead)) return CommitKind.None;
                return Query(list, lookahead, after: true, ignore);
            default:
                return CommitKind.None;
        }
    }

    CommitKind RootFails(Root root, Lookahead lookahead, PointAlternative? ignore)
    {
        if (root.RuleKey is { } rule) return AllCalls(rule, call => Query(call, lookahead, after: false, ignore));
        if (_local)
        {
            Record(root.IsPostfix ? _afterUnion : _failUnion, root.PointKey!, lookahead);
            return CommitKind.Composed;
        }
        if (root.IsPostfix) // the Pratt loop stops; the point completes before the operator
            return Min(CommitKind.Composed, AllCalls(root.PointKey!, call => Query(call, lookahead, after: true, ignore)));
        return Min(CommitKind.Composed, AllCalls(root.PointKey!, call => Query(call, lookahead, after: false, ignore)));
    }

    CommitKind RootAfter(Root root, Lookahead lookahead, PointAlternative? ignore)
    {
        if (root.RuleKey is { } rule) return AllCalls(rule, call => Query(call, lookahead, after: true, ignore));
        // The alternative completed: the Pratt loop may go on with another postfix alternative.
        if (Overlaps(PostfixOperators(root.PointKey!, ignore), lookahead)) return CommitKind.None;
        if (_local)
        {
            Record(_afterUnion, root.PointKey!, lookahead);
            return CommitKind.Composed;
        }
        return Min(CommitKind.Composed, AllCalls(root.PointKey!, call => Query(call, lookahead, after: true, ignore)));
    }

    static void Record(Dictionary<string, Lookahead> unions, string point, Lookahead lookahead) =>
        unions[point] = unions.TryGetValue(point, out var known) ? known.Union(lookahead) : lookahead;

    Lookahead PostfixOperators(string pointKey, PointAlternative? ignore)
    {
        var result = Lookahead.Empty;
        foreach (var alternative in _analysis.Point(pointKey))
            if (!ReferenceEquals(alternative, ignore) && alternative.Alternative.Body is SequenceExpr body
                && _analysis.IsPostfix(alternative, pointKey))
                result = result.Union(FirstOfItems(body.Items, 1, body.Items.Count));
        return result;
    }

    // ---- repair data ----

    Lookahead Insert(SequenceExpr sequence, int index)
    {
        int count = sequence.Items.Count;
        var rest = FirstOfItems(sequence.Items, index + 1, count, concrete: true);
        return NullableItems(sequence.Items, index + 1, count) ? rest.Union(Follow(sequence)) : rest;
    }

    IReadOnlyList<SyncTarget> Sync(SequenceExpr sequence, int index)
    {
        var targets = new List<SyncTarget>();
        for (int k = index; k < sequence.Items.Count; k++)
        {
            var first = ConcreteFirst(sequence.Items[k]);
            if (!first.IsEmpty) targets.Add(new SyncTarget(k, first));
        }
        if (index > 0)
        {
            var before = sequence.Items[index - 1];
            if (before is LabeledExpr labeled) before = labeled.Inner;
            if (before is RepeatExpr { Kind: not RepeatKind.Optional } repeat)
                targets.Add(new SyncTarget(index - 1, ConcreteFirst(repeat.Inner)));
            else if (before is SeparatedListExpr list)
                targets.Add(new SyncTarget(index - 1, ConcreteFirst(list.Item)));
        }
        return targets;
    }

    /// <summary>What may follow <paramref name="expr"/>. A cycle answers nothing, which only makes insertion rarer.</summary>
    Lookahead Follow(Expr expr)
    {
        if (_follow.TryGetValue(expr, out var known)) return known;
        if (!_followVisiting.Add(expr)) return Lookahead.Empty;
        var occurrence = _occurrences[expr];
        Lookahead result;
        switch (occurrence.Parent)
        {
            case null:
            {
                var root = occurrence.Root;
                if (root.RuleKey is null)
                {
                    // An alternative ends where its point's callers continue, or where its Pratt loop goes on.
                    result = PostfixOperators(root.PointKey!, null);
                    if (_calls.TryGetValue(root.PointKey!, out var pointCalls))
                        foreach (var call in pointCalls) result = result.Union(Follow(call));
                    else
                        result = result.Union(Lookahead.Of(Terminal.End));
                }
                else if (!_calls.TryGetValue(root.RuleKey, out var calls))
                {
                    result = Lookahead.Of(Terminal.End);
                }
                else
                {
                    result = Lookahead.Empty;
                    foreach (var call in calls) result = result.Union(Follow(call));
                }
                break;
            }
            case SequenceExpr sequence:
            {
                int count = sequence.Items.Count;
                result = FirstOfItems(sequence.Items, occurrence.Index + 1, count, concrete: true);
                if (NullableItems(sequence.Items, occurrence.Index + 1, count)) result = result.Union(Follow(sequence));
                break;
            }
            case RepeatExpr repeat:
                result = repeat.Kind == RepeatKind.Optional ? Follow(repeat) : ConcreteFirst(expr).Union(Follow(repeat));
                break;
            case SeparatedListExpr list:
                result = occurrence.Index == 0 ? ConcreteFirst(list.Separator).Union(Follow(list)) : ConcreteFirst(list.Item);
                break;
            case PredicateExpr:
                result = Lookahead.Empty;
                break;
            default: // a choice alternative or a labeled element
                result = Follow(occurrence.Parent);
                break;
        }
        _followVisiting.Remove(expr);
        _follow[expr] = result;
        return result;
    }
}
