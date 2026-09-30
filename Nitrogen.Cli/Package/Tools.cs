using System.Diagnostics;

namespace Nitrogen.Cli;

/// <summary>Finds and runs the build tools <c>nitrogen package</c> needs; tests replace the lookups.</summary>
internal sealed class Tools(Func<string, string?>? find = null, Func<string?>? javaHome = null)
{
    readonly Func<string, string?> _find = find ?? FindOnPath;
    readonly Func<string?> _javaHome = javaHome ?? (() => Environment.GetEnvironmentVariable("JAVA_HOME"));

    public string? Find(string name) => _find(name);

    /// <summary>JAVA_HOME's java, or java on PATH; null when neither exists.</summary>
    public string? Java()
    {
        if (_javaHome() is { } home)
        {
            string java = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(java)) return java;
        }
        return _find("java");
    }

    static string? FindOnPath(string name)
    {
        string[] suffixes = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (string suffix in suffixes)
            {
                string candidate = Path.Combine(directory, name + suffix);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Runs <paramref name="file"/> in <paramref name="directory"/>, copying its output; its exit code.</summary>
    public async Task<int> RunAsync(string file, IReadOnlyList<string> args, string directory, TextWriter output, CancellationToken cancel)
    {
        output.WriteLine($"> {Path.GetFileName(file)} {string.Join(' ', args)}");
        var start = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"could not start '{file}'");
        var lines = new object();
        void Copy(string? line) { if (line is not null) lock (lines) output.WriteLine(line); }
        process.OutputDataReceived += (_, e) => Copy(e.Data);
        process.ErrorDataReceived += (_, e) => Copy(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancel);
        return process.ExitCode;
    }
}
