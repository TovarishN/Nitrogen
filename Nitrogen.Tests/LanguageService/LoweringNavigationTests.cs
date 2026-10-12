using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LoweringNavigationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-navigation-").FullName;

    const string Grammar = """
        syntax module M
        {
          token Identifier = ['a'..'z']+;
          syntax LowersLiteral = "literal" Type:Identifier Field:Identifier
            lowers Grammar.LowersLiteral(text Type, text Field);
          syntax Other = Value:"other" lowers Grammar.LowersLiteral(text Value, text Value);
        }
        """;

    const string Helpers = """
        using Nitrogen.Semantic;
        public static class GrammarSemantics
        {
            static OperationSignature Op(string name, SemanticType result, params SemanticType[] inputs) =>
                new("Grammar." + name, result, inputs);
            static readonly OperationSignature[] Operations =
            [
                Op("LowersLiteral", SemanticTypes.Text, SemanticTypes.Text, SemanticTypes.Text),
            ];
            public static SemanticModule Module { get; } = new("Grammar", ["Core"], [], Operations);
            // "Grammar.LowersLiteral" is only a comment.
            static readonly string Unrelated = "Grammar.LowersLiteral";
        }
        """;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    static DocumentPosition At(string source, string text, int shift = 0) =>
        new LineMap(source).PositionOf(source.IndexOf(text, StringComparison.Ordinal) + shift);

    void Write(string path, string text) => File.WriteAllText(Path.Combine(_root, path), text);

    NitrogenLanguageService Service(bool configured = false)
    {
        Write("M.ngr", Grammar);
        Write("GrammarSemantics.cs", Helpers);
        Write("Language.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        if (configured)
            Write("nitrogen.json", """
                { "languages": [ { "name": "M", "extensions": [".m"], "grammars": ["*.ngr"],
                  "sources": ["GrammarSemantics.cs"], "start": "M.LowersLiteral" } ] }
                """);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("M.ngr"), 1, Grammar);
        Assert.DoesNotContain(service.Diagnostics(Uri("M.ngr")), d => d.Severity == ServiceSeverity.Error);
        return service;
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    public void A_lowers_operation_navigates_to_its_CSharp_registration(bool configured, int shift)
    {
        using var service = Service(configured);
        var location = Assert.Single(service.Definition(Uri("M.ngr"), At(Grammar, "Grammar.LowersLiteral", shift)));
        Assert.Equal(Uri("GrammarSemantics.cs"), location.Uri);
        Assert.Equal(At(Helpers, "\"LowersLiteral\""), location.Range.Start);
    }

    [Fact]
    public void Usages_include_closed_grammars_and_optionally_the_registration()
    {
        using var service = Service();
        const string closed = "syntax module Extra { syntax R = \"r\" lowers Grammar.LowersLiteral(text this, text this); }";
        Write("Extra.ngr", closed);
        var usages = service.References(Uri("M.ngr"), At(Grammar, "Grammar.LowersLiteral"), false);
        Assert.Equal(3, usages.Count);
        Assert.Equal(2, usages.Count(l => l.Uri == Uri("M.ngr")));
        Assert.Contains(usages, l => l.Uri == Uri("Extra.ngr") && l.Range.Start == At(closed, "Grammar.LowersLiteral"));
        Assert.Equal(4, service.References(Uri("M.ngr"), At(Grammar, "Grammar.LowersLiteral"), true).Count);
    }

    [Fact]
    public void Find_usages_from_the_CSharp_registration_finds_grammar_calls()
    {
        using var service = Service();
        service.Open(Uri("GrammarSemantics.cs"), 1, Helpers);
        var usages = service.References(Uri("GrammarSemantics.cs"), At(Helpers, "\"LowersLiteral\"", 2), false);
        Assert.Equal(2, usages.Count);
        Assert.All(usages, l => Assert.Equal(Uri("M.ngr"), l.Uri));
    }

    [Fact]
    public void Open_CSharp_edits_and_deleted_sources_do_not_leave_stale_targets()
    {
        using var service = Service();
        service.Open(Uri("GrammarSemantics.cs"), 1, "\n" + Helpers);
        var location = Assert.Single(service.Definition(Uri("M.ngr"), At(Grammar, "Grammar.LowersLiteral")));
        Assert.Equal(At(Helpers, "\"LowersLiteral\"").Line + 1, location.Range.Start.Line);
        service.Close(Uri("GrammarSemantics.cs"));
        File.Delete(Path.Combine(_root, "GrammarSemantics.cs"));
        Assert.Empty(service.Definition(Uri("M.ngr"), At(Grammar, "Grammar.LowersLiteral")));
    }

    [Fact]
    public void Named_semantic_types_navigate_to_their_registration()
    {
        using var service = Service();
        string helpers = Helpers + "\npublic static class Types { public static readonly Nitrogen.Semantic.SemanticType Word = Nitrogen.Semantic.SemanticType.Named(\"Custom\", \"Word\"); }";
        service.Open(Uri("GrammarSemantics.cs"), 1, helpers);
        const string grammar = "syntax module M { token Id = ['a'..'z']+; syntax R = Value:Id lowers text Custom.Word Value; }";
        service.Change(Uri("M.ngr"), 2, grammar);
        var location = Assert.Single(service.Definition(Uri("M.ngr"), At(grammar, "Custom.Word", 7)));
        Assert.Equal(Uri("GrammarSemantics.cs"), location.Uri);
        Assert.Equal(At(helpers, "\"Custom\""), location.Range.Start);
    }

    [Fact]
    public void The_actual_self_hosted_lowers_clause_navigates_to_GrammarSemantics()
    {
        using var service = Service();
        string grammar = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Grammars", "Nitrogen.ngr"));
        string helpers = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "NavigationSources", "GrammarSemantics.cs"));
        service.Change(Uri("M.ngr"), 2, grammar);
        service.Open(Uri("GrammarSemantics.cs"), 1, helpers);
        var location = Assert.Single(service.Definition(Uri("M.ngr"), At(grammar, "lowers Grammar.LowersLiteral", 15)));
        Assert.Equal(Uri("GrammarSemantics.cs"), location.Uri);
        Assert.Equal(At(helpers, "\"LowersLiteral\""), location.Range.Start);
    }

    [Fact]
    public async Task Declaration_and_definition_requests_return_the_same_registration()
    {
        using var service = Service();
        var at = At(Grammar, "Grammar.LowersLiteral", 8);
        string request = System.Text.Json.JsonSerializer.Serialize(new
        {
            textDocument = new { uri = Uri("M.ngr") }, position = new { line = at.Line, character = at.Character },
        });
        var (_, messages, _) = await LspServerTests.Session(service,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/declaration\",\"params\":" + request + "}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/definition\",\"params\":" + request + "}",
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""",
            """{"jsonrpc":"2.0","method":"exit"}""");
        Assert.True(messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("declarationProvider").GetBoolean());
        var declaration = messages.Single(m => m.TryGetProperty("id", out var id) && id.GetInt32() == 2).GetProperty("result");
        var definition = messages.Single(m => m.TryGetProperty("id", out var id) && id.GetInt32() == 3).GetProperty("result");
        Assert.Equal(definition.GetRawText(), declaration.GetRawText());
        Assert.Equal(Uri("GrammarSemantics.cs"), Assert.Single(declaration.EnumerateArray()).GetProperty("uri").GetString());
    }

    [Theory]
    [InlineData("new OperationSignature(result: SemanticTypes.Text, id: \"Grammar.LowersLiteral\")", "Grammar.LowersLiteral", "\"Grammar.LowersLiteral\"")]
    [InlineData("SemanticType.Named(name: \"Word\", module: \"Custom\")", "Custom.Word", "\"Custom\"")]
    public void Named_CSharp_arguments_preserve_the_semantic_identity(string registration, string id, string anchor)
    {
        using var service = Service();
        string helpers = "using Nitrogen.Semantic; public static class S { static readonly object Value = " + registration + "; }";
        service.Open(Uri("GrammarSemantics.cs"), 1, helpers);
        string grammar = "syntax module M { token Id = ['a'..'z']+; syntax R = Value:Id lowers " +
            (id == "Custom.Word" ? "text " + id + " Value" : id + "()") + "; }";
        service.Change(Uri("M.ngr"), 2, grammar);
        var location = Assert.Single(service.Definition(Uri("M.ngr"), At(grammar, id)));
        Assert.Equal(At(helpers, anchor), location.Range.Start);
    }

    [Fact]
    public void Embedded_language_positions_do_not_resolve_as_CSharp_registration_positions()
    {
        using var service = Service();
        foreach (var language in LanguageServiceTests.ScopesRegistry().Entries) service.Registry.Add(language);
        const string prefix = "using Nitrogen.Semantic; class C { object Op = new OperationSignature(\"Grammar.LowersLiteral\", SemanticTypes.Text); ";
        int registration = prefix.IndexOf("Grammar.LowersLiteral", StringComparison.Ordinal);
        string embedded = "unit a { let x = 1; let y = " + new string(' ', registration - "unit a { let x = 1; let y = ".Length) + "x; }";
        string host = prefix + "object Code = /*lang=scopes*/ \"" + embedded + "\"; }";
        service.Open(Uri("Code.cs"), 1, host);
        var location = Assert.Single(service.Definition(Uri("Code.cs"), At(host, "x; }")));
        Assert.Equal(Uri("Code.cs"), location.Uri);
        Assert.Equal(At(host, "x = 1"), location.Range.Start);
    }

    [Fact]
    public void A_factory_with_a_constant_default_identity_navigates_to_its_call()
    {
        using var service = Service();
        const string helpers = """
            using Nitrogen.Semantic;
            public static class S
            {
                static OperationSignature Op(string name = "LowersLiteral") => new("Grammar." + name, SemanticTypes.Text);
                static readonly OperationSignature Value = Op();
            }
            """;
        service.Open(Uri("GrammarSemantics.cs"), 1, helpers);
        var location = Assert.Single(service.Definition(Uri("M.ngr"), At(Grammar, "Grammar.LowersLiteral")));
        Assert.Equal(At(helpers, "Op()"), location.Range.Start);
    }

    [Fact]
    public void Ordinary_strings_comments_and_other_projects_are_not_operation_definitions()
    {
        using var service = Service();
        Directory.CreateDirectory(Path.Combine(_root, "Other"));
        Write("Other/Other.csproj", "<Project />");
        Write("Other/Other.cs", Helpers);
        service.Change(Uri("GrammarSemantics.cs"), 2, Helpers.Replace("Op(\"LowersLiteral\"", "Op(\"Different\""));
        Assert.Empty(service.Definition(Uri("M.ngr"), At(Grammar, "Grammar.LowersLiteral")));
    }
}
