namespace Nitrogen;

/// <summary>Resource limits for one complete syntax-pattern match, including input validation.</summary>
public sealed record QuoteMatchOptions
{
    public int MaxWorkSteps { get; init; } = 1_000_000;
    public CancellationToken CancellationToken { get; init; }
}
