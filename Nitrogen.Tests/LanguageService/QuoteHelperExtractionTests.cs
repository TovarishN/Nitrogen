using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteHelperExtractionTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-quote-extract-").FullName;
    const string Grammar = "syntax module M { syntax Program=Expr \";\"; extensible syntax Expr { | Atom=\"x\" | Wrap=X:Expr \"wrap\" precedence 30 rewrites <[ $X ]> } }";
    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    NitrogenLanguageService Service(string grammar = Grammar)
    {
        File.WriteAllText(Path.Combine(_root, "main.ngr"), grammar);
        File.WriteAllText(Path.Combine(_root, "shared.ngr"), "syntax module Shared { }");
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), "{\"languages\":[{\"name\":\"M\",\"extensions\":[\".m\"],\"start\":\"M.Program\",\"grammars\":[\"main.ngr\",\"shared.ngr\"]}]}");
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("main.ngr"), 1, grammar);
        return service;
    }
    QuoteHelperExtractionPreparation Prepare(NitrogenLanguageService service, int length = 0)
    {
        int at = Grammar.IndexOf("<[", StringComparison.Ordinal);
        var lines = new LineMap(Grammar);
        return service.PrepareQuoteHelperExtraction(Uri("main.ngr"), new(lines.PositionOf(at + (length == 0 ? 3 : 0)), lines.PositionOf(at + (length == 0 ? 3 : length))));
    }

    [Theory]
    [InlineData("main.ngr", "M", true)]
    [InlineData("shared.ngr", "Shared", true)]
    [InlineData("extracted.ngr", "Extracted", false)]
    public void Proposes_a_complete_compilable_helper_without_writing_files(string file, string module, bool existing)
    {
        using var service = Service();
        var prepared = Prepare(service);
        Assert.Null(prepared.UnavailableReason);
        var request = new QuoteHelperExtractionRequest(prepared.Id!, Uri(file), module, "WrapQuote", existing);
        var proposal = service.ResolveQuoteHelperExtraction(request);
        Assert.Null(proposal.UnavailableReason);
        Assert.Equal(!existing, proposal.CreateDestination);
        Assert.Equal(Grammar, File.ReadAllText(Path.Combine(_root, "main.ngr")));
        var files = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllText);
        foreach (var change in proposal.Changes)
            files[new System.Uri(change.Uri).LocalPath] = RenameTests.Apply(change.OriginalText, change.Edits);
        if (proposal.CreateDestination) files[Path.Combine(_root, file)] = proposal.DestinationText;
        Assert.Contains("rewrites WrapQuote(X)", files[Path.Combine(_root, "main.ngr")]);
        Assert.Contains("syntax function WrapQuote(X: M.Expr) : M.Expr = <[ $X ]>;", files[Path.Combine(_root, file)]);
        var workspace = new GrammarWorkspace();
        foreach (var input in files.Where(f => f.Key.EndsWith(".ngr", StringComparison.Ordinal))) workspace.SetGrammar(input.Key, input.Value);
        using var compiled = workspace.Compile();
        Assert.True(compiled.Succeeded, string.Join("\n", compiled.Diagnostics));
        if (!existing) Assert.Contains("extracted.ngr", files[Path.Combine(_root, "nitrogen.json")]);
        service.Open(Uri("shared.ngr"), 7, File.ReadAllText(Path.Combine(_root, "shared.ngr")));
        Assert.Null(service.ResolveQuoteHelperExtraction(request).UnavailableReason);
        service.Change(Uri("main.ngr"), 2, Grammar + "\n");
        Assert.Contains("main.ngr", service.ResolveQuoteHelperExtraction(request).UnavailableReason!);
    }

    [Fact]
    public void Partial_quote_selection_is_refused()
    {
        using var service = Service();
        Assert.Null(Prepare(service, 4).Id);
    }

    [Fact]
    public void New_destinations_cannot_overwrite_and_names_cannot_collide()
    {
        using var service = Service();
        var prepared = Prepare(service);
        Assert.NotNull(service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("shared.ngr"), "Shared", "Quote", false)).UnavailableReason);
        Assert.NotNull(service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("main.ngr"), "M", "Expr", true)).UnavailableReason);
    }

    [Theory]
    [InlineData("syntax module M { syntax Program=Expr \";\"; extensible syntax Expr { | Atom=\"x\" | Wrap=X:Expr \"wrap\" precedence 30 rewrites <[ x ]> } }", "WrapQuote()", "WrapQuote() : M.Expr = <[ x ]>;")]
    [InlineData("syntax module M { syntax Program=Expr; extensible syntax Expr { | Atom=\"x\" | Pair=\"(\" A:Expr \",\" B:Expr \")\" | Wrap=\"wrap\" A:Expr B:Expr \"wrap\" rewrites <[ ( $B, ( $A, $B ) ) ]> } }", "WrapQuote(B, A)", "WrapQuote(B: M.Expr, A: M.Expr)")]
    [InlineData("syntax module M { syntax Program=Expr; extensible syntax Expr { | Atom=\"x\" | Call=\"f(\" Args:(Expr; \",\")* \")\" | Wrap=\"wrap(\" Args:(Expr; \";\")* \")\" rewrites <[ f( ..$Args, ..$Args ) ]> } }", "WrapQuote(Args)", "WrapQuote(Args: M.Expr*)")]
    [InlineData("syntax module M { token String=\"\\\"$Fake\\\"\"; syntax Program=Expr; extensible syntax Expr { | Atom=\"x\" | Str=String | Wrap=X:Expr \"wrap\" precedence 30 rewrites <[ \"$Fake\" ]> } }", "WrapQuote()", "WrapQuote() : M.Expr = <[ \"$Fake\" ]>;")]
    public void Infers_only_actual_holes_in_first_use_order_and_preserves_quote(string grammar, string call, string declaration)
    {
        using var service = Service(grammar);
        int at = grammar.IndexOf("<[", StringComparison.Ordinal);
        int end = grammar.IndexOf("]>", at, StringComparison.Ordinal) + 2;
        var lines = new LineMap(grammar);
        var prepared = service.PrepareQuoteHelperExtraction(Uri("main.ngr"), new(lines.PositionOf(at), lines.PositionOf(end)));
        Assert.Null(prepared.UnavailableReason);
        var proposal = service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("main.ngr"), "M", "WrapQuote", true));
        Assert.Null(proposal.UnavailableReason);
        var text = RenameTests.Apply(grammar, Assert.Single(proposal.Changes).Edits);
        Assert.Contains("rewrites " + call, text);
        Assert.Contains("syntax function " + declaration, text);
        Assert.Contains(grammar[at..end], text);
    }

    [Theory]
    [InlineData("shared.ngr")]
    [InlineData("nitrogen.json")]
    public void A_changed_destination_or_configuration_is_named(string file)
    {
        using var service = Service();
        var prepared = Prepare(service);
        File.AppendAllText(Path.Combine(_root, file), "\n");
        var proposal = service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("shared.ngr"), "Shared", "WrapQuote", true));
        Assert.Contains(file, proposal.UnavailableReason!);
    }

    [Fact]
    public void Invalid_existing_module_and_outside_destination_are_refused()
    {
        using var service = Service();
        var prepared = Prepare(service);
        Assert.NotNull(service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("shared.ngr"), "Missing", "Quote", true)).UnavailableReason);
        Assert.NotNull(service.ResolveQuoteHelperExtraction(new(prepared.Id!, new System.Uri(Path.Combine(Path.GetDirectoryName(_root)!, "outside.ngr")).AbsoluteUri, "Shared", "Quote", false)).UnavailableReason);
    }

    [Fact]
    public void Extracted_DateCalc_quote_preserves_real_evaluation()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage");
        foreach (string file in Directory.GetFiles(directory)) File.Copy(file, Path.Combine(_root, Path.GetFileName(file)), true);
        string sourcePath = Path.Combine(_root, "DateCalc.ngr");
        string grammar = File.ReadAllText(sourcePath);
        grammar = grammar.Replace("  syntax function FortnightDuration(Count: Expr) : Expr = <[ $Count * 2 weeks ]>;", "")
            .Replace("rewrites FortnightDuration(Count)", "rewrites <[ $Count * 2 weeks ]>");
        File.WriteAllText(sourcePath, grammar);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        var lines = new LineMap(grammar);
        int at = grammar.IndexOf("<[ $Count * 2 weeks ]>", StringComparison.Ordinal);
        var prepared = service.PrepareQuoteHelperExtraction(Uri("DateCalc.ngr"), new(lines.PositionOf(at), lines.PositionOf(at + "<[ $Count * 2 weeks ]>".Length)));
        Assert.Null(prepared.UnavailableReason);
        var proposal = service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("Shared.ngr"), "Shared", "FortnightDuration", false));
        Assert.Null(proposal.UnavailableReason);
        var files = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllText);
        foreach (var change in proposal.Changes) files[new System.Uri(change.Uri).LocalPath] = RenameTests.Apply(change.OriginalText, change.Edits);
        files[Path.Combine(_root, "Shared.ngr")] = proposal.DestinationText;
        const string program = "let count = 3;\ncount fortnights;\n(1 + 2) fortnights;\n2026-10-05 + 2 fortnights;";
        service.Open(Uri("sample.datecalc"), 1, program);
        File.WriteAllText(Path.Combine(_root, "Shared.ngr"), ""); // VS Code creates the file, then changes open buffers.
        service.Open(Uri("Shared.ngr"), 1, proposal.DestinationText);
        foreach (var change in proposal.Changes)
            service.Change(change.Uri, (change.Version ?? 0) + 1, files[new System.Uri(change.Uri).LocalPath]);
        var whole = new DocumentRange(new(0, 0), new(10, 0));
        Assert.Empty(service.Diagnostics(Uri("sample.datecalc")));
        Assert.Equal(new[] { "= 3", "= 42 days", "= 42 days", "= 2026-11-02 Mon" }, service.ValueHints(Uri("sample.datecalc"), whole).Select(h => h.Label));
        service.Change(Uri("Shared.ngr"), 2, proposal.DestinationText.Replace("2 weeks", "3 weeks"));
        Assert.Empty(service.Diagnostics(Uri("sample.datecalc")));
        Assert.Equal(new[] { "= 3", "= 63 days", "= 63 days", "= 2026-11-16 Mon" }, service.ValueHints(Uri("sample.datecalc"), whole).Select(h => h.Label));
        var workspace = new GrammarWorkspace { GeneratedNamespace = "DateCalc.Syntax", CompositionName = "DateCalc", StartRule = "DateCalc.Program" };
        foreach (var file in files)
            if (file.Key.EndsWith(".ngr", StringComparison.Ordinal)) workspace.SetGrammar(file.Key, file.Value);
            else if (file.Key.EndsWith(".cs", StringComparison.Ordinal)) workspace.Sources[file.Key] = file.Value;
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        var evaluator = snapshot.Language!.Modules[0].GetType().Assembly.GetType("DateCalc.Syntax.DateCalcEvaluator", true)!;
        var result = (System.Collections.IEnumerable)evaluator.GetMethod("Run")!.Invoke(null,
            new object?[] { "let count = 3;\ncount fortnights;\n(1 + 2) fortnights;\n2026-10-05 + 2 fortnights;", null })!;
        Assert.Equal(new[] { "3", "42 days", "42 days", "2026-11-02 Mon" }, result.Cast<object>().Select(line =>
        {
            Assert.False((bool)line.GetType().GetProperty("IsError")!.GetValue(line)!);
            return (string)line.GetType().GetProperty("Text")!.GetValue(line)!;
        }));
        Assert.Equal(grammar, File.ReadAllText(sourcePath));
    }

    [Fact]
    public void Symlink_destinations_and_original_CSharp_errors_are_refused()
    {
        using var service = Service();
        var prepared = Prepare(service);
        string link = Path.Combine(_root, "linked.ngr");
        File.CreateSymbolicLink(link, Path.Combine(_root, "shared.ngr"));
        Assert.NotNull(service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("linked.ngr"), "Shared", "Quote", true)).UnavailableReason);
        File.Delete(link);
        File.WriteAllText(Path.Combine(_root, "bad.cs"), "public class Broken { this does not compile }");
        string config = Path.Combine(_root, "nitrogen.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"grammars\":", "\"sources\":[\"bad.cs\"],\"grammars\":"));
        prepared = Prepare(service);
        var proposal = service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("main.ngr"), "M", "Quote", true));
        Assert.NotNull(proposal.UnavailableReason);
    }

    [Fact]
    public void A_local_helper_collision_qualifies_the_shared_call_and_comments_are_not_parameters()
    {
        string grammar = Grammar.Replace("syntax Program", "syntax function WrapQuote() : Expr = <[ x ]>; syntax Program")
            .Replace("<[ $X ]>", "<[ /* $NotAField */ $X ]>");
        using var service = Service(grammar);
        int at = grammar.IndexOf("/* $NotAField */", StringComparison.Ordinal);
        var lines = new LineMap(grammar);
        var prepared = service.PrepareQuoteHelperExtraction(Uri("main.ngr"), new(lines.PositionOf(at), lines.PositionOf(at)));
        Assert.Null(prepared.UnavailableReason);
        var proposal = service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("shared.ngr"), "Shared", "WrapQuote", true));
        Assert.Null(proposal.UnavailableReason);
        Assert.Contains("rewrites Shared.WrapQuote(X)", RenameTests.Apply(grammar, proposal.Changes.Single(c => c.Uri == Uri("main.ngr")).Edits));
        Assert.Contains("WrapQuote(X: M.Expr)", RenameTests.Apply(File.ReadAllText(Path.Combine(_root, "shared.ngr")), proposal.Changes.Single(c => c.Uri == Uri("shared.ngr")).Edits));
    }
    [Fact]
    public void An_unrelated_language_glob_cannot_adopt_the_destination()
    {
        using var service = Service();
        string config = Path.Combine(_root, "nitrogen.json");
        File.WriteAllText(config, "{\"languages\":[{\"name\":\"M\",\"extensions\":[\".m\"],\"start\":\"M.Program\",\"grammars\":[\"main.ngr\"]},{\"name\":\"Other\",\"extensions\":[\".other\"],\"start\":\"Shared.Program\",\"grammars\":[\"shared*.ngr\"]}]}");
        File.WriteAllText(Path.Combine(_root, "shared.ngr"), "syntax module Shared { syntax Program=\"x\"; }");
        var prepared = Prepare(service);
        Assert.Null(prepared.UnavailableReason);
        Assert.Contains("unrelated", service.ResolveQuoteHelperExtraction(new(prepared.Id!, Uri("sharedNew.ngr"), "Extracted", "Quote", false)).UnavailableReason!);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
