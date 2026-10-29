namespace Nitrogen;

/// <summary>Owns compiled pattern storage. Disposing it invalidates future matches, not borrowed captures.</summary>
public sealed class PatternCompilation : IDisposable
{
    internal PatternCompilation(ParseResult? parsed, string category, IReadOnlyDictionary<string, QuoteHole> captures,
        IReadOnlyList<QuoteDiagnostic> diagnostics, IReadOnlyDictionary<string, NamedPatternDefinition>? references = null)
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        if (parsed is not null) Pattern = new(parsed, category, captures, references);
    }

    public bool Success => Pattern is not null;
    public QuotePattern? Pattern { get; }
    public IReadOnlyList<QuoteDiagnostic> Diagnostics { get; }
    public void Dispose() => Pattern?.DisposeStorage();
}

/// <summary>One node a search matched, with that match's captures. Both borrow the input syntax.</summary>
public sealed record QuoteFoundMatch(QuoteNode Node, IReadOnlyDictionary<string, QuoteArgument> Captures);

/// <summary>Every match of a search in preorder; <see cref="Complete"/> is false when the work limit stopped it early.</summary>
public sealed record QuoteFindResult(IReadOnlyList<QuoteFoundMatch> Matches, bool Complete, IReadOnlyList<QuoteDiagnostic> Diagnostics);

/// <summary>A successful match borrows its captures from the input syntax. Keep that syntax alive while consuming them.</summary>
public sealed record QuoteMatchResult(bool Matched, IReadOnlyDictionary<string, QuoteArgument> Captures,
    IReadOnlyList<QuoteDiagnostic> Diagnostics);
