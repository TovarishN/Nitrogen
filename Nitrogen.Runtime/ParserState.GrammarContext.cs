namespace Nitrogen;

public unsafe ref partial struct ParserState
{
    /// <summary>Tests a composed Boolean grammar flag without consuming input or producing a node.</summary>
    public readonly bool TestContext(GrammarContextDecl context) =>
        (GrammarContext & ContextLanguage().ContextMask(context)) != 0;

    /// <summary>Parses a child under one replacement flag value, restoring all caller flags even on an exception.</summary>
    public bool ParseWithContext(GrammarContextDecl context, bool value, delegate*<ref ParserState, bool> parse)
    {
        ulong mask = ContextLanguage().ContextMask(context);
        ulong previous = GrammarContext;
        GrammarContext = value ? previous | mask : previous & ~mask;
        try { return parse(ref this); }
        finally { GrammarContext = previous; }
    }

    readonly Language ContextLanguage() => Language ?? throw new InvalidOperationException("Grammar contexts need a Language.");
}
