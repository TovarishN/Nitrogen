using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarExtractionLspTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-extract-lsp-").FullName;
    const string Rule = "syntax Word = \"x\";";
    const string Text = "syntax module A { " + Rule + " syntax Program = Word; }";
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
    object Selection() => new { textDocument = new { uri = Uri("a.ngr") }, range = new { start = new { line = 0, character = 18 }, end = new { line = 0, character = 18 + Rule.Length } } };
    static string Request(int id, string method, object parameters) => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
    static string Initialize(bool supported) => Request(1, "initialize", new { initializationOptions = new { grammarExtraction = true }, capabilities = new { workspace = new { workspaceEdit = new { documentChanges = supported, resourceOperations = new[] { "create" }, changeAnnotationSupport = new { groupsOnLabel = true } } } } });
    static JsonElement Result(List<JsonElement> messages, int id) => messages.Single(m => m.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id).GetProperty("result");
    [Fact]
    public async Task Offers_an_extract_command_only_for_refactor_requests()
    {
        using var service = Service();
        var selection = JsonSerializer.SerializeToElement(Selection());
        var (_, messages, _) = await LspServerTests.Session(service, Initialize(true), Request(2, "textDocument/codeAction", new {
            textDocument = selection.GetProperty("textDocument"), range = selection.GetProperty("range"), context = new { only = new[] { "refactor.extract" } } }), Shutdown, Exit);
        var action = Assert.Single(Result(messages, 2).EnumerateArray());
        Assert.Equal("refactor.extract", action.GetProperty("kind").GetString());
        Assert.Equal("nitrogen.extractGrammar", action.GetProperty("command").GetProperty("command").GetString());
    }
    [Fact]
    public async Task Unsupported_clients_get_an_unavailable_explanation()
    {
        using var service = Service();
        var (_, messages, _) = await LspServerTests.Session(service, Initialize(false), Request(2, "textDocument/codeAction", Selection()), Shutdown, Exit);
        var action = Assert.Single(Result(messages, 2).EnumerateArray());
        Assert.Contains("file", action.GetProperty("disabled").GetProperty("reason").GetString()!);
    }
    [Fact]
    public async Task Resolves_to_versioned_file_creation_and_confirmation_annotations()
    {
        using var service = Service();
        var prepared = service.PrepareExtraction(Uri("a.ngr"), new(new(0, 18), new(0, 18 + Rule.Length)));
        var (_, messages, _) = await LspServerTests.Session(service, Initialize(true), Request(2, "nitrogen/resolveGrammarExtraction", new {
            id = prepared.Id, destinationUri = Uri("aa.ngr"), module = "Shared" }), Shutdown, Exit);
        var edit = Result(messages, 2).GetProperty("action").GetProperty("edit");
        var changes = edit.GetProperty("documentChanges").EnumerateArray().ToArray();
        Assert.Equal("create", changes[0].GetProperty("kind").GetString());
        Assert.False(changes[0].GetProperty("options").GetProperty("overwrite").GetBoolean());
        Assert.Contains(changes, c => c.TryGetProperty("textDocument", out var document) && document.GetProperty("uri").GetString() == Uri("a.ngr") && document.GetProperty("version").GetInt32() == 1);
        Assert.True(edit.GetProperty("changeAnnotations").GetProperty("extract").GetProperty("needsConfirmation").GetBoolean());
    }
    [Fact]
    public async Task Prepare_request_returns_defaults_and_quickfix_only_does_not_offer_extraction()
    {
        using var service = Service();
        var selection = JsonSerializer.SerializeToElement(Selection());
        var (_, messages, _) = await LspServerTests.Session(service, Initialize(true), Request(2, "nitrogen/prepareGrammarExtraction", Selection()),
            Request(3, "textDocument/codeAction", new { textDocument = selection.GetProperty("textDocument"), range = selection.GetProperty("range"), context = new { only = new[] { "quickfix" } } }), Shutdown, Exit);
        Assert.Equal("A.Extracted", Result(messages, 2).GetProperty("suggestedModule").GetString());
        Assert.Empty(Result(messages, 3).EnumerateArray());
    }
    public void Dispose() => Directory.Delete(_root, true);
}
