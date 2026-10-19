using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>An immutable composition of syntax modules. Thread-safe; parse from any thread.</summary>
public sealed unsafe partial class Language
{
    readonly ExtensionPoint?[] _points;
    readonly SyntaxModule?[] _modulesById;
    internal readonly delegate*<ReadOnlySpan<char>, int, int> Trivia;
    internal readonly AsciiSet TriviaStart;

    internal Language(SyntaxModule[] modules, ExtensionPoint?[] points, delegate*<ReadOnlySpan<char>, int, int> trivia, AsciiSet triviaStart, SemanticCatalog semanticCatalog,
        DeclarativeLowering declarative)
    {
        Modules = modules;
        SemanticCatalog = semanticCatalog;
        Declarative = declarative;
        _points = points;
        Trivia = trivia;
        TriviaStart = triviaStart;
        _modulesById = new SyntaxModule?[modules.Length == 0 ? 1 : modules.Max(m => m.Id) + 1];
        foreach (var module in modules) _modulesById[module.Id] = module;
    }

    public IReadOnlyList<SyntaxModule> Modules { get; }

    /// <summary>This language's dense kind ids: what its trees store per node in place of the full kind.</summary>
    internal DenseKindTable Kinds { get; } = new();

    /// <summary>The 12-bit id a tree of this language stores for <paramref name="kind"/>, assigned on first sight.</summary>
    internal ushort DenseKind(int kind) => Kinds.DenseKind(kind);

    /// <summary>The full kind of a dense id that <see cref="DenseKind"/> returned.</summary>
    internal int KindOf(ushort dense) => Kinds.KindOf(dense);

    public SemanticCatalog SemanticCatalog { get; }

    /// <summary>The composed declarative rules (issue 251).</summary>
    public DeclarativeLowering Declarative { get; }

    /// <summary>The module with process-wide id <paramref name="id"/>, when it is part of this language.</summary>
    internal SyntaxModule? ModuleById(int id) => (uint)id < (uint)_modulesById.Length ? _modulesById[id] : null;

    /// <summary>Whether child <paramref name="slot"/> of a <paramref name="kind"/> node holds a list (<see cref="SyntaxModule.IsListSlot"/>).</summary>
    public bool IsListSlot(int kind, int slot) =>
        ModuleById(SyntaxKinds.ModuleOf(kind))?.IsListSlot(SyntaxKinds.LocalOf(kind), slot) ?? false;

    public ExtensionPoint GetExtensionPoint(ExtensionPointDecl decl) =>
        (uint)decl.GlobalId < (uint)_points.Length && _points[decl.GlobalId] is { } point
            ? point
            : throw new InvalidOperationException($"Extension point '{decl}' is not part of this language.");

    public string GetKindName(int kind)
    {
        int module = SyntaxKinds.ModuleOf(kind), local = SyntaxKinds.LocalOf(kind);
        if (module == 0) return SyntaxKinds.GetBuiltinName(local);
        var owner = module < _modulesById.Length ? _modulesById[module] : null;
        return owner is null ? "#" + kind : owner.GetKindName(local);
    }

    public ParseResult Parse(string text, Rule start) => Parse(text, start, recoverOnly: false);

    /// <summary>
    /// Parses <paramref name="text"/>; the tree's <see cref="SyntaxTree.Text"/> is <paramref name="treeText"/>, which must have
    /// the same length. Every token's characters must be equal in both: they may differ only in trivia (a preprocessor's masking).
    /// Diagnostics and repairs carry spans and message strings, never text, so they are the same either way.
    /// </summary>
    public ParseResult Parse(string text, Rule start, string treeText)
    {
        ArgumentNullException.ThrowIfNull(treeText);
        ArgumentNullException.ThrowIfNull(text);
        if (treeText.Length != text.Length)
            throw new ArgumentException("The tree text must be as long as the parsed text.", nameof(treeText));
        return Parse(text, start, recoverOnly: false, treeText);
    }

