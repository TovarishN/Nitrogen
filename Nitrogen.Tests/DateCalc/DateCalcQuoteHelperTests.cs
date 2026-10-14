using System.Collections;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DateCalcQuoteHelperTests
{
    [Fact]
    public void The_shipped_DateCalc_example_uses_a_typed_quote_helper()
    {
        string grammar = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage", "DateCalc.ngr"));
        Assert.Contains("syntax function FortnightDuration(Count: Expr) : Expr = <[ $Count * 2 weeks ]>;", grammar);
        Assert.Contains("rewrites FortnightDuration(Count)", grammar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_or_shared_helper_preserves_DateCalc_results(bool shared)
    {
        var workspace = new GrammarWorkspace { GeneratedNamespace = "DateCalc.Syntax", CompositionName = "DateCalc", StartRule = "DateCalc.Program" };
        string directory = Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage");
        string grammar = File.ReadAllText(Path.Combine(directory, "DateCalc.ngr"));
        grammar = grammar.Replace("  syntax function FortnightDuration(Count: Expr) : Expr = <[ $Count * 2 weeks ]>;", "")
            .Replace("rewrites FortnightDuration(Count)", "rewrites <[ $Count * 2 weeks ]>");
        const string helper = "syntax function FortnightDuration(Value: DateCalc.Expr) : DateCalc.Expr = <[ $Value * 2 weeks ]>;";
        grammar = grammar.Replace("rewrites <[ $Count * 2 weeks ]>", "rewrites FortnightDuration(Count)");
        if (shared)
        {
            grammar = grammar.Replace("symbols {", "using Shared;\n  symbols {");
            workspace.SetGrammar("Shared.ngr", "syntax module Shared { " + helper + " }");
        }
        else grammar = grammar.Replace("  extensible syntax Expr", "  " + helper + "\n  extensible syntax Expr");
        workspace.SetGrammar("DateCalc.ngr", grammar);
        foreach (string file in Directory.GetFiles(directory, "*.cs")) workspace.Sources[file] = File.ReadAllText(file);
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        const string sample = "let count = 3;\ncount fortnights;\n(1 + 2) fortnights;\n2026-10-05 + 2 fortnights;";
        var evaluator = snapshot.Language!.Modules[0].GetType().Assembly.GetType("DateCalc.Syntax.DateCalcEvaluator", true)!;
        var result = (IEnumerable)evaluator.GetMethod("Run")!.Invoke(null, new object?[] { sample, null })!;
        var lines = result.Cast<object>().Select(line =>
        {
            Assert.False((bool)line.GetType().GetProperty("IsError")!.GetValue(line)!);
            return (string)line.GetType().GetProperty("Text")!.GetValue(line)!;
        }).ToArray();
        Assert.Equal(new[] { "3", "42 days", "42 days", "2026-11-02 Mon" }, lines);
    }
    [Fact]
    public void An_expansion_error_retains_the_helper_definition_as_related_source()
    {
        string root = Directory.CreateTempSubdirectory("datecalc-helper-origin-").FullName;
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage");
            foreach (string file in Directory.GetFiles(directory)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            string path = Path.Combine(root, "DateCalc.ngr");
            string grammar = File.ReadAllText(path).Replace("  syntax function FortnightDuration(Count: Expr) : Expr = <[ $Count * 2 weeks ]>;", "")
                .Replace("rewrites FortnightDuration(Count)", "rewrites <[ $Count * 2 weeks ]>").Replace("  extensible syntax Expr",
                "  syntax function Duration(Value:Expr):Expr=<[ $Value * 2 weeks ]>;\n  extensible syntax Expr")
                .Replace("rewrites <[ $Count * 2 weeks ]>", "rewrites Duration(Count)");
            File.WriteAllText(path, grammar);
            using var service = new Nitrogen.LanguageService.NitrogenLanguageService(Nitrogen.Cli.LspCommand.Registry());
            service.ConfigureWorkspace(root);
            string uri = new Uri(Path.Combine(root, "test.datecalc")).AbsoluteUri;
            service.Open(uri, 1, "2026-10-05 fortnights;");
            var diagnostic = Assert.Single(service.Diagnostics(uri), d => d.Code == "DC0002");
            Assert.NotNull(diagnostic.Related);
            Assert.Contains(diagnostic.Related!, location => location.Uri == new Uri(path).AbsoluteUri);
        }
        finally { Directory.Delete(root, true); }
    }
}
