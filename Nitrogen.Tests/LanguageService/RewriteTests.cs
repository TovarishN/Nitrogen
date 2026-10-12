using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Semantic;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Uses of rewrite rules in a DateCalc grammar extended with them (spec: quasiquote rewrites).</summary>
public sealed class RewriteTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-rewrites-").FullName;

    internal const string Alternatives = """
            | NextDay    = Date:Expr "next" "day" precedence 30  rewrites <[ $Date + 1 days ]>
            | Doubled    = Value:Expr "doubled" precedence 30  rewrites <[ $Value + $Value ]>
            | Monthish   = Count:Expr "monthish" precedence 30  rewrites <[ $Count * 2 fortnights ]>
            | Loops      = Count:Expr "loops" precedence 30  rewrites <[ $Count loops ]>
            | Mystery    = Value:Expr "mystery" precedence 30  rewrites <[ $Value + nowhere ]>
            | Same       = Value:Expr "same" precedence 30  rewrites <[ $Value ]>

        """;

    public RewriteTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        string grammar = Path.Combine(_root, "DateCalc.ngr");
        string text = File.ReadAllText(grammar);
        int days = text.IndexOf("    | Days    =", StringComparison.Ordinal);
        File.WriteAllText(grammar, text.Insert(days, Alternatives));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        return service;
    }

    /// <summary>A lowered node without origins: operations, constants and the rest by type.</summary>
    static string Shape(HirNode node) => node switch
    {
        HirOperation operation => $"{operation.Signature.Id}({string.Join(", ", operation.Arguments.Select(Shape))})",
        HirConstant constant => $"{constant.Value}:{constant.Type}",
        _ => $"{node.GetType().Name}:{node.Type}",
    };

    string Lowered(NitrogenLanguageService service, string name, string text)
    {
        service.Open(Uri(name), 1, text);
        var inspection = service.InspectDocument(Uri(name))!;
        Assert.DoesNotContain(inspection.Diagnostics, d => d.Code is not ("NH0001" or "NH0002" or "NH0003"));
        return Shape(Assert.Single(inspection.Roots));
    }

    [Fact]
    public void A_use_lowers_as_its_expansion()
    {
        using var service = Service();
        Assert.Equal(Lowered(service, "b.datecalc", "3 * 2 weeks;"), Lowered(service, "a.datecalc", "3 fortnights;"));
        Assert.Empty(service.Diagnostics(Uri("a.datecalc")));
    }

    [Fact]
    public void A_use_has_its_expansions_type_and_passes_it_on()
    {
        using var service = Service();
        const string text = "let x = 3 fortnights;\nx;";
        service.Open(Uri("a.datecalc"), 1, text);
        Assert.Empty(service.Diagnostics(Uri("a.datecalc")));
        Assert.Contains("DateCalc.Duration", service.Hover(Uri("a.datecalc"), new DocumentPosition(1, 0))!.Markdown);
    }

    [Fact]
    public void A_use_shows_its_value()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "2026-10-05 next day;");
        var whole = new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(1, 0));
        Assert.Equal("= 2026-10-06 Tue", Assert.Single(service.ValueHints(Uri("a.datecalc"), whole)).Label);
    }

    [Fact]
    public void An_error_in_the_expansion_is_reported_at_the_use()
    {
        using var service = Service();
        const string text = "3 next day;";
        service.Open(Uri("a.datecalc"), 1, text);
        var error = Assert.Single(service.Diagnostics(Uri("a.datecalc")), d => d.Severity == ServiceSeverity.Error);
        Assert.Equal("DC0002", error.Code);
        Assert.StartsWith("in the expansion of", error.Message);
        Assert.Contains("NextDay", error.Message);
        Assert.Equal(new DocumentPosition(0, 0), error.Range.Start);
    }

    [Fact]
    public void An_error_in_a_child_stays_at_the_child()
    {
        using var service = Service();
        const string text = "2026-02-30 next day;";
        service.Open(Uri("a.datecalc"), 1, text);
        var error = Assert.Single(service.Diagnostics(Uri("a.datecalc")), d => d.Severity == ServiceSeverity.Error);
        Assert.Equal("DC0001", error.Code);
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 10)), error.Range);
    }

    [Fact]
    public void A_hole_used_twice_stands_for_the_same_child()
    {
        using var service = Service();
        Assert.Equal(Lowered(service, "b.datecalc", "(1 + 2) + (1 + 2);"), Lowered(service, "a.datecalc", "(1 + 2) doubled;"));
    }

    [Fact]
    public void A_rewrite_using_another_expands_both()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "1 monthish;\n1 * (2 * 2 weeks);");
        var whole = new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(2, 0));
        var hints = service.ValueHints(Uri("a.datecalc"), whole);
        Assert.Equal(2, hints.Count);
        Assert.Equal(hints[1].Label, hints[0].Label);
    }

    [Fact]
    public void A_cycle_is_reported_and_stops()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "1 loops;");
        Assert.Contains(service.InspectDocument(Uri("a.datecalc"))!.Diagnostics, d => d.Code == "NH0008");
        var cycle = Assert.Single(service.Diagnostics(Uri("a.datecalc")), d => d.Code == "NH0008");
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 7)), cycle.Range);
        Assert.Equal("in the expansion of Loops: A rewrite that reaches itself prevents lowering.", cycle.Message);
    }

    [Fact]
    public void A_lowering_problem_in_the_expansion_is_reported_at_the_use()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "1 mystery;");
        var error = Assert.Single(service.Diagnostics(Uri("a.datecalc")), d => d.Severity == ServiceSeverity.Error);
        Assert.Equal("NB0001", error.Code);
        Assert.Equal("in the expansion of Mystery: unresolved value 'nowhere'", error.Message);
        Assert.Equal(new DocumentRange(new DocumentPosition(0, 0), new DocumentPosition(0, 9)), error.Range);
        Assert.Empty(service.InspectDocument(Uri("a.datecalc"))!.Roots);
        Assert.All(service.InspectDocument(Uri("a.datecalc"))!.Diagnostics, d => Assert.Equal(Uri("a.datecalc"), d.Origin.Path));
    }

    [Fact]
    public void A_template_that_is_a_hole_lowers_as_the_child_itself()
    {
        using var service = Service();
        service.Open(Uri("a.datecalc"), 1, "(1 + 2) same;");
        var root = Assert.Single(service.InspectDocument(Uri("a.datecalc"))!.Roots);
        Assert.Equal(new TextSpan(1, 5), root.Origins[0].Span); // the sum's own, not the use's
        Assert.DoesNotContain(root.Origins, o => o.Span == new TextSpan(0, 12));
    }

    void AddRules(string alternatives, string rules = "", string statement = "Setup")
    {
        string grammar = Path.Combine(_root, "DateCalc.ngr");
        string text = File.ReadAllText(grammar);
        int days = text.IndexOf("    | Days    =", StringComparison.Ordinal);
        text = text.Insert(days, alternatives);
        if (rules.Length > 0)
        {
            text = text.Replace("syntax Statement = Let / Show;", $"syntax Statement = Let / Show / {statement};");
            int program = text.IndexOf("  syntax Program", StringComparison.Ordinal);
            text = text.Insert(program, rules);
        }
        File.WriteAllText(grammar, text);
    }

    [Fact]
    public void A_template_that_does_not_parse_warns_and_its_uses_report()
    {
        AddRules("    | Broken = Count:Expr \"broken\" precedence 30  rewrites <[ $Count * ]>\n");
        using var service = Service();
        string grammarText = File.ReadAllText(Path.Combine(_root, "DateCalc.ngr"));
        service.Open(Uri("DateCalc.ngr"), 1, grammarText);
        var warning = Assert.Single(service.Diagnostics(Uri("DateCalc.ngr")), d => d.Code == "NGR0137");
        Assert.Equal(ServiceSeverity.Warning, warning.Severity);
        int broken = Array.FindIndex(grammarText.Split('\n'), l => l.Contains("| Broken", StringComparison.Ordinal));
        Assert.True(broken >= 0);
        Assert.Equal(broken, warning.Range.Start.Line);
        Assert.Contains("expected", warning.Message);

        service.Open(Uri("a.datecalc"), 1, "1 broken;\n2 fortnights;");
        Assert.Contains(service.InspectDocument(Uri("a.datecalc"))!.Diagnostics, d => d.Code == "NH0009");
        Assert.DoesNotContain(service.Diagnostics(Uri("a.datecalc")), d => d.Range.Start.Line == 1); // the language still works
    }

    [Fact]
    public void A_template_that_declares_a_name_warns()
    {
        AddRules("", "  syntax Setup = \"setup\" \";\"  rewrites <[ let tmp = 1; ]>;\n");
        using var service = Service();
        service.Open(Uri("DateCalc.ngr"), 1, File.ReadAllText(Path.Combine(_root, "DateCalc.ngr")));
        Assert.Contains(service.Diagnostics(Uri("DateCalc.ngr")), d => d.Code == "NGR0138" && d.Severity == ServiceSeverity.Warning);
    }

    [Fact]
    public void Hover_on_a_use_shows_its_template()
    {
        using var service = Service();
        const string text = "3 fortnights;";
        service.Open(Uri("a.datecalc"), 1, text);
        var hover = service.Hover(Uri("a.datecalc"), new DocumentPosition(0, text.IndexOf("fortnights", StringComparison.Ordinal) + 2))!;
        Assert.Contains("```\nrewrites <[ $Count * 2 weeks ]>\n```", hover.Markdown);
        Assert.DoesNotContain("rewrites", service.Hover(Uri("a.datecalc"), new DocumentPosition(0, 0))?.Markdown ?? ""); // on the child
    }

    [Fact]
    public void A_statement_use_lowers_as_its_expansion()
    {
        AddRules("", "  syntax Twice = \"twice\" Value:Expr \";\"  rewrites <[ $Value + $Value; ]>;\n", statement: "Twice");
        using var service = Service();
        Assert.Equal(Lowered(service, "b.datecalc", "3 + 3;"), Lowered(service, "a.datecalc", "twice 3;"));
        Assert.Empty(service.Diagnostics(Uri("a.datecalc")));
    }

    [Fact]
    public void An_instantiation_is_not_mistaken_for_a_document_named_rewrite()
    {
        var workspace = new Nitrogen.Workspace.GrammarWorkspace { GeneratedNamespace = "DateCalc.Syntax" };
        workspace.SetGrammar("DateCalc.ngr", File.ReadAllText(Path.Combine(_root, "DateCalc.ngr")));
        foreach (string source in new[] { "DateCalcLanguage.cs", "MathModule.cs", "DateCalcEvaluator.cs", "DateCalcFixes.cs" })
            workspace.Sources[source] = File.ReadAllText(Path.Combine(_root, source));
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        const string text = "3 fortnights;"; // shorter than the template, so a template origin taken as this file's overruns it
        using var parsed = snapshot.Parse(text, "DateCalc.Program");
        var project = new Nitrogen.Binding.Project(snapshot.Language!);
        project.Set("rewrite", parsed.Tree);
        var lowered = HirLowering.Lower(new Nitrogen.Semantics.ProjectSemantics(project)["rewrite"], snapshot.Language!.SemanticCatalog);
        Assert.Empty(lowered.Diagnostics);
        var origins = new List<SourceOrigin>();
        void Collect(HirNode node)
        {
            origins.AddRange(node.Origins);
            if (node is HirOperation operation) foreach (var argument in operation.Arguments) Collect(argument);
        }
        Collect(Assert.Single(lowered.Roots));
        Assert.All(origins.Where(o => o.Path == "rewrite"), o => Assert.True(o.Span.End <= text.Length, o.ToString()));
    }
}
