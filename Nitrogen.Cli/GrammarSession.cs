using System.Diagnostics;
using System.Globalization;
using Nitrogen.Binding;
using Nitrogen.Workspace;

namespace Nitrogen.Cli;

/// <summary>
/// The command core <c>parse</c> and <c>watch</c> share (issue 236). Each <see cref="Run"/>
/// re-reads the grammar files, compiles a new snapshot and parses every sample with it, writing
/// to one <see cref="TextWriter"/>. A good snapshot replaces (and disposes) the previous one.
/// </summary>
internal sealed class GrammarSession(CliOptions options, TextWriter output) : IDisposable
{
    readonly GrammarWorkspace _workspace = new();
    WorkspaceSnapshot? _snapshot;

    /// <summary>Where <c>watch</c> listens: a grammar directory's <c>*.ngr</c>, or a single grammar file.</summary>
    public IEnumerable<(string Directory, string Filter)> WatchTargets() =>
        options.Grammars.Select(grammar => Directory.Exists(grammar)
            ? (Path.GetFullPath(grammar), "*.ngr")
            : (Path.GetDirectoryName(Path.GetFullPath(grammar))!, Path.GetFileName(grammar)));

    /// <summary>0 when the grammar compiled and every sample parsed without diagnostics, else 1.</summary>
    public int Run()
    {
        List<string>? files;
        try
        {
            files = LoadGrammars();
        }
        catch (IOException error)
        {
            output.WriteLine($"error: {error.Message}");
            return 1;
        }
        if (files is null) return 1;

        long started = Stopwatch.GetTimestamp();
        var snapshot = _workspace.Compile();
        double compileMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!snapshot.Succeeded)
        {
            output.WriteLine(Invariant($"compile failed (version {snapshot.Version}) in {compileMs:F0} ms"));
            foreach (var diagnostic in snapshot.Diagnostics) output.WriteLine(diagnostic);
            snapshot.Dispose();
            return 1;
        }

        _snapshot?.Dispose();
        _snapshot = snapshot;
        output.WriteLine(Invariant($"compiled {Plural(files.Count, "grammar")} (version {snapshot.Version}) in {compileMs:F0} ms"));
        foreach (var diagnostic in snapshot.Diagnostics) output.WriteLine(diagnostic);
        if (snapshot.FindRule(options.Start) is null)
        {
            output.WriteLine($"error: no rule '{options.Start}'");
            return 1;
        }

        int failed = 0;
        var samples = new List<Sample>();
        try
        {
            foreach (string path in options.Samples)
            {
                var sample = ParseSample(snapshot, path);
                if (sample is null) failed++;
                else samples.Add(sample);
            }
            Project? project = null;
            if (options.Bind)
            {
                project = new Project(snapshot.Language!);
                foreach (var sample in samples) project.Set(sample.Path, sample.Result.Tree);
            }
            foreach (var sample in samples)
                if (!Report(sample, project)) failed++;
        }
        finally
        {
            foreach (var sample in samples) sample.Result.Dispose();
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Syncs the workspace with the grammar files on disk; null (reported) when one is missing.</summary>
    List<string>? LoadGrammars()
    {
        var files = new List<string>();
        foreach (string grammar in options.Grammars)
        {
            if (Directory.Exists(grammar))
            {
                files.AddRange(Directory.GetFiles(grammar, "*.ngr").Order(StringComparer.Ordinal));
            }
            else if (File.Exists(grammar))
            {
                files.Add(grammar);
            }
            else
            {
                output.WriteLine($"error: no grammar file or directory '{grammar}'");
                return null;
            }
        }
        if (files.Count == 0)
        {
            output.WriteLine("error: no .ngr files");
            return null;
        }

        files = files.Distinct(StringComparer.Ordinal).ToList();
        foreach (string gone in _workspace.Paths.Except(files, StringComparer.Ordinal).ToArray()) _workspace.RemoveGrammar(gone);
        foreach (string file in files) _workspace.SetGrammar(file, File.ReadAllText(file));
        return files;
    }

    /// <summary>A parsed sample waiting to be reported: binding needs every sample's tree at once.</summary>
    sealed record Sample(string Path, SourceText Source, ParseResult Result, double ParseMs);

    Sample? ParseSample(WorkspaceSnapshot snapshot, string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException error)
        {
            output.WriteLine($"{path}: error: {error.Message}");
            return null;
        }
        long started = Stopwatch.GetTimestamp();
        var result = snapshot.Parse(text, options.Start);
        return new Sample(path, new SourceText(text), result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Writes a sample's parse results and, with --bind, its binding; true when it has no diagnostics.</summary>
    bool Report(Sample sample, Project? project)
    {
        var (path, source, result, parseMs) = sample;
        var diagnostics = result.Diagnostics.ToArray();
        output.WriteLine(diagnostics.Length == 0
            ? Invariant($"{path}: ok in {parseMs:F1} ms")
            : Invariant($"{path}: {Plural(diagnostics.Length, "diagnostic")} in {parseMs:F1} ms"));
        foreach (var diagnostic in diagnostics)
        {
            var (line, column) = source.GetLineColumn(diagnostic.Span.Start);
            output.WriteLine($"{path}({line},{column}): {diagnostic.Severity.ToString().ToLowerInvariant()}: {result.FormatMessage(diagnostic)}");
        }
        IReadOnlyList<BindingDiagnostic> bindings = project?.Diagnostics(path) ?? [];
        foreach (var diagnostic in bindings)
        {
            var (line, column) = source.GetLineColumn(diagnostic.Span.Start);
            output.WriteLine($"{path}({line},{column}): error {diagnostic.Code}: {diagnostic.Message}");
        }
        if (options.Tree) output.WriteLine(SyntaxDumper.Dump(result.Tree));
        if (options.Tree && project is not null) WriteReferences(sample, project);
        return diagnostics.Length == 0 && bindings.Count == 0;
    }

    void WriteReferences(Sample sample, Project project)
    {
        output.WriteLine($"{sample.Path} references:");
        foreach (var reference in project[sample.Path].References)
        {
            if (!project.IsEffective(reference)) continue;
            var (line, column) = sample.Source.GetLineColumn(reference.NameSpan.Start);
            var targets = project.Resolve(reference);
            string target = targets.Count == 0 ? "unresolved" : string.Join(", ", targets.Select(s => Describe(s, sample.Path, project)));
            output.WriteLine($"  {reference.Name} ({line},{column}) -> {target}");
        }
    }

    static string Describe(Symbol symbol, string from, Project project)
    {
        if (symbol.IsBuiltin) return $"builtin {symbol.Kind} {symbol.Name}";
        var (line, column) = new SourceText(project[symbol.Path!].Tree.Text).GetLineColumn(symbol.NameSpan.Start);
        return $"{symbol.Kind} {symbol.Name} ({line},{column}){(symbol.Path == from ? "" : $" in {symbol.Path}")}";
    }

    /// <summary>Timings print the same in every culture.</summary>
    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    internal static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public void Dispose()
    {
        _snapshot?.Dispose();
        _snapshot = null;
    }
}