    /// <summary>
    /// Runs the recovery pass alone (issue 235). A test hook: on valid input it must give the same
    /// tree as <see cref="Parse(string, Rule)"/> without a single repair.
    /// </summary>
    internal ParseResult ParseRecovering(string text, Rule start) => Parse(text, start, recoverOnly: true);

    /// <summary>
    /// The fast pass alone, as parsing worked before issue 235: a test hook for the mutation
    /// corpus's first-diagnostic check (spec §8, I2).
    /// </summary>
    internal ParseResult ParseFast(string text, Rule start)
    {
        ArgumentNullException.ThrowIfNull(text);
        var context = ParseContext.Acquire();
        try
        {
            var arena = context.Arena;
            if (Run(text, start, context, recovering: false))
                return ParseResult.Succeeded(TreeFreezer.Freeze(arena, arena.ChildStack[0], text, this), arena);
            return ParseResult.Failed(TreeFreezer.ErrorTree(text, this), arena, ParseResult.CaptureFailure(arena));
        }
        finally
        {
            ParseContext.Release(context);
        }
    }

    /// <summary>
    /// Test hook: the fast pass with tracking on, returning the rule results it recorded (nodes are arena
    /// indices) whether or not the parse succeeded.
    /// </summary>
    internal IReadOnlyList<ReuseRecord> ParseTracked(string text, Rule start)
    {
        ArgumentNullException.ThrowIfNull(text);
        var context = ParseContext.Acquire();
        try
        {
            Run(text, start, context, recovering: false, tracking: true);
            var arena = context.Arena;
            arena.Tracking = false;
            return arena.Records.AsSpan(0, arena.RecordCount).ToArray();
        }
        finally
        {
            ParseContext.Release(context);
        }
    }

    /// <summary>
    /// What the fast pass expected where it failed on <paramref name="text"/> (issue 238): literals
    /// (keywords, punctuation) and token names, and the position. (-1, empty) when the text parses.
    /// Completion appends a character no grammar accepts, so the failure lands where the user types.
    /// </summary>
    public (int Position, IReadOnlyList<(string Text, bool IsLiteral)> Items) Expected(string text, Rule start)
    {
        using var result = ParseFast(text, start);
        if (result.Success || result.Diagnostics.Length == 0) return (-1, []);
        return (result.Diagnostics[0].Span.Start, result.ExpectedItems());
    }

    /// <summary>
    /// The fast pass; when it fails, the recovery pass, which yields a whole-input tree unless even
    /// the start rule's first element cannot start (then a single Error root).
    /// </summary>
    /// <summary>
    /// Parses like <see cref="Parse(string, Rule)"/> and keeps the data <see cref="Reparse"/> needs to reuse this
    /// result after an edit: how far each rule result examined the text. An editor parses once with this and then
    /// reparses; a one-shot parse should use <see cref="Parse(string, Rule)"/>, which keeps nothing extra.
    /// <paramref name="treeText"/>, when given, is the tree's text, as for <see cref="Parse(string, Rule, string)"/>.
    /// </summary>
    public ParseResult ParseIncremental(string text, Rule start, string? treeText = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (treeText is not null && treeText.Length != text.Length)
            throw new ArgumentException("The tree text must be as long as the parsed text.", nameof(treeText));
        var result = Parse(text, start, recoverOnly: false, treeText, incremental: true);
        result.StartRule = start;
        result.ReuseText = text;
        return result;
    }

