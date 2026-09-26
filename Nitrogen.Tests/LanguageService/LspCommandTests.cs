using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.MotionDsl;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

/// <summary>The languages `nitrogen lsp` serves (issue 238).</summary>
public class LspCommandTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("file:///x/a.motion", "motion")]
    [InlineData("file:///x/a.skill", "motion")]
    [InlineData("file:///x/a.policy", "policy")]
    [InlineData("file:///x/a.compose", "policy")]
    [InlineData("file:///x/Motion.ngr", "ngr")]
    public void Each_file_type_has_its_language(string uri, string language)
    {
        Assert.True(LspCommand.Registry().TryFind(uri, out var entry, out _));
        Assert.Equal(language, entry.Name);
    }

    [Fact]
    public void Every_corpus_file_and_grammar_is_served_without_diagnostics()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        var files = ServedFiles();
        foreach (string path in files) Assert.NotEmpty(service.Open(new Uri(path).AbsoluteUri, 1, File.ReadAllText(path)));

        var failures = files.Select(p => new Uri(p).AbsoluteUri)
            .SelectMany(uri => service.Diagnostics(uri).Select(d => $"{uri}:{d.Range.Start.Line + 1}: {d.Code} {d.Message}"))
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    static List<string> ServedFiles() =>
        MotionCorpus.Files().Concat(MotionCorpus.SkillFiles()).Concat(MotionCorpus.PolicyFiles()).Concat(MotionCorpus.ComposeFiles())
            .Concat(new[] { "Motion.ngr", "Policy.ngr" }.Select(g => Path.Combine(Here(), "..", "..", "Nitrogen.MotionDsl", g)))
            .Append(Path.Combine(Here(), "..", "..", "Nitrogen.Ngr", "Nitrogen.ngr"))
            .Select(Path.GetFullPath)
            .ToList();

    [Fact]
    public void Every_served_file_has_ordered_tokens_and_an_outline_of_every_declaration()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        var failures = new List<string>();
        foreach (string path in ServedFiles())
        {
            string uri = new Uri(path).AbsoluteUri;
            service.Open(uri, 1, File.ReadAllText(path));
            var tokens = service.SemanticTokens(uri);
            if (tokens.Count == 0) failures.Add($"{uri}: no tokens");
            for (int i = 1; i < tokens.Count; i++)
            {
                var (a, b) = (tokens[i - 1], tokens[i]);
                if (b.Start.Line < a.Start.Line || (b.Start.Line == a.Start.Line && b.Start.Character < a.Start.Character + a.Length))
                    failures.Add($"{uri}: tokens overlap or are out of order at {b.Start}");
            }
            service.TryGet(uri, out var document);
            int declarations = service.ProjectOf(document)[uri].Declarations.Count;
            int outlined = Count(service.DocumentSymbols(uri));
            if (outlined != declarations) failures.Add($"{uri}: outline has {outlined} of {declarations} declarations");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void A_grammar_outline_lists_its_module_and_rules()
    {
        string path = Path.GetFullPath(Path.Combine(Here(), "..", "..", "Nitrogen.MotionDsl", "Motion.ngr"));
        string text = File.ReadAllText(path);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open(new Uri(path).AbsoluteUri, 1, text);
        var module = Assert.Single(service.DocumentSymbols(new Uri(path).AbsoluteUri));
        Assert.Equal("Motion", module.Name);
        Assert.Equal(OutlineKind.Module, module.Outline);
        var parsed = Nitrogen.Grammar.GrammarParser.Parse(text).File!.Modules[0];
        // Rules, and the symbol properties a module declares beside them (issue 239).
        Assert.Equal(parsed.Rules.Count + parsed.SymbolProperties.Count, module.Children.Count);
    }

    [Fact]
    public void Motion_names_are_coloured_by_kind()
    {
        const string text = "skill probe {\n lifecycle continuous\n requires motors {\n  biped.knee\n }\n source p: phases {\n  phase a {\n   pose {\n    biped.knee = rest\n   }\n  }\n }\n output blend {\n  p priority 1\n }\n}\n";
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open("file:///w/a.skill", 1, text);
        var tokens = service.SemanticTokens("file:///w/a.skill");
        Assert.Contains(tokens, t => t.Start == new DocumentPosition(3, 2) && t.Length == 10 && t.Type == TokenType.EnumMember && t.Modifiers == TokenModifiers.Declaration);
        Assert.Contains(tokens, t => t.Start == new DocumentPosition(8, 4) && t.Length == 10 && t.Type == TokenType.EnumMember && t.Modifiers == TokenModifiers.None);
        Assert.Contains(tokens, t => t.Start == new DocumentPosition(0, 6) && t.Length == 5 && t.Type == TokenType.Class); // `s` would be the seconds unit, a keyword
    }

    [Fact]
    public void Every_effective_reference_has_a_definition_or_is_builtin()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        var failures = new List<string>();
        foreach (string path in ServedFiles())
        {
            string uri = new Uri(path).AbsoluteUri;
            service.Open(uri, 1, File.ReadAllText(path));
            service.TryGet(uri, out var document);
            var project = service.ProjectOf(document);
            foreach (var reference in project[uri].References.Where(project.IsEffective))
            {
                var resolved = project.Resolve(reference);
                if (resolved.Count == 0) continue; // an optional .ngr reference to another module
                var at = document.Lines.PositionOf(reference.NameSpan.Start);
                int expected = resolved.Count(s => !s.IsBuiltin);
                if (service.Definition(uri, at).Count != expected) failures.Add($"{uri}:{at.Line + 1}: {reference.Name}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void Renaming_sampled_declarations_keeps_every_file_clean()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        var failures = new List<string>();
        int renamed = 0, namedByBuiltins = 0;
        foreach (string path in ServedFiles())
        {
            string uri = new Uri(path).AbsoluteUri, text = File.ReadAllText(path);
            service.Open(uri, 1, text);
            service.TryGet(uri, out var document);
            foreach (var declaration in BindingOracle.Sample(service.ProjectOf(document)[uri].Declarations, 3).ToList())
            {
                var at = document.Lines.PositionOf(declaration.NameSpan.Start);
                var project = service.ProjectOf(document);
                // A policy channel both declares itself and names its built-in goal channel: renaming it must be refused.
                if (project[uri].References.Any(r => r.NameSpan == declaration.NameSpan && project.Resolve(r).Any(s => s.IsBuiltin)))
                {
                    namedByBuiltins++;
                    var refusal = Assert.Throws<RenameRefusedException>(() => service.Rename(uri, at, declaration.Name + "_r"));
                    Assert.Contains("built in", refusal.Message);
                    continue;
                }
                IReadOnlyDictionary<string, IReadOnlyList<DocumentEdit>> edits;
                try
                {
                    edits = service.Rename(uri, at, declaration.Name + "_r");
                }
                catch (RenameRefusedException refusal)
                {
                    failures.Add($"{uri}:{at.Line + 1}: {declaration.Name}: {refusal.Message}");
                    continue;
                }
                renamed++;
                service.Change(uri, 2, RenameTests.Apply(text, edits[uri]));
                failures.AddRange(service.Diagnostics(uri).Select(d => $"{uri}:{d.Range.Start.Line + 1}: after renaming {declaration.Name}: {d.Code} {d.Message}"));
                service.Change(uri, 3, text);
                service.TryGet(uri, out document);
            }
        }
        output.WriteLine($"{renamed} renames applied, {namedByBuiltins} refused as named by a built-in");
        Assert.True(renamed > 100, $"only {renamed} renames");
        Assert.True(namedByBuiltins > 0, "no declaration named by a built-in was sampled");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    static int Count(IEnumerable<OutlineSymbol> symbols) => symbols.Sum(s => 1 + Count(s.Children));

    static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
