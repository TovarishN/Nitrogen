namespace Nitrogen;

internal sealed class QuoteMatchBudget(QuoteMatchOptions options)
{
    int _remaining = options.MaxWorkSteps;

    internal void Step()
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (_remaining-- <= 0) throw new QuoteMatchLimitException();
    }
}

internal sealed class QuoteMatchLimitException : Exception;
