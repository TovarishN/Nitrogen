using System.Diagnostics;

namespace Nitrogen.Cli;

/// <summary>
/// Coalesces a burst of file-system events into one run. <see cref="WaitAsync"/> returns once a
/// signal has come and no other has followed for the quiet period. An editor's save raises
/// several events; the watch loop must recompile once, after the last of them.
/// </summary>
internal sealed class Debouncer(TimeSpan quiet)
{
    readonly SemaphoreSlim _signals = new(0);
    long _last;

    /// <summary>Called from any thread: a watched file changed.</summary>
    public void Signal()
    {
        Volatile.Write(ref _last, Stopwatch.GetTimestamp());
        _signals.Release();
    }

    public async Task WaitAsync(CancellationToken cancel)
    {
        await _signals.WaitAsync(cancel);
        while (true)
        {
            var since = Stopwatch.GetElapsedTime(Volatile.Read(ref _last));
            if (since >= quiet) break;
            await Task.Delay(quiet - since, cancel);
        }
        while (_signals.Wait(0))
        {
        }
    }
}
