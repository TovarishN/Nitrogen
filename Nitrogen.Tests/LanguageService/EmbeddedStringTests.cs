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
    }

    [Fact]
    public void A_raw_literal_drops_its_closing_indentation_and_maps_each_character_to_its_source()
    {
        const string source = "x = /*lang=calc*/ \"\"\"\n        let a = 1;\n          a;\n        \"\"\";";
        var found = Assert.Single(EmbeddedStrings.Find(source));

        Assert.Equal("let a = 1;\n  a;", found.Value);
        Assert.Equal(source.IndexOf("let", StringComparison.Ordinal), found.Map[0]);
        Assert.Equal(source.IndexOf("a;", StringComparison.Ordinal), found.Map[found.Value.IndexOf("a;", StringComparison.Ordinal)]);
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
    public void An_open_helper_source_shows_the_workspace_compile_errors_in_it()
    {
        string helper = Path.Combine(_root, "MathModule.cs");
        File.WriteAllText(helper, File.ReadAllText(helper).Replace("MathF.Sqrt", "MathF.Sqrtt", StringComparison.Ordinal));
        using var service = Service();

        service.Open(Uri("MathModule.cs"), 1, File.ReadAllText(helper));

        Assert.Contains(service.Diagnostics(Uri("MathModule.cs")), d => d.Code == "CS0117" && d.Message.Contains("Sqrtt", StringComparison.Ordinal));
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
}
