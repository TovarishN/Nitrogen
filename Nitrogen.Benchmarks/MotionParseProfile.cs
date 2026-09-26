using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;

namespace Nitrogen.Benchmarks;

/// <summary>NitrogenTree under EventPipe CPU sampling; writes a .speedscope.json per run.</summary>
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class MotionParseProfile
{
    string[] _sources = [];

    [GlobalSetup]
    public void Setup() => _sources = MotionCorpus.Files().Select(File.ReadAllText).ToArray();

    [Benchmark]
    public int NitrogenTree()
    {
        int nodes = 0;
        foreach (string source in _sources)
        {
            using var result = NitrogenMotionParser.Language.Parse(source, MotionModule.File);
            nodes += result.Stats.TreeNodes;
        }
        return nodes;
    }
}
