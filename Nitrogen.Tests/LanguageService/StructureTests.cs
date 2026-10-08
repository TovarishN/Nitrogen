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

    [Fact]
    public void Selection_grows_through_the_enclosing_syntax()
    {
        using var service = Service();
        const string text = "let sprint = 2 weeks;\nweekday(2026-10-05 + 3 * sprint);";
        service.Open(Uri("a.datecalc"), 1, text);
        var lines = new LineMap(text);

        var steps = Assert.Single(service.SelectionRanges(Uri("a.datecalc"), [lines.PositionOf(text.IndexOf('3'))]));
        string[] texts = steps.Select(r => text[lines.OffsetOf(r.Start)..lines.OffsetOf(r.End)]).ToArray();

        Assert.Equal(["3", "3 * sprint", "2026-10-05 + 3 * sprint", "weekday(2026-10-05 + 3 * sprint)", "weekday(2026-10-05 + 3 * sprint);"], texts[..5]);
        Assert.Equal(text, texts[^1]);
        Assert.All(texts.Zip(texts.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void Each_position_gets_its_own_steps()
    {
        using var service = Service();
        const string text = "1 + 2;\nmax(3, 4);";
        service.Open(Uri("a.datecalc"), 1, text);

        var result = service.SelectionRanges(Uri("a.datecalc"), [new DocumentPosition(0, 0), new DocumentPosition(1, 4)]);

        Assert.Equal(2, result.Count);
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 1)), result[0][0]);
        Assert.Equal(new DocumentRange(new DocumentPosition(1, 4), new DocumentPosition(1, 5)), result[1][0]);
    }

    [Fact]
    public void A_csharp_file_gets_no_selection_steps_from_nitrogen()
    {
        using var service = Service();
        service.Open(Uri("C.cs"), 1, "const string D = /*lang=datecalc*/ \"1 + 1;\";");
        Assert.All(service.SelectionRanges(Uri("C.cs"), [new DocumentPosition(0, 37)]), Assert.Empty);
    }
}
