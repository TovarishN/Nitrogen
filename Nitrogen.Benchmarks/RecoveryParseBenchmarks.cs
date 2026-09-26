using BenchmarkDotNet.Attributes;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Tests;

namespace Nitrogen.Benchmarks;

/// <summary>
/// Broken input against clean (issue 235, spec §8): each .motion file's first mutant the fast pass
/// rejects, against that file unchanged. The broken parse runs both passes.
/// </summary>
[MemoryDiagnoser]
public class RecoveryParseBenchmarks
{
    string[] _clean = [];
    string[] _broken = [];

    [GlobalSetup]
    public void Setup()
    {
        var clean = new List<string>();
        var broken = new List<string>();
        foreach (string path in MotionCorpus.Files())
        {
            string text = File.ReadAllText(path);
            foreach (var mutant in Mutator.Mutants(path, text, 3))
            {
                using var result = NitrogenMotionParser.Language.Parse(mutant.Text, MotionModule.File);
                if (result.Success) continue;
                clean.Add(text);
                broken.Add(mutant.Text);
                break;
            }
        }
        if (broken.Count == 0) throw new InvalidOperationException($"no .motion corpus under {MotionCorpus.Root}");
        _clean = clean.ToArray();
        _broken = broken.ToArray();
    }

    [Benchmark(Baseline = true)]
    public int Clean() => Parse(_clean);

    [Benchmark]
    public int Broken() => Parse(_broken);

    static int Parse(string[] texts)
    {
        int nodes = 0;
        foreach (string text in texts)
        {
            using var result = NitrogenMotionParser.Language.Parse(text, MotionModule.File);
            nodes += result.Stats.TreeNodes;
        }
        return nodes;
    }
}
