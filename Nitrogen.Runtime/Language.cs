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

    /// <summary>The 16-bit id a tree of this language stores for <paramref name="kind"/>, assigned on first sight.</summary>
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
    ParseResult Parse(string text, Rule start, bool recoverOnly, string? treeText = null)
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
                if (Run(text, start, context, recovering: false))
                    return ParseResult.Succeeded(TreeFreezer.Freeze(arena, arena.ChildStack[0], treeText, this), arena);
                failure = ParseResult.CaptureFailure(arena);
            }
            if (!Run(text, start, context, recovering: true))
                return ParseResult.Failed(TreeFreezer.ErrorTree(treeText, this), arena, failure);
            var tree = TreeFreezer.Freeze(arena, arena.ChildStack[0], treeText, this);
            return arena.HasRecovery || !recoverOnly
                ? ParseResult.Recovered(tree, arena, failure)
                : ParseResult.Succeeded(tree, arena);
        }
        finally
        {
            ParseContext.Release(context);
        }
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
        IReadOnlyDictionary<string, QuoteHole>? holes = null, ExtensionPointDecl? templateStart = null)
    {
        var arena = context.Arena;
        arena.Clear();
        context.Memo.Clear();
        arena.Recovering = recovering;
        arena.CallFlagMask = recovering ? 3 : 0;
        var state = new ParserState(text, arena, context.Memo, Trivia, TriviaStart) { Language = this, Holes = holes, TemplateStart = templateStart };
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
/// Maps full kinds (<c>(moduleId &lt;&lt; 16) | localKind</c>) to dense 16-bit ids, so a tree stores a
/// <see cref="ushort"/> per node. Ids are assigned on first sight; the built-in kinds are pre-assigned
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
            if (_count > ushort.MaxValue)
                throw new InvalidOperationException($"More than {ushort.MaxValue} distinct syntax kinds in one language.");

            ushort dense = (ushort)_count;
            if (dense == _kinds.Length)
            {
                var grown = new int[Math.Min(_kinds.Length * 2, ushort.MaxValue + 1)];
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
