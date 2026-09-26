namespace Nitrogen.Cli;

/// <summary>The <c>nitrogen</c> command: argument checking, <c>parse</c>, and the <c>watch</c> loop.</summary>
internal static class NitrogenCli
{
    static readonly TimeSpan s_quiet = TimeSpan.FromMilliseconds(100);

    /// <summary>The process exit code: 0 clean, 1 compile or parse errors, 2 unusable arguments. <c>watch</c> returns 0 when cancelled.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancel)
    {
        var options = CliOptions.Parse(args, out string error);
        if (options is null)
        {
            output.WriteLine($"error: {error}");
            output.WriteLine(CliOptions.Usage);
            return 2;
        }

        using var session = new GrammarSession(options, output);
        return options.Command == "parse" ? session.Run() : await WatchAsync(session, output, cancel);
    }

    static async Task<int> WatchAsync(GrammarSession session, TextWriter output, CancellationToken cancel)
    {
        var targets = session.WatchTargets().Distinct().ToList();
        foreach (var (directory, _) in targets)
        {
            if (Directory.Exists(directory)) continue;
            output.WriteLine($"error: no directory '{directory}'");
            return 1;
        }

        var debouncer = new Debouncer(s_quiet);
        var watchers = targets.Select(target => Watch(target.Directory, target.Filter, debouncer)).ToList();
        try
        {
            session.Run();
            output.WriteLine($"watching {string.Join(", ", targets.Select(t => Path.Combine(t.Directory, t.Filter)))} (ctrl+c stops)");
            while (true)
            {
                await debouncer.WaitAsync(cancel);
                output.WriteLine();
                session.Run();
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }

    static FileSystemWatcher Watch(string directory, string filter, Debouncer debouncer)
    {
        var watcher = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => debouncer.Signal();
        watcher.Created += (_, _) => debouncer.Signal();
        watcher.Deleted += (_, _) => debouncer.Signal();
        watcher.Renamed += (_, _) => debouncer.Signal();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