    /// <summary>
    /// Parses <paramref name="text"/>, an edit of <paramref name="previous"/>'s text by <paramref name="changes"/>
    /// (old-text coordinates, sorted by start, non-overlapping), reusing every subtree of <paramref name="previous"/>
    /// whose parse examined no edited text. The result equals a full parse of <paramref name="text"/>, is incremental
    /// itself, and is independent of <paramref name="previous"/>: either may be disposed first. Only a successful fast
    /// pass reuses; a text with errors is parsed whole.
    /// </summary>
    /// <exception cref="ObjectDisposedException"><paramref name="previous"/> is disposed.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="previous"/> is not incremental or comes from another Language, or the changes are unsorted,
    /// overlapping, out of range, or do not turn the old text's length into <paramref name="text"/>'s.
    /// </exception>
    public ParseResult Reparse(ParseResult previous, string text, ReadOnlySpan<TextChange> changes, string? treeText = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(text);
        if (previous.Tree.IsDisposed) throw new ObjectDisposedException(nameof(ParseResult));
        if (previous.Reuse is null) throw new ArgumentException("The previous result is not incremental; parse it with ParseIncremental.", nameof(previous));
        if (!ReferenceEquals(previous.Tree.Language, this)) throw new ArgumentException("The previous result comes from another Language.", nameof(previous));
        if (treeText is not null && treeText.Length != text.Length)
            throw new ArgumentException("The tree text must be as long as the parsed text.", nameof(treeText));
        int oldLength = previous.Tree.Text.Length, length = oldLength, end = 0;
        foreach (var change in changes)
        {
            if (change.Start < end || change.OldLength < 0 || change.NewLength < 0 || change.Start + change.OldLength > oldLength)
                throw new ArgumentException("Changes must be sorted, non-overlapping and within the old text.", nameof(changes));
            end = change.Start + change.OldLength;
            length += change.NewLength - change.OldLength;
        }
        if (length != text.Length)
            throw new ArgumentException($"The changes turn a {oldLength}-character text into {length} characters, not {text.Length}.", nameof(text));
        // After an error the base is the last successful parse, whose text the given changes do not start from: map
        // it to the new text by their common prefix and suffix, which is exact about what text is unchanged.
        var baseTree = previous.ReuseTree ?? previous.Tree;
        string baseText = previous.ReuseText!;
        var baseChanges = previous.ReuseTree is null ? changes.ToArray() : [TextChange.Between(baseText, text)];
        var reuse = new ReuseContext(baseTree, previous.Reuse, baseChanges);
        var result = Parse(text, previous.StartRule, recoverOnly: false, treeText, incremental: true, reuse);
        result.StartRule = previous.StartRule;
        if (result.Success)
        {
            result.ReuseText = text;
        }
        else
        {
            // The text has errors: keep the base to reuse from once it parses again.
            result.Reuse = previous.Reuse;
            result.ReuseTree = baseTree.Copy();
            result.ReuseText = baseText;
        }
        return result;
    }


    ParseResult Parse(string text, Rule start, bool recoverOnly, string? treeText = null, bool incremental = false, ReuseContext? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        treeText ??= text;
        var context = ParseContext.Acquire();
        try
        {
            var arena = context.Arena;
            var failure = FirstFailure.None;
            if (!recoverOnly)
            {
                if (Run(text, start, context, recovering: false, tracking: incremental, reuse: reuse))
                {
                    if (!incremental)
                        return ParseResult.Succeeded(TreeFreezer.Freeze(arena, arena.ChildStack[0], treeText, this), arena);
                    var map = new int[arena.NodeCount];
                    var frozen = ParseResult.Succeeded(TreeFreezer.Freeze(arena, arena.ChildStack[0], treeText, this, arenaToTree: map), arena);
                    frozen.Reuse = ReuseTable.From(arena, map, reuse?.Table);
                    frozen.Stats = frozen.Stats with { ReusedNodes = arena.ReusedNodes };
                    return frozen;
                }
                // A reused subtree skips the attempts made inside it, and with them what they expected at the
                // furthest failure: retry the fast pass without reuse so the first diagnostic is a full parse's.
                if (reuse is not null) Run(text, start, context, recovering: false);
                failure = ParseResult.CaptureFailure(arena);
            }
            if (incremental)
            {
                // A text with errors keeps nothing to reuse: the next reparse parses it whole.
                var recovered = Parse(text, start, failure, context, treeText);
                recovered.Reuse = ReuseTable.Empty;
                return recovered;
            }
            return Parse(text, start, failure, context, treeText, recoverOnly);
        }
        finally
        {
            ParseContext.Release(context);
        }
    }

