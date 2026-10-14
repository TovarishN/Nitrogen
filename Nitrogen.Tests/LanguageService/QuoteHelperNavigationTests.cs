using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteHelperNavigationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-helper-navigation-").FullName;
    const string Main = "syntax module M { using Shared; syntax Program=E \";\"; extensible syntax E { | Atom=\"x\" | Call=\"f\" \"(\" X:E \")\" | Wrap=X:E \"wrap\" precedence 30 rewrites F(X) } }";
    const string Shared = "syntax module Shared { syntax function F(V:M.E):M.E=<[ f($V) ]>; }";
    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    NitrogenLanguageService Service()
    {
        File.WriteAllText(Path.Combine(_root, "main.ngr"), Main);
        File.WriteAllText(Path.Combine(_root, "shared.ngr"), Shared);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), "{\"languages\":[{\"name\":\"M\",\"extensions\":[\".m\"],\"start\":\"M.Program\",\"grammars\":[\"*.ngr\"]}]}");
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("main.ngr"), 1, Main);
        service.Open(Uri("shared.ngr"), 1, Shared);
        return service;
    }
    static DocumentPosition At(string text, string marker, int delta = 0) => new LineMap(text).PositionOf(text.IndexOf(marker, StringComparison.Ordinal) + delta);

    [Fact]
    public void Calls_parameters_arguments_and_categories_navigate_to_their_own_declarations()
    {
        using var service = Service();
        var helper = Assert.Single(service.Definition(Uri("main.ngr"), At(Main, "F(X)")));
        Assert.Equal(Uri("shared.ngr"), helper.Uri);
        Assert.Equal(At(Shared, "F(V"), helper.Range.Start);
        var parameter = Assert.Single(service.Definition(Uri("shared.ngr"), At(Shared, "$V", 1)));
        Assert.Equal(At(Shared, "V:M.E"), parameter.Range.Start);
        var argument = Assert.Single(service.Definition(Uri("main.ngr"), At(Main, "F(X)", 2)));
        Assert.Equal(At(Main, "Wrap=X:E", 5), argument.Range.Start);
        var category = Assert.Single(service.Definition(Uri("shared.ngr"), At(Shared, "M.E", 2)));
        Assert.Equal(Uri("main.ngr"), category.Uri);
        Assert.Equal(At(Main, "syntax E", 7), category.Range.Start);
        Assert.NotEmpty(service.Definition(Uri("shared.ngr"), At(Shared, "f($V)")));
    }

    [Fact]
    public void Helper_and_parameter_renames_cover_resolved_uses_without_changing_caller_fields()
    {
        using var service = Service();
        Assert.Equal(2, service.References(Uri("shared.ngr"), At(Shared, "F(V"), true).Count);
        var helperEdits = service.Rename(Uri("shared.ngr"), At(Shared, "F(V"), "Expand");
        Assert.Equal(2, helperEdits.Values.Sum(edits => edits.Count));
        var parameterEdits = service.Rename(Uri("shared.ngr"), At(Shared, "V:M.E"), "Input");
        Assert.Equal(2, Assert.Single(parameterEdits).Value.Count);
        Assert.DoesNotContain(Uri("main.ngr"), parameterEdits.Keys);
    }

    [Fact]
    public void Completion_after_rewrites_in_an_incomplete_document_shows_typed_helpers()
    {
        using var service = Service();
        string text = Main.Replace("F(X)", "F");
        service.Change(Uri("main.ngr"), 2, text);
        var item = Assert.Single(service.Completion(Uri("main.ngr"), At(text, "rewrites F", 10)), item => item.Label == "F");
        Assert.Equal(CompletionKind.Function, item.Kind);
        Assert.Contains("M.E", item.Detail);
    }
    [Fact]
    public void Category_rename_is_refused_when_helper_consumers_cannot_be_updated_together()
    {
        using var service = Service();
        Assert.Throws<RenameRefusedException>(() => service.Rename(Uri("main.ngr"), At(Main, "syntax E", 7), "Changed"));
    }

    [Fact]
    public void Editing_a_helper_refreshes_diagnostics_without_restarting_the_service()
    {
        using var service = Service();
        service.Change(Uri("shared.ngr"), 2, Shared.Replace("f($V)", "unparseable"));
        Assert.Contains(service.Diagnostics(Uri("shared.ngr")), d => d.Code == "NQ0001");
        service.Change(Uri("shared.ngr"), 3, Shared);
        Assert.DoesNotContain(service.Diagnostics(Uri("shared.ngr")), d => d.Code == "NQ0001");
    }

    [Fact]
    public void Parameter_rename_ignores_dollar_text_in_comments()
    {
        using var service = Service();
        string text = Shared.Replace("f($V)", "f($V) /* $V */");
        service.Change(Uri("shared.ngr"), 2, text);
        var edits = service.Rename(Uri("shared.ngr"), At(text, "V:M.E"), "Input");
        Assert.Equal(2, Assert.Single(edits).Value.Count);
    }


    [Fact]
    public void Renaming_a_local_helper_cannot_capture_an_imported_call()
    {
        using var service = Service();
        string main = Main.Replace("syntax Program", "syntax function Local(V:E):E=<[ x ]>; syntax Program");
        service.Change(Uri("main.ngr"), 2, main);
        var error = Assert.Throws<RenameRefusedException>(() => service.Rename(Uri("main.ngr"), At(main, "Local(V"), "F"));
        Assert.Contains("resolution", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Signed_non_ngr_helpers_support_navigation_rename_and_quoted_keywords()
    {
        string text = "#!nitrogen grammar\nlanguage M { start M.Program; extensions \".m\"; }\n" + Main + "\n" + Shared;
        File.WriteAllText(Path.Combine(_root, "main.syntax"), text);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("main.syntax"), 1, text);
        Assert.Empty(service.Diagnostics(Uri("main.syntax")));
        Assert.Equal(At(text, "F(V"), Assert.Single(service.Definition(Uri("main.syntax"), At(text, "F(X)"))).Range.Start);
        Assert.Equal(2, service.Rename(Uri("main.syntax"), At(text, "F(V"), "Expand").Values.Sum(e => e.Count));
        Assert.Equal(At(text, "V:M.E"), Assert.Single(service.Definition(Uri("main.syntax"), At(text, "$V", 1))).Range.Start);
        Assert.NotEmpty(service.Definition(Uri("main.syntax"), At(text, "f($V)")));
    }

    [Fact]
    public void Parameter_rename_uses_actual_holes_even_when_a_token_matches_the_same_text()
    {
        const string main = "syntax module M { token Dollar=\"$V\"; syntax Program=E; syntax function F(V:E):E=<[ $V ]>; extensible syntax E { | Atom=\"x\" | Money=Dollar | Wrap=X:E \"wrap\" precedence 30 rewrites F(X) } }";
        using var service = Service();
        service.Change(Uri("main.ngr"), 2, main);
        service.Change(Uri("shared.ngr"), 2, "syntax module Shared { }");
        var changes = service.Rename(Uri("main.ngr"), At(main, "V:E"), "Input");
        string changed = RenameTests.Apply(main, changes[Uri("main.ngr")]);
        Assert.Contains("F(Input:E):E=<[ $Input ]>", changed);
        Assert.Contains("token Dollar=\"$V\"", changed);
    }
    [Fact]
    public void Parameter_rename_cannot_turn_opaque_token_text_into_an_additional_hole()
    {
        const string main = "syntax module M { token Dollar=\"$Input\"; syntax Program=E; syntax function F(V:E):E=<[ f($V, $Input) ]>; extensible syntax E { | Atom=\"x\" | Money=Dollar | Call=\"f\" \"(\" A:E \",\" B:E \")\" | Wrap=X:E \"wrap\" precedence 30 rewrites F(X) } }";
        using var service = Service();
        service.Change(Uri("shared.ngr"), 2, "syntax module Shared { }");
        service.Change(Uri("main.ngr"), 2, main);
        Assert.Throws<RenameRefusedException>(() => service.Rename(Uri("main.ngr"), At(main, "V:E"), "Input"));
    }

    [Fact]
    public void Shared_helper_rename_updates_every_known_composition()
    {
        const string common = "syntax module Common { extensible syntax E { | Atom=\"x\" } }";
        const string shared = "syntax module Shared { syntax function F(V:Common.E):Common.E=<[ $V ]>; }";
        const string a = "syntax module A { using Common; using Shared; syntax Program=E; extend syntax Common.E { | Wrap=\"a\" X:E rewrites F(X) } }";
        string b = a.Replace("module A", "module B").Replace("Wrap=\"a\"", "Other=\"b\"");
        File.WriteAllText(Path.Combine(_root, "common.ngr"), common);
        File.WriteAllText(Path.Combine(_root, "shared.ngr"), shared);
        File.WriteAllText(Path.Combine(_root, "a.ngr"), a);
        File.WriteAllText(Path.Combine(_root, "b.ngr"), b);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), "{\"languages\":[{\"name\":\"A\",\"extensions\":[\".a\"],\"start\":\"A.Program\",\"grammars\":[\"common.ngr\",\"shared.ngr\",\"a.ngr\"]},{\"name\":\"B\",\"extensions\":[\".b\"],\"start\":\"B.Program\",\"grammars\":[\"common.ngr\",\"shared.ngr\",\"b.ngr\"]}]}");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("shared.ngr"), 1, shared);
        var changes = service.Rename(Uri("shared.ngr"), At(shared, "F(V"), "G");
        Assert.Equal(new[] { Uri("a.ngr"), Uri("b.ngr"), Uri("shared.ngr") }.Order(), changes.Keys.Order());
        Assert.All(changes.Values, edits => Assert.Single(edits));
    }
    [Fact]
    public void Category_references_include_ordinary_grammar_uses_and_helper_annotations()
    {
        using var service = Service();
        var references = service.References(Uri("main.ngr"), At(Main, "syntax E", 7), true);
        Assert.Contains(references, r => r.Uri == Uri("main.ngr") && r.Range.Start == At(Main, "Program=E", 8));
        Assert.Contains(references, r => r.Uri == Uri("main.ngr") && r.Range.Start == At(Main, "Wrap=X:E", 7));
        Assert.Contains(references, r => r.Uri == Uri("shared.ngr") && r.Range.Start == At(Shared, "M.E"));
        Assert.Equal(references.OrderBy(r => r.Uri).ThenBy(r => r.Range.Start.Line).ThenBy(r => r.Range.Start.Character),
            service.References(Uri("main.ngr"), At(Main, "Program=E", 8), true).OrderBy(r => r.Uri).ThenBy(r => r.Range.Start.Line).ThenBy(r => r.Range.Start.Character));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
