using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Folding and expand-selection from the syntax tree.</summary>
public sealed class StructureTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-structure-").FullName;

    public StructureTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        return service;
    }

    IReadOnlyList<ServiceFoldingRange> Folds(NitrogenLanguageService service, string name, string text)
    {
        service.Open(Uri(name), 1, text);
        return service.FoldingRanges(Uri(name));
    }

    [Fact]
    public void A_call_split_over_two_lines_folds()
    {
        using var service = Service();
        Assert.Equal([new ServiceFoldingRange(0, 1, false)], Folds(service, "a.datecalc", "max(2026-10-05,\n  2026-12-25);"));
    }

    [Fact]
    public void A_grammar_folds_its_module_and_blocks_leaving_closers_visible()
    {
        using var service = Service();
        string grammar = File.ReadAllText(Path.Combine(_root, "DateCalc.ngr"));
        string[] lines = grammar.Split('\n');
        var folds = Folds(service, "DateCalc.ngr", grammar);

        int module = Array.FindIndex(lines, l => l.StartsWith("syntax module DateCalc", StringComparison.Ordinal));
        Assert.Contains(new ServiceFoldingRange(module, lines.Length - 3, false), folds); // ends before the closing "}" (then a final empty line)
        int day = Array.FindIndex(lines, l => l.Contains("| Day ", StringComparison.Ordinal));
        Assert.Contains(new ServiceFoldingRange(day + 1, day + 3, false), folds);          // the { … } semantics block, its "}" visible
        Assert.Contains(new ServiceFoldingRange(0, 3, true), folds);                       // the four leading comment lines
        Assert.All(folds, f => Assert.True(f.EndLine > f.StartLine));
        Assert.Equal(folds.Count, folds.Select(f => f.StartLine).Distinct().Count());       // one fold per start line
    }

    [Fact]
    public void Two_comment_lines_fold_and_one_does_not()
    {
        using var service = Service();
        Assert.Contains(new ServiceFoldingRange(0, 1, true), Folds(service, "a.datecalc", "// one\n// two\n1 + 1;"));
        Assert.DoesNotContain(Folds(service, "b.datecalc", "// one\n1 + 1;"), f => f.IsComment);
    }

    [Fact]
    public void A_csharp_file_gets_no_folds_from_nitrogen()
    {
        using var service = Service();
        Assert.Empty(Folds(service, "C.cs", "class C\n{\n    const string D = /*lang=datecalc*/ \"\"\"\n        1 + 1;\n        2 + 2;\n        \"\"\";\n}\n"));
    }
}
