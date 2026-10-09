using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Languages in tagged C# strings: finding and decoding the strings, and serving them inside their C# host.</summary>
public sealed class EmbeddedStringTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-embedded-").FullName;

    public EmbeddedStringTests()
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

    static DocumentPosition At(string source, string text, int shift = 0)
    {
        int offset = source.IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{text}' is not in the text");
        offset += shift;
        int line = source.AsSpan(0, offset).Count('\n');
        return new DocumentPosition(line, offset - (source.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1));
    }

    [Theory]
    [InlineData("""Run(/*lang=calc*/ "1 + 2;");""", "calc", "1 + 2;")]
    [InlineData("""Run(/* language = calc */ @"say ""hi"";");""", "calc", "say \"hi\";")]
    [InlineData("// language=calc\nconst string S = \"a\\tb\";", "calc", "a\tb")]
    [InlineData("""Run(/*lang=calc*/ "A\\");""", "calc", "A\\")]
    public void Tagged_literals_decode_their_value(string source, string tag, string value)
    {
        var found = Assert.Single(EmbeddedStrings.Find(source));
        Assert.Equal(tag, found.Tag);
        Assert.Equal(value, found.Value);
        Assert.Equal(value.Length + 1, found.Map.Length);
        Assert.Equal(source.LastIndexOf('"') + 1, found.End); // just past the closing quote
    }

    [Fact]
    public void A_raw_literal_drops_its_closing_indentation_and_maps_each_character_to_its_source()
    {
        const string source = "x = /*lang=calc*/ \"\"\"\n        let a = 1;\n          a;\n        \"\"\";";
        var found = Assert.Single(EmbeddedStrings.Find(source));

        Assert.Equal("let a = 1;\n  a;", found.Value);
        Assert.Equal(source.IndexOf("let", StringComparison.Ordinal), found.Map[0]);
        Assert.Equal(source.IndexOf("a;", StringComparison.Ordinal), found.Map[found.Value.IndexOf("a;", StringComparison.Ordinal)]);
        Assert.Equal(source.LastIndexOf("\"\"\"", StringComparison.Ordinal) + 3, found.End);
    }

    [Theory]
    [InlineData("""Run("1 + 2;");""")]                                  // untagged
    [InlineData("""Run(/*lang=calc*/ $"{x} + 2;");""")]                 // interpolated
    [InlineData("// language=calc\nFoo(); Run(\"1;\");")]                // the tag ended with its statement
    [InlineData("""var q = '"'; // "/*lang=calc*/ "1;" """)]           // quotes in a char and a comment
    public void Untagged_interpolated_and_hidden_literals_are_not_embedded(string source) =>
        Assert.Empty(EmbeddedStrings.Find(source));

    [Fact]
    public void Tokens_and_diagnostics_of_a_tagged_string_map_to_its_csharp_host()
    {
        using var service = Service();
        const string host = "class C\n{\n    object A = Run(/*lang=datecalc*/ \"let d = 2026-10-05; weekday(d);\");\n    object B = Run(/*lang=datecalc*/ \"2026-10-05 + 2026-10-06;\");\n}\n";
        string uri = Uri("C.cs");

        Assert.Contains(uri, service.Open(uri, 3, host));

        var tokens = service.SemanticTokens(uri);
        Assert.Contains(tokens, t => t.Start == At(host, "let") && t.Type == TokenType.Keyword);
        Assert.Contains(tokens, t => t.Start == At(host, "weekday") && t.Length == "weekday".Length && t.Type == TokenType.Function);
        Assert.Contains(tokens, t => t.Start == At(host, "d = ") && t.Modifiers == TokenModifiers.Declaration);

        var error = Assert.Single(service.Diagnostics(uri));
        Assert.Equal("DC0002", error.Code);
        Assert.Equal(At(host, "2026-10-05 + 2026-10-06"), error.Range.Start);
        Assert.Equal(At(host, "2026-10-05 + 2026-10-06", "2026-10-05 + 2026-10-06".Length), error.Range.End);
        Assert.Equal(3, service.VersionOf(uri));
    }

    [Fact]
    public void Completion_hover_and_rename_work_inside_the_string()
    {
        using var service = Service();
        const string host = "var r = Run(/*lang=datecalc*/ \"let start = 2026-10-05; weekday(st);\");";
        string uri = Uri("C.cs");
        service.Open(uri, 1, host);

        var completion = Assert.Single(service.Completion(uri, At(host, "(st)", 3)), i => i.Label == "start");
        Assert.Equal(new DocumentRange(At(host, "(st)", 1), At(host, "(st)", 3)), completion.Replace);

        Assert.Contains("`DateCalc.Date`", service.Hover(uri, At(host, "2026"))!.Markdown);
        Assert.Null(service.Hover(uri, At(host, "Run")));

        const string renamed = "var r = Run(/*lang=datecalc*/ \"let start = 2026-10-05; weekday(start);\");";
        service.Change(uri, 2, renamed);
        var edit = Assert.Single(service.Rename(uri, At(renamed, "start"), "begin"));
        Assert.Equal(uri, edit.Key);
        Assert.Equal([At(renamed, "start"), At(renamed, "start)")], edit.Value.Select(e => e.Range.Start));
    }

    [Fact]
    public void Ordinary_csharp_has_nothing_to_rename()
    {
        using var service = Service();
        const string host = "var r = Run(/*lang=datecalc*/ \"let start = 2026-10-05; weekday(start);\");";
        string uri = Uri("C.cs");
        service.Open(uri, 1, host);

        // Outside the tagged string, rename is refused, so the C# rename is the editor's own.
        Assert.Throws<RenameRefusedException>(() => service.PrepareRename(uri, At(host, "Run")));
        Assert.Throws<RenameRefusedException>(() => service.Rename(uri, At(host, "Run"), "Go"));
    }

    [Fact]
    public void An_open_helper_source_shows_the_workspace_compile_errors_in_it()
    {
        string helper = Path.Combine(_root, "MathModule.cs");
        File.WriteAllText(helper, File.ReadAllText(helper).Replace("MathF.Sqrt", "MathF.Sqrtt", StringComparison.Ordinal));
        using var service = Service();

        service.Open(Uri("MathModule.cs"), 1, File.ReadAllText(helper));

        Assert.Contains(service.Diagnostics(Uri("MathModule.cs")), d => d.Code == "CS0117" && d.Message.Contains("Sqrtt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_skipped_language_is_left_to_another_server_by_name_or_extension()
    {
        const string host = "Run(/*lang=datecalc*/ \"2026-10-05 + 2026-10-06;\");";
        foreach (var (skip, served) in new[] { ("DATECALC", false), (".datecalc", false), ("geom", true) })
        {
            using var service = Service();
            service.SkipEmbedded([skip]);
            service.Open(Uri("C.cs"), 1, host);
            Assert.Equal(served, service.Diagnostics(Uri("C.cs")).Any(d => d.Code == "DC0002"));
        }
    }

    [Fact]
    public async Task The_initialize_request_names_the_languages_to_skip()
    {
        string Session(bool skip) => "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{}" +
            (skip ? ",\"initializationOptions\":{\"skipLanguages\":[\"scopes\"]}" : "") + "}}";
        const string open = """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///w/C.cs","languageId":"csharp","version":1,"text":"Run(/*lang=scopes*/ \"unit a { let y = q; }\");"}}}""";
        foreach (bool skip in new[] { false, true })
        {
            using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
            var (_, messages, _) = await LspServerTests.Session(service, Session(skip), open);
            var publish = Assert.Single(messages, m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics");
            Assert.Equal(skip ? 0 : 1, publish.GetProperty("params").GetProperty("diagnostics").GetArrayLength());
        }
    }

    [Fact]
    public void Closing_the_host_stops_serving_its_strings()
    {
        using var service = Service();
        string uri = Uri("C.cs");
        service.Open(uri, 1, "Run(/*lang=datecalc*/ \"1 + 2;\");");
        Assert.True(service.IsOpen(uri));

        service.Close(uri);

        Assert.False(service.IsOpen(uri));
        Assert.Empty(service.SemanticTokens(uri));
    }

    static readonly DocumentRange Whole = new(new DocumentPosition(0, 0), new DocumentPosition(int.MaxValue, 0));

    const string Snippets = """"
        class C
        {
            object A = Run(/*lang=datecalc*/ """
                let christmas = 2026-12-25;
                weekday(christmas);
                """);
            const string D = /*lang=datecalc*/ "2026-10-05 + 6 weeks;";
        }
        """";

    [Fact]
    public void Values_show_inside_a_multiline_string_and_after_a_one_line_string()
    {
        using var service = Service();
        string uri = Uri("Snippets.cs");
        service.Open(uri, 1, Snippets);

        var hints = service.ValueHints(uri, Whole);

        Assert.Equal(["= 2026-12-25 Fri", "= Friday", "= 2026-11-16 Mon"], hints.Select(h => h.Label));
        Assert.Equal(
            [
                At(Snippets, "2026-12-25;", "2026-12-25;".Length),
                At(Snippets, "weekday(christmas);", "weekday(christmas);".Length),
                At(Snippets, "6 weeks;\"", "6 weeks;\"".Length),
            ],
            hints.Select(h => h.At));
    }

    [Fact]
    public void A_string_with_an_error_hides_only_its_own_values()
    {
        using var service = Service();
        string uri = Uri("Mixed.cs");
        service.Open(uri, 1, "class C\n{\n    const string A = /*lang=datecalc*/ \"2026-10-05 + 2026-10-06;\";\n    const string B = /*lang=datecalc*/ \"1 + 1;\";\n}\n");

        Assert.Equal("= 2", Assert.Single(service.ValueHints(uri, Whole)).Label);
    }

    [Fact]
    public void Host_values_are_filtered_by_host_range()
    {
        using var service = Service();
        string uri = Uri("Snippets.cs");
        service.Open(uri, 1, Snippets);
        int line = At(Snippets, "const string D").Line;

        var hint = Assert.Single(service.ValueHints(uri,
            new DocumentRange(new DocumentPosition(line, 0), new DocumentPosition(line, int.MaxValue))));
        Assert.Equal("= 2026-11-16 Mon", hint.Label);
    }

    [Fact]
    public void An_edit_to_the_host_shows_the_new_values()
    {
        using var service = Service();
        string uri = Uri("Edit.cs");
        service.Open(uri, 1, "const string A = /*lang=datecalc*/ \"1 + 1;\";");
        Assert.Equal("= 2", Assert.Single(service.ValueHints(uri, Whole)).Label);

        service.Change(uri, 2, "const string A = /*lang=datecalc*/ \"2 + 2;\";");
        Assert.Equal("= 4", Assert.Single(service.ValueHints(uri, Whole)).Label);
    }

    [Fact]
    public void A_skipped_language_shows_no_values_in_strings()
    {
        using var service = Service();
        service.SkipEmbedded(["datecalc"]);
        string uri = Uri("Snippets.cs");
        service.Open(uri, 1, Snippets);

        Assert.Empty(service.ValueHints(uri, Whole));
    }
}
