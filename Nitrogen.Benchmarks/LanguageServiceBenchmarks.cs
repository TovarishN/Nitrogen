using BenchmarkDotNet.Attributes;
using Nitrogen.Binding;
using Nitrogen.LanguageService;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Semantics;

namespace Nitrogen.Benchmarks;

/// <summary>
/// The editor's latency on the largest .skill (issue 238, spec §5): a change (re-parse, re-bind,
/// diagnostics, typing included since issue 239) ≤ 15 ms, semantic tokens ≤ 5 ms, a completion (with its
/// prefix parse) ≤ 10 ms; typing a file from nothing ≤ 2 ms (issue 239).
/// </summary>
[MemoryDiagnoser]
public class LanguageServiceBenchmarks
{
    NitrogenLanguageService _service = null!;
    string _uri = "", _text = "";
    DocumentPosition _completionAt;
    int _version = 1;
    ParseResult _parsed = null!;
    Project _project = null!;
    string _path = "";

    [GlobalSetup]
    public void Setup()
    {
        string path = MotionCorpus.SkillFiles().OrderByDescending(f => new FileInfo(f).Length).First();
        _text = File.ReadAllText(path);
        _uri = new Uri(path).AbsoluteUri;
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("motion", NitrogenMotionParser.Language, new Dictionary<string, Rule> { [".skill"] = MotionModule.File }));
        _service = new NitrogenLanguageService(registry);
        _service.Open(_uri, _version, _text);
        int easing = _text.LastIndexOf("easing ", StringComparison.Ordinal);
        _completionAt = new LineMap(_text).PositionOf(easing < 0 ? _text.Length / 2 : easing + "easing ".Length);
        _path = path;
        _parsed = NitrogenMotionParser.Language.Parse(_text, MotionModule.File);
        _project = new Project(NitrogenMotionParser.Language);
        _project.Set(_path, _parsed.Tree);
        Console.WriteLine($"// {Path.GetFileName(path)}: {_text.Length} chars; completion at {_completionAt}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _service.Dispose();
        _parsed.Dispose();
    }

    [Benchmark]
    public int Change()
    {
        _service.Change(_uri, ++_version, _text);
        return _service.Diagnostics(_uri).Count;
    }

    [Benchmark]
    public int Tokens() => _service.SemanticTokens(_uri).Count;

    [Benchmark]
    public int Completion() => _service.Completion(_uri, _completionAt).Count;

    /// <summary>Every property a check needs and every check, from nothing: a change's worst case (spec §6: ≤ 2 ms).</summary>
    [Benchmark]
    public int Semantics() => new ProjectSemantics(_project)[_path].Diagnostics().Count;
}
