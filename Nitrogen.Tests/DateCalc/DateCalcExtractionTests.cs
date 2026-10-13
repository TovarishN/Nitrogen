using System.Collections;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DateCalcExtractionTests
{
    [Fact]
    public void DateCalc_factory_preserves_evaluation_after_token_extraction()
    {
        string root = Directory.CreateTempSubdirectory("datecalc-extraction-").FullName;
        try
        {
            foreach (string name in new[] { "DateCalc.ngr", "nitrogen.json", "DateCalcLanguage.cs", "MathModule.cs", "DateCalcEvaluator.cs", "DateCalcFixes.cs" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage", name), Path.Combine(root, name));
            string grammarPath = Path.Combine(root, "DateCalc.ngr");
            string grammar = File.ReadAllText(grammarPath);
            string uri = new Uri(grammarPath).AbsoluteUri;
            using var service = new NitrogenLanguageService(LspCommand.Registry());
            service.ConfigureWorkspace(root);
            service.Open(uri, 1, grammar);
            const string selected = "token Number     = Digit+ (\".\" Digit+)?;";
            int at = grammar.IndexOf(selected, StringComparison.Ordinal);
            Assert.True(at >= 0);
            var map = new LineMap(grammar);
            var preparation = service.PrepareExtraction(uri, new(map.PositionOf(at), map.PositionOf(at + selected.Length)));
            Assert.Null(preparation.UnavailableReason);
            var proposal = service.ResolveExtraction(new(preparation.Id!, new Uri(Path.Combine(root, "Shared.ngr")).AbsoluteUri, "Shared"));
            Assert.True(proposal.UnavailableReason is null, proposal.UnavailableReason);
            using var before = Compile(root);
            Assert.Equal(new[] { "3", "42 days", "42 days", "2026-11-02 Mon" }, Evaluate(before));
            foreach (var change in proposal.Changes)
                File.WriteAllText(new Uri(change.Uri).LocalPath, RenameTests.Apply(change.OriginalText, change.Edits));
            File.WriteAllText(new Uri(proposal.DestinationUri).LocalPath, proposal.DestinationText);
            using var after = Compile(root);
            Assert.Equal(Evaluate(before), Evaluate(after));
            Assert.Equal(new[] { "DateCalc", "Shared" }, after.Language!.Modules.Select(m => m.Name));
        }
        finally { Directory.Delete(root, true); }
    }

    static WorkspaceSnapshot Compile(string root)
    {
        var workspace = new GrammarWorkspace { GeneratedNamespace = "DateCalc.Syntax", CompositionName = "DateCalc", StartRule = "DateCalc.Program" };
        foreach (string file in Directory.GetFiles(root, "*.ngr")) workspace.SetGrammar(file, File.ReadAllText(file));
        foreach (string file in Directory.GetFiles(root, "*.cs")) workspace.Sources[file] = File.ReadAllText(file);
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    static string[] Evaluate(WorkspaceSnapshot snapshot)
    {
        const string sample = "let count = 3;\ncount fortnights;\n(1 + 2) fortnights;\n2026-10-05 + 2 fortnights;";
        var evaluator = snapshot.Language!.Modules[0].GetType().Assembly.GetType("DateCalc.Syntax.DateCalcEvaluator", true)!;
        var result = (IEnumerable)evaluator.GetMethod("Run")!.Invoke(null, new object?[] { sample, null })!;
        return result.Cast<object>().Select(line =>
        {
            Assert.False((bool)line.GetType().GetProperty("IsError")!.GetValue(line)!);
            return (string)line.GetType().GetProperty("Text")!.GetValue(line)!;
        }).ToArray();
    }
}