    /// <summary>The recovery pass after the fast pass failed with <paramref name="failure"/> (or alone, for <paramref name="recoverOnly"/>).</summary>
    ParseResult Parse(string text, Rule start, FirstFailure failure, ParseContext context, string treeText, bool recoverOnly = false)
    {
        var arena = context.Arena;
        if (!Run(text, start, context, recovering: true))
            return ParseResult.Failed(TreeFreezer.ErrorTree(treeText, this), arena, failure);
        var tree = TreeFreezer.Freeze(arena, arena.ChildStack[0], treeText, this);
        return arena.HasRecovery || !recoverOnly
            ? ParseResult.Recovered(tree, arena, failure)
            : ParseResult.Succeeded(tree, arena);
    }

    /// <summary>
    /// Parses <paramref name="text"/> as the rule <paramref name="category"/> (qualified: <c>Module.Rule</c>, a
    /// syntax or extensible rule), where each <c>$Name</c> of <paramref name="holes"/> stands for a node of the
    /// rule it maps to (spec: quasiquote rewrites). The fast pass only: a template either parses or doesn't.
    /// </summary>
    public ParseResult ParseTemplate(string text, string category, IReadOnlyDictionary<string, string> holes)
        => ParseTemplate(text, category, holes.ToDictionary(h => h.Key, h => QuoteHole.Node(h.Value)));

    public ParseResult ParseTemplate(string text, string category, IReadOnlyDictionary<string, QuoteHole> holes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(holes);
        int dot = category.LastIndexOf('.');
        string moduleName = dot < 0 ? "" : category[..dot], ruleName = category[(dot + 1)..];
        Rule start;
        ExtensionPointDecl? point = null;
        if (Modules.FirstOrDefault(m => m.Name == moduleName)?.GetRule(ruleName) is { } rule) start = rule;
        else
        {
            point = _points.FirstOrDefault(p => p is not null && p.Decl.QualifiedName == category)?.Decl
                ?? throw new ArgumentException($"'{category}' is not a rule of this language.", nameof(category));
            start = new Rule(category, &ParseTemplateStart);
        }
        var context = ParseContext.Acquire();
        try
        {
            var arena = context.Arena;
            if (Run(text, start, context, recovering: false, holes, point))
                return ParseResult.Succeeded(TreeFreezer.Freeze(arena, arena.ChildStack[0], text, this), arena);
            return ParseResult.Failed(TreeFreezer.ErrorTree(text, this), arena, ParseResult.CaptureFailure(arena));
        }
        finally
        {
            ParseContext.Release(context);
        }
    }

    /// <summary>What the template parser expects at its first failure, for completion inside a quasiquote.</summary>
    public (int Position, IReadOnlyList<(string Text, bool IsLiteral)> Items) ExpectedTemplate(
        string text, string category, IReadOnlyDictionary<string, string> holes)
    {
        using var result = ParseTemplate(text, category, holes);
        return result.Success || result.Diagnostics.Length == 0 ? (-1, [])
            : (result.Diagnostics[0].Span.Start, result.ExpectedItems());
    }

