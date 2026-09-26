using BenchmarkDotNet.Attributes;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;

namespace Nitrogen.Benchmarks;

/// <summary>
/// The whole .motion corpus per operation. HandWritten is MotionLexer + MotionParser (tokens and
/// AST). NitrogenTree stops at the syntax tree; NitrogenAst adds the mapping to the same AST.
/// </summary>
[MemoryDiagnoser]
public class MotionParseBenchmarks
{
    string[] _sources = [];

    [GlobalSetup]
    public void Setup()
    {
        _sources = MotionCorpus.Files().Select(File.ReadAllText).ToArray();
        if (_sources.Length == 0) throw new InvalidOperationException($"no .motion corpus under {MotionCorpus.Root}");
    }

    [Benchmark(Baseline = true)]
    public int HandWritten()
    {
        int blocks = 0;
        foreach (string source in _sources)
            blocks += new MotionParser(new MotionLexer(source).Tokenize()).ParseFile().Blocks.Count;
        return blocks;
    }

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

    [Benchmark]
    public int NitrogenAst()
    {
        int blocks = 0;
        foreach (string source in _sources)
            blocks += NitrogenMotionParser.ParseFile(source).Blocks.Count;
        return blocks;
    }
}
