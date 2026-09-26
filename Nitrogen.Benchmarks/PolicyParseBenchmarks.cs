using BenchmarkDotNet.Attributes;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;

namespace Nitrogen.Benchmarks;

/// <summary>The whole .policy + .compose corpus per operation; same three variants as MotionParseBenchmarks.</summary>
[MemoryDiagnoser]
public class PolicyParseBenchmarks
{
    string[] _policies = [];
    string[] _composes = [];

    [GlobalSetup]
    public void Setup()
    {
        _policies = MotionCorpus.PolicyFiles().Select(File.ReadAllText).ToArray();
        _composes = MotionCorpus.ComposeFiles().Select(File.ReadAllText).ToArray();
        if (_policies.Length == 0) throw new InvalidOperationException($"no .policy corpus under {MotionCorpus.Root}");
    }

    [Benchmark(Baseline = true)]
    public int HandWritten()
    {
        int goals = 0;
        foreach (string source in _policies)
            goals += new PolicyParser(new MotionLexer(source).Tokenize()).ParsePolicy().Goals.Count;
        foreach (string source in _composes)
            goals += new PolicyParser(new MotionLexer(source).Tokenize()).ParseCompose().Getups.Count;
        return goals;
    }

    [Benchmark]
    public int NitrogenTree()
    {
        int nodes = 0;
        foreach (string source in _policies)
        {
            using var result = NitrogenPolicyParser.Language.Parse(source, PolicyModule.PolicyDocument);
            nodes += result.Stats.TreeNodes;
        }
        foreach (string source in _composes)
        {
            using var result = NitrogenPolicyParser.Language.Parse(source, PolicyModule.ComposeDocument);
            nodes += result.Stats.TreeNodes;
        }
        return nodes;
    }

    [Benchmark]
    public int NitrogenAst()
    {
        int goals = 0;
        foreach (string source in _policies) goals += NitrogenPolicyParser.ParsePolicy(source).Goals.Count;
        foreach (string source in _composes) goals += NitrogenPolicyParser.ParseCompose(source).Getups.Count;
        return goals;
    }
}