    public (int Position, IReadOnlyList<(string Text, bool IsLiteral)> Items) ExpectedTemplate(
        string text, string category, IReadOnlyDictionary<string, QuoteHole> holes)
    {
        using var result = ParseTemplate(text, category, holes);
        return result.Success || result.Diagnostics.Length == 0 ? (-1, [])
            : (result.Diagnostics[0].Span.Start, result.ExpectedItems());
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<DeclarativeRule, Lazy<RewriteTemplate>> _rewrites = new();

    /// <summary>The document path of a template's instantiation: a URI no document has (spec: quasiquote rewrites).</summary>
    internal const string RewritePath = "nitrogen:rewrite";

    /// <summary>
    /// A rewrite rule's template, parsed once as its category with its holes (spec: quasiquote rewrites); or why it
    /// can't be used: it doesn't parse (with the parser's first message), or it declares a name. The text is trimmed
    /// first, so spans in the tree are relative to the trimmed text.
    /// </summary>
    public RewriteTemplate RewriteTemplate(DeclarativeRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return _rewrites.GetOrAdd(rule, r => new Lazy<RewriteTemplate>(() => ParseRewrite(r))).Value;
    }

    RewriteTemplate ParseRewrite(DeclarativeRule rule)
    {
        var spec = rule.Rewrite ?? throw new ArgumentException("Not a rewrite rule.", nameof(rule));
        string text = spec.Text.Trim();
        var holes = spec.Holes.ToDictionary(h => h.Name, h => new QuoteHole(h.Rule, h.IsList));
        ParseResult parsed;
        try { parsed = ParseTemplate(text, spec.Category, holes); }
        catch (ArgumentException error) { return new RewriteTemplate(null, error.Message); }
        if (!parsed.Success)
        {
            var first = parsed.Diagnostics[0];
            string error = $"'{text}' does not parse as {spec.Category}: {parsed.FormatMessage(first)} at {first.Span.Start}";
            parsed.Dispose();
            return new RewriteTemplate(null, error);
        }
        var project = new Nitrogen.Binding.Project(this);
        if (project.Set(RewritePath, parsed.Tree).Declarations.Count > 0)
        {
            parsed.Dispose();
            return new RewriteTemplate(null, "a template cannot declare names");
        }
        return new RewriteTemplate(parsed.Tree, null);
    }

    static bool ParseTemplateStart(ref ParserState s) => s.ParseExtensible(s.TemplateStart!, 0);

    bool Run(string text, Rule start, ParseContext context, bool recovering,
        IReadOnlyDictionary<string, QuoteHole>? holes = null, ExtensionPointDecl? templateStart = null, bool tracking = false,
        ReuseContext? reuse = null)
    {
        var arena = context.Arena;
        arena.Clear();
        context.Memo.Clear();
        arena.Recovering = recovering;
        // Only a plain fast pass records reuse data: recovery repairs and template holes depend on more than the text.
        arena.Tracking = tracking && !recovering && holes is null;
        if (!arena.Tracking) reuse = null;
        arena.GraftSource = reuse?.Tree;
        arena.CallFlagMask = recovering ? 3 : 0;
        var state = new ParserState(text, arena, context.Memo, Trivia, TriviaStart) { Language = this, Holes = holes, TemplateStart = templateStart, Reuse = reuse };
        if (holes is not null) state.SkipTrivia();
        var parse = start.Parse;
        if (!parse(ref state)) return false;
        state.SkipTrivia();
        if (!state.AtEnd)
        {
            if (!recovering)
            {
                state.Expect("end of input");
                return false;
            }
            arena.HasRecovery = true;
            arena.TrailingStart = state.Position;
        }
        if (arena.ChildStackCount != 1 || arena.FrameCount != 0)
            throw new InvalidOperationException($"Start rule '{start.Name}' must produce exactly one node.");
        return true;
    }
}

/// <summary>
/// Maps full kinds (<c>(moduleId &lt;&lt; 16) | localKind</c>) to dense 12-bit ids, so a tree stores a
/// <see cref="ushort"/> per node: the id and the node's flags (<see cref="Pack"/>). Ids are assigned on first sight; the built-in kinds are pre-assigned
/// (dense id = local kind). Safe for concurrent use: lookups are lock-free, a miss assigns under a lock.
/// Writers fill an entry, then publish it with a release write; arrays are only appended to in place
/// or replaced by a grown copy, so a reader sees an entry either unassigned (then takes the lock) or
/// complete. Trees without a <see cref="Language"/> use <see cref="Shared"/>.
/// </summary>
internal sealed class DenseKindTable
{
    /// <summary>The table of trees built without a language (hand-built test trees, any module).</summary>
    public static readonly DenseKindTable Shared = new();

    const int Builtins = SyntaxKinds.ListHole + 1;

    /// <summary>
    /// A tree stores a node's dense id in the low <see cref="IdBits"/> bits of a <see cref="ushort"/> and
    /// its <see cref="NodeFlags"/> in the top four, so a language has at most <see cref="MaxDense"/> kinds
    /// besides <see cref="SyntaxKinds.None"/>.
    /// </summary>
    public const int IdBits = 12;

