using Nitrogen.Semantic;

namespace Nitrogen;

/// <summary>An immutable composition of syntax modules. Thread-safe; parse from any thread.</summary>
public sealed unsafe class Language
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

    bool Run(string text, Rule start, ParseContext context, bool recovering)
    {
        var arena = context.Arena;
        arena.Clear();
        context.Memo.Clear();
        arena.Recovering = recovering;
        arena.CallFlagMask = recovering ? 3 : 0;
        var state = new ParserState(text, arena, context.Memo, Trivia, TriviaStart) { Language = this };
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
