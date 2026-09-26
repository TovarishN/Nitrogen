using System.Diagnostics;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public class DebouncerTests
{
    [Fact]
    public async Task A_burst_of_signals_runs_once_after_the_quiet_period()
    {
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(100));
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < 5; i++)
        {
            debouncer.Signal();
            await Task.Delay(20);
        }
        await debouncer.WaitAsync(CancellationToken.None);
        // The last signal came at 80 ms or later, and the quiet period is 100 ms.
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(170));

        // The whole burst was one run: nothing is left to wait for.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => debouncer.WaitAsync(timeout.Token));
    }
}