    /// <summary>The largest dense id, also the mask that takes the id out of a packed entry.</summary>
    public const int MaxDense = (1 << IdBits) - 1;

    /// <summary>A packed tree entry: <paramref name="dense"/> with <paramref name="flags"/> above it.</summary>
    public static ushort Pack(ushort dense, NodeFlags flags) => (ushort)(dense | (int)flags << IdBits);

    readonly object _gate = new();
    // [module][local] -> dense id; 0 means unassigned, except for kind None (0), which is dense 0.
    ushort[]?[] _byModule;
    // dense id -> kind. Entries [0, _count) are assigned.
    int[] _kinds;
    int _count;

    public DenseKindTable()
    {
        var builtins = new ushort[Builtins];
        _kinds = new int[64];
        for (int k = 0; k < Builtins; k++)
        {
            builtins[k] = (ushort)k;
            _kinds[k] = k;
        }
        _byModule = [builtins];
        _count = Builtins;
    }

    /// <summary>
    /// Dense id → kind, for every id assigned so far. A tree keeps the array current when it is
    /// frozen: later assignments go to new indices or a grown copy, never change existing entries.
    /// </summary>
    public int[] Snapshot => Volatile.Read(ref _kinds);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public ushort DenseKind(int kind)
    {
        var modules = Volatile.Read(ref _byModule);
        int module = kind >>> 16, local = kind & 0xFFFF;
        if ((uint)module < (uint)modules.Length && Volatile.Read(ref modules[module]) is { } locals && (uint)local < (uint)locals.Length)
        {
            ushort dense = Volatile.Read(ref locals[local]);
            if (dense != 0 || kind == SyntaxKinds.None) return dense;
        }
        return Assign(kind);
    }

    public int KindOf(ushort dense)
    {
        var kinds = Volatile.Read(ref _kinds);
        if (dense < kinds.Length && (kinds[dense] != SyntaxKinds.None || dense == 0)) return kinds[dense];
        lock (_gate)
        {
            if (dense >= _count) throw new ArgumentOutOfRangeException(nameof(dense));
            return _kinds[dense];
        }
    }

    ushort Assign(int kind)
    {
        lock (_gate)
        {
            int module = kind >>> 16, local = kind & 0xFFFF;
            var modules = _byModule;
            var locals = module < modules.Length ? modules[module] : null;
            if (locals is not null && local < locals.Length && locals[local] != 0) return locals[local];
            if (_count > MaxDense)
                throw new InvalidOperationException($"More than {MaxDense} distinct syntax kinds in one language.");

            ushort dense = (ushort)_count;
            if (dense == _kinds.Length)
            {
                var grown = new int[Math.Min(_kinds.Length * 2, MaxDense + 1)];
                Array.Copy(_kinds, grown, _count);
                Volatile.Write(ref _kinds, grown);
            }
            _kinds[dense] = kind;
            _count++;

            if (locals is not null && local < locals.Length)
            {
                Volatile.Write(ref locals[local], dense);
                return dense;
            }
            var newLocals = new ushort[Math.Max(local + 1, Math.Min((locals?.Length ?? 8) * 2, 0x10000))];
            locals?.CopyTo(newLocals, 0);
            newLocals[local] = dense;
            if (module >= modules.Length)
            {
                var newModules = new ushort[]?[Math.Max(module + 1, Math.Min(modules.Length * 2, 0x10000))];
                modules.CopyTo(newModules, 0);
                newModules[module] = newLocals;
                Volatile.Write(ref _byModule, newModules);
            }
            else Volatile.Write(ref modules[module], newLocals);
            return dense;
        }
    }
}

/// <summary>
/// A rewrite rule's parsed template, or why it can't be used. The tree is the language's, shared by every use and kept
/// for the language's lifetime: read it, never dispose it.
/// </summary>
public sealed record RewriteTemplate(SyntaxTree? Tree, string? Error);
