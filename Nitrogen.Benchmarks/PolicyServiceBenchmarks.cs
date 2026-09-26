using BenchmarkDotNet.Attributes;
using Nitrogen.Binding;
using Nitrogen.LanguageService;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.Semantics;

namespace Nitrogen.Benchmarks;

/// <summary>
/// The editor's latency on the largest .policy (issue 241, spec §5): a change (re-parse, re-bind,
/// every PV check) ≤ 15 ms; all semantics from nothing ≤ 2 ms.
/// </summary>
[MemoryDiagnoser]
public class PolicyServiceBenchmarks
{
    NitrogenLanguageService _service = null!;
    string _uri = "", _text = "", _path = "";
    int _version = 1;
    ParseResult _parsed = null!;
    Project _project = null!;

    [GlobalSetup]
    public void Setup()
    {
        _path = MotionCorpus.PolicyFiles().OrderByDescending(f => new FileInfo(f).Length).First();
        _text = File.ReadAllText(_path);
        _uri = new Uri(_path).AbsoluteUri;
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("policy", NitrogenPolicyParser.Language, new Dictionary<string, Rule> { [".policy"] = PolicyModule.PolicyDocument }));
        _service = new NitrogenLanguageService(registry);
        _service.Open(_uri, _version, _text);
        _parsed = NitrogenPolicyParser.Language.Parse(_text, PolicyModule.PolicyDocument);
        _project = new Project(NitrogenPolicyParser.Language);
        _project.Set(_path, _parsed.Tree);
        Console.WriteLine($"// {Path.GetFileName(_path)}: {_text.Length} chars");
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

    /// <summary>Every property a check needs and every check, from nothing.</summary>
    [Benchmark]
    public int Semantics() => new ProjectSemantics(_project)[_path].Diagnostics().Count;
}
