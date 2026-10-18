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
    ParseResult Parse(string text, Rule start, bool recoverOnly)
    {
        ArgumentNullException.ThrowIfNull(text);
        var context = ParseContext.Acquire();
        try
        {
            var arena = context.Arena;
            var failure = FirstFailure.None;
            if (!recoverOnly)
            {
                if (Run(text, start, context, recovering: false))
                    return ParseResult.Succeeded(TreeFreezer.Freeze(arena, arena.ChildStack[0], text, this), arena);
                failure = ParseResult.CaptureFailure(arena);
            }
            if (!Run(text, start, context, recovering: true))
                return ParseResult.Failed(TreeFreezer.ErrorTree(text, this), arena, failure);
            var tree = TreeFreezer.Freeze(arena, arena.ChildStack[0], text, this);
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
/// A rewrite rule's parsed template, or why it can't be used. The tree is the language's, shared by every use and kept
/// for the language's lifetime: read it, never dispose it.
/// </summary>
public sealed record RewriteTemplate(SyntaxTree? Tree, string? Error);
