using BenchmarkDotNet.Attributes;
using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;

namespace Nitrogen.Benchmarks;

/// <summary>
/// Binding's cost against parsing (issue 237, spec §4): the whole .motion corpus per operation.
/// Parse is the baseline. Bind walks the trees; BindAndDiagnose is what an editor does per change:
/// bind every file into a project and ask each for its diagnostics. The gate is BindAndDiagnose ≤ 0.5×.
/// </summary>
[MemoryDiagnoser]
public class MotionBindingBenchmarks
{
    string[] _sources = [];
    ParseResult[] _parsed = [];

    [GlobalSetup]
    public void Setup()
    {
        _sources = MotionCorpus.Files().Select(File.ReadAllText).ToArray();
        if (_sources.Length == 0) throw new InvalidOperationException($"no .motion corpus under {MotionCorpus.Root}");
        _parsed = _sources.Select(s => NitrogenMotionParser.Language.Parse(s, MotionModule.File)).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var result in _parsed) result.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Parse()
    {
        int nodes = 0;
        foreach (string source in _sources)
        {
            using var result = NitrogenMotionParser.Language.Parse(source, MotionModule.File);
            nodes += result.Stats.TreeNodes;
        }
        return nodes;
    }

    [Benchmark]
    public int Bind()
    {
        int references = 0;
        foreach (var result in _parsed) references += FileBinding.Bind("f", result.Tree).References.Count;
        return references;
    }

    [Benchmark]
    public int BindAndDiagnose()
    {
        var project = new Project(NitrogenMotionParser.Language);
        for (int i = 0; i < _parsed.Length; i++) project.Set(i.ToString(), _parsed[i].Tree);
        int diagnostics = 0;
        for (int i = 0; i < _parsed.Length; i++) diagnostics += project.Diagnostics(i.ToString()).Count;
        return diagnostics;
    }
}
