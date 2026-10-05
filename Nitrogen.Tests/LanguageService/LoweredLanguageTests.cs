using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// The lowered language in the editor, on the DateCalc example: in its .ngr grammar, the lowering
/// clauses are coloured, completed, explained and checked against the language's semantic catalog; in
/// .datecalc files, what the source lowers to colours it and types its completions.
/// </summary>
public sealed class LoweredLanguageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-datecalc-").FullName;
    readonly string _grammar;

    public LoweredLanguageTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        _grammar = File.ReadAllText(Path.Combine(_root, "DateCalc.ngr"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    NitrogenLanguageService Service()
    {
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("DateCalc.ngr"), 1, _grammar);
        Assert.Empty(service.Diagnostics(Uri("DateCalc.ngr")));
        return service;
    }

    /// <summary>The position of the <paramref name="nth"/> occurrence of <paramref name="text"/>, plus <paramref name="shift"/> characters.</summary>
    static DocumentPosition At(string source, string text, int shift = 0, int nth = 0)
    {
        int offset = -1;
        for (int i = 0; i <= nth; i++) offset = source.IndexOf(text, offset + 1, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{text}' is not in the text");
        offset += shift;
        int line = source.AsSpan(0, offset).Count('\n');
        return new DocumentPosition(line, offset - (source.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1));
    }

    static TokenType? TokenAt(IReadOnlyList<SemanticToken> tokens, DocumentPosition position) =>
        tokens.FirstOrDefault(t => t.Start == position) is { Length: > 0 } token ? token.Type : null;

    [Fact]
    public void Lowering_clauses_colour_operations_types_and_fields()
    {
        using var service = Service();
        var tokens = service.SemanticTokens(Uri("DateCalc.ngr"));

        Assert.Equal(TokenType.Function, TokenAt(tokens, At(_grammar, "DateCalc.Days")));
        Assert.Equal(TokenType.Type, TokenAt(tokens, At(_grammar, "Core.Scalar")));
        Assert.Equal(TokenType.Parameter, TokenAt(tokens, At(_grammar, "Days(Amount)", "Days(".Length)));
        Assert.Equal(TokenType.Parameter, TokenAt(tokens, At(_grammar, "Amount:Expr")));
        Assert.Equal(TokenType.Property, TokenAt(tokens, At(_grammar, "operation? Selected", "operation? ".Length)));
    }

    [Fact]
    public void Lowering_clauses_complete_from_the_semantic_catalog()
    {
        using var service = Service();
        string uri = Uri("DateCalc.ngr");

        var operations = service.Completion(uri, At(_grammar, "DateCalc.Days(Amount)"));
        var weekday = Assert.Single(operations, i => i.Label == "DateCalc.Weekday");
        Assert.Equal("DateCalc.Weekday(DateCalc.Date) → Core.Text", weekday.Detail);
        Assert.Equal("Math.Pow(Core.Scalar, Core.Scalar) → Core.Scalar", Assert.Single(operations, i => i.Label == "Math.Pow").Detail);
        Assert.Contains(operations, i => i.Label == "literal" && i.Kind == CompletionKind.Keyword);

        var types = service.Completion(uri, At(_grammar, "Core.Scalar Value"));
        Assert.Contains(types, i => i.Label == "DateCalc.Duration" && i.Kind == CompletionKind.Class);
        Assert.DoesNotContain(types, i => i.Label == "DateCalc.Weekday");

        var fields = service.Completion(uri, At(_grammar, "Days(Amount)", "Days(".Length));
        Assert.Equal("Amount", fields[0].Label);
        Assert.Equal(CompletionKind.Field, fields[0].Kind);
    }

    [Fact]
    public void Lowering_clauses_hover_with_signatures_and_report_unknown_operations_where_they_are_written()
    {
        using var service = Service();
        string uri = Uri("DateCalc.ngr");
        Assert.Equal("`DateCalc.Days(Core.Scalar) → DateCalc.Duration` — operation",
            service.Hover(uri, At(_grammar, "DateCalc.Days", 3))!.Markdown);

        string broken = _grammar.Replace("lowers DateCalc.Days(Amount)", "lowers DateCalc.Dayz(Amount)", StringComparison.Ordinal);
        service.Change(uri, 2, broken);

        var unknown = Assert.Single(service.Diagnostics(uri), d => d.Code == "NM0008" && d.Range.Start.Line > 0);
        Assert.Equal(At(broken, "DateCalc.Dayz"), unknown.Range.Start);
    }

    [Fact]
    public void A_lowered_document_colours_operation_words_and_typed_values()
    {
        using var service = Service();
        const string text = "let start = 2026-10-05;\nweekday(start + 2 weeks);\n";
        service.Open(Uri("a.datecalc"), 1, text);
        Assert.Empty(service.Diagnostics(Uri("a.datecalc")));
        var tokens = service.SemanticTokens(Uri("a.datecalc"));

        Assert.Equal(TokenType.Function, TokenAt(tokens, At(text, "weekday")));
        Assert.Equal(TokenType.Function, TokenAt(tokens, At(text, "weeks")));
        Assert.Equal(TokenType.Number, TokenAt(tokens, At(text, "2026-10-05")));
        Assert.Equal(TokenType.Number, TokenAt(tokens, At(text, "2 weeks")));
        Assert.Equal(TokenType.Keyword, TokenAt(tokens, At(text, "let")));
    }

    [Fact]
    public void A_language_can_colour_values_by_their_semantic_type()
    {
        string config = Path.Combine(_root, "nitrogen.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"tokens\":", "\"types\": { \"DateCalc.Date\": \"enumMember\" },\n      \"tokens\":", StringComparison.Ordinal));
        using var service = Service();
        const string text = "2026-10-05 - 3 days;\n";
        service.Open(Uri("a.datecalc"), 1, text);
        var tokens = service.SemanticTokens(Uri("a.datecalc"));

        Assert.Equal(TokenType.EnumMember, TokenAt(tokens, At(text, "2026-10-05")));
        Assert.Equal(TokenType.Number, TokenAt(tokens, At(text, "3")));
    }

    [Fact]
    public void Completion_shows_lowered_types_and_ranks_the_expected_one_first()
    {
        using var service = Service();
        const string text = "let size = 3;\nlet span = 2 weeks;\n(s) in days;\n";
        service.Open(Uri("a.datecalc"), 1, text);

        var items = service.Completion(Uri("a.datecalc"), At(text, "(s", "(s".Length));

        Assert.Equal(["span", "size"], items.Where(i => i.Detail.StartsWith("value", StringComparison.Ordinal)).Select(i => i.Label));
        Assert.Equal("value : DateCalc.Duration", items[0].Detail);
    }

    [Fact]
    public void Functions_and_constants_complete_as_built_in_names_with_their_types()
    {
        using var service = Service();
        const string text = "sq(p);\n";
        service.Open(Uri("a.datecalc"), 1, text);

        var functions = service.Completion(Uri("a.datecalc"), At(text, "sq", 2));
        Assert.Contains(functions, i => i.Label == "sqrt" && i.Kind == CompletionKind.Function && i.Detail == "function (built-in)");
        var constants = service.Completion(Uri("a.datecalc"), At(text, "(p", 2));
        Assert.Contains(constants, i => i.Label == "pi" && i.Detail == "value : Core.Scalar (built-in)");

        service.Change(Uri("a.datecalc"), 2, "sqrt(pi);\n");
        var tokens = service.SemanticTokens(Uri("a.datecalc"));
        Assert.Contains(tokens, t => t.Start == new DocumentPosition(0, 0) && t.Type == TokenType.Function && t.Modifiers == TokenModifiers.DefaultLibrary);
        Assert.Equal("`function sqrt` — built-in\n\n`Core.Scalar` · `Math.Sqrt`", service.Hover(Uri("a.datecalc"), new DocumentPosition(0, 1))!.Markdown);
    }

    [Fact]
    public void Hover_shows_what_an_expression_lowers_to_once()
    {
        using var service = Service();
        const string text = "2026-10-05 + 3 days;\n";
        service.Open(Uri("a.datecalc"), 1, text);

        var hover = service.Hover(Uri("a.datecalc"), At(text, "+"))!;

        Assert.Equal("`DateCalc.Date` · `DateCalc.Later`", hover.Markdown);
    }

    [Fact]
    public void A_type_error_in_a_lowered_document_reports_at_the_operator()
    {
        using var service = Service();
        const string text = "2026-10-05 + 2026-10-06;\n";
        service.Open(Uri("a.datecalc"), 1, text);

        var error = Assert.Single(service.Diagnostics(Uri("a.datecalc")));

        Assert.Equal("DC0002", error.Code);
        Assert.Equal("'+' does not apply to DateCalc.Date and DateCalc.Date", error.Message);
    }
}
