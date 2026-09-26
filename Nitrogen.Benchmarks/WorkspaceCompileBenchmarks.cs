using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nitrogen.Workspace;

namespace Nitrogen.Benchmarks;

/// <summary>
/// The authoring loop's latency (issue 236, spec §6): a warm recompile of Motion.ngr, from grammar
/// text to a built Language, snapshot disposed. The gate is ≤ 1 s. The cold time (the first
/// compile in a fresh process, Roslyn's JIT included) is the compile line `nitrogen parse` prints.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class WorkspaceCompileBenchmarks
{
    GrammarWorkspace _workspace = null!;

    static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [GlobalSetup]
    public void Setup()
    {
        _workspace = new GrammarWorkspace();
        _workspace.SetGrammar("Motion.ngr", File.ReadAllText(Path.Combine(Here(), "..", "Nitrogen.MotionDsl", "Motion.ngr")));
        using var first = _workspace.Compile();
        if (!first.Succeeded) throw new InvalidOperationException(string.Join("\n", first.Diagnostics));
    }

    [Benchmark]
    public int WarmRecompile()
    {
        using var snapshot = _workspace.Compile();
        return snapshot.Version;
    }
}
