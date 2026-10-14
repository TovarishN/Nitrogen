using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class QuoteHelperExtractionLspTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-extract-lsp-").FullName;
    const string Rule = "syntax Word = \"x\";";
    const string Text = "syntax module A { syntax Program = Expr; extensible syntax Expr { | Atom=\"x\" | Wrap=X:Expr \"wrap\" precedence 30 rewrites <[ $X ]> } }";
    const string Shutdown = """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""";
    const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";
    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    NitrogenLanguageService Service()
    {
        File.WriteAllText(Path.Combine(_root, "a.ngr"), Text);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """{"languages":[{"name":"A","extensions":[".a"],"start":"A.Program","grammars":["a.ngr"]}]}""");
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("a.ngr"), 1, Text);
        return service;
    }
    object Selection() => new { textDocument = new { uri = Uri("a.ngr") }, range = new { start = new { line = 0, character = Text.IndexOf("<[", StringComparison.Ordinal) + 3 }, end = new { line = 0, character = Text.IndexOf("<[", StringComparison.Ordinal) + 3 } } };
    static string Request(int id, string method, object parameters) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
    static string Initialize(bool supported) => Request(1, "initialize", new { initializationOptions = new { grammarExtraction = true }, capabilities = new { workspace = new { workspaceEdit = new { documentChanges = supported, resourceOperations = new[] { "create" }, changeAnnotationSupport = new { groupsOnLabel = true } } } } });
    static JsonElement Result(List<JsonElement> messages, int id) => messages.Single(m => m.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id).GetProperty("result");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolves_complete_annotated_edits_with_creation_only_for_new_files(bool existing)
    {
        using var service = Service();
        int at = Text.IndexOf("<[", StringComparison.Ordinal) + 3;
        var prepared = service.PrepareQuoteHelperExtraction(Uri("a.ngr"), new(new(0, at), new(0, at)));
        Assert.Null(prepared.UnavailableReason);
        var (_, messages, _) = await LspServerTests.Session(service, Initialize(true), Request(2, "nitrogen/resolveQuoteHelperExtraction", new {
            id = prepared.Id, destinationUri = Uri(existing ? "a.ngr" : "shared.ngr"), module = existing ? "A" : "Shared", helperName = "WrapQuote", existingDestination = existing }), Shutdown, Exit);
        var result = Result(messages, 2);
        Assert.False(result.TryGetProperty("unavailableReason", out _));
        var edit = result.GetProperty("action").GetProperty("edit");
        var changes = edit.GetProperty("documentChanges").EnumerateArray().ToArray();
        Assert.Equal(!existing, changes.Any(c => c.TryGetProperty("kind", out var kind) && kind.GetString() == "create"));
        Assert.Contains(changes, c => c.TryGetProperty("textDocument", out var document) && document.GetProperty("uri").GetString() == Uri("a.ngr") && document.GetProperty("version").GetInt32() == 1);
        Assert.True(edit.GetProperty("changeAnnotations").GetProperty("extract").GetProperty("needsConfirmation").GetBoolean());
        Assert.Contains(result.GetProperty("expectedDocuments").EnumerateArray(), d => d.GetProperty("uri").GetString() == Uri("a.ngr") && d.GetProperty("text").GetString() == Text);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quote_action_is_available_or_explains_missing_client_support(bool supported)
    {
        using var service = Service();
        var (_, messages, _) = await LspServerTests.Session(service, Initialize(supported),
            Request(2, "textDocument/codeAction", Selection()), Request(3, "nitrogen/prepareQuoteHelperExtraction", Selection()), Shutdown, Exit);
        var action = Assert.Single(Result(messages, 2).EnumerateArray());
        Assert.Equal("Extract Quote Helper…", action.GetProperty("title").GetString());
        Assert.True(action.TryGetProperty(supported ? "command" : "disabled", out _));
        if (supported) Assert.Equal("A", Result(messages, 3).GetProperty("sourceModule").GetString());
        else Assert.False(Result(messages, 3).TryGetProperty("id", out _));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
