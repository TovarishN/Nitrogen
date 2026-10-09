using System.Text;
using System.Text.Json;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Whole sessions against the server over in-memory streams (issue 238).</summary>
public class LspServerTests
{
    internal static async Task<(int Code, List<JsonElement> Messages, string Log)> Session(NitrogenLanguageService service, params string[] bodies)
    {
        var input = new LockstepInput(bodies);
        var output = new MemoryStream();
        var log = new StringWriter();
        var server = new LspServer(new JsonRpcConnection(input, output), service, log);
        input.Idle = server.Idle;
        int code = await server.RunAsync(CancellationToken.None);

        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        return (code, messages, log.ToString());
    }

    const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""";
    const string Initialized = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";
    const string Shutdown = """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""";
    const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";

    static string DidOpen(string uri, int version, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri
        + "\",\"languageId\":\"x\",\"version\":" + version + ",\"text\":" + JsonSerializer.Serialize(text) + "}}}";

    [Fact]
    public async Task A_session_initializes_publishes_diagnostics_and_shuts_down()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var (code, messages, _) = await Session(service,
            Initialize, Initialized, DidOpen("file:///w/a.scopes", 3, "unit a { let y = q; }"), Shutdown, Exit);

        Assert.Equal(0, code);
        Assert.Equal(3, messages.Count);
        Assert.Equal(1, messages[0].GetProperty("id").GetInt32());
        Assert.Equal(1, messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("textDocumentSync").GetInt32());

        var publish = messages[1];
        Assert.Equal("textDocument/publishDiagnostics", publish.GetProperty("method").GetString());
        Assert.Equal(3, publish.GetProperty("params").GetProperty("version").GetInt32());
        var diagnostic = Assert.Single(publish.GetProperty("params").GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("NB0001", diagnostic.GetProperty("code").GetString());
        Assert.Equal(17, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal("nitrogen", diagnostic.GetProperty("source").GetString());

        Assert.Equal(99, messages[2].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task A_change_republishes_and_a_close_clears()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        const string change = """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///w/a.scopes","version":2},"contentChanges":[{"text":"unit a { let y = 1; }"}]}}""";
        const string close = """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///w/a.scopes"}}}""";
        var (_, messages, _) = await Session(service,
            Initialize, DidOpen("file:///w/a.scopes", 1, "unit a { let y = q; }"), change, close, Shutdown, Exit);

        var counts = messages.Where(m => m.TryGetProperty("method", out _))
            .Select(m => m.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).ToArray();
        Assert.Equal(new[] { 1, 0, 0 }, counts); // open (q unresolved), change (fixed), close (cleared)
    }

    [Fact]
    public async Task Unknown_requests_get_an_error_and_bad_notifications_are_logged()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var (code, messages, log) = await Session(service,
            """{"jsonrpc":"2.0","id":7,"method":"textDocument/formatting","params":{}}""",
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""",
            Exit);

        Assert.Equal(1, code); // exit without shutdown
        var error = Assert.Single(messages).GetProperty("error");
        Assert.Equal(LspServer.MethodNotFound, error.GetProperty("code").GetInt32());
        Assert.Contains("textDocument/didOpen", log);
    }

    [Fact]
    public async Task Broken_framing_ends_the_session()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var log = new StringWriter();
        var input = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 5\r\n\r\n{oops"));
        int code = await new LspServer(new JsonRpcConnection(input, new MemoryStream()), service, log).RunAsync(CancellationToken.None);
        Assert.Equal(1, code);
        Assert.StartsWith("nitrogen lsp:", log.ToString());
    }

    [Fact]
    public void Tokens_are_encoded_relative_to_the_previous_one()
    {
        var data = SemanticTokenEncoding.Encode(new[]
        {
            new SemanticToken(new(0, 0), 7, TokenType.Comment, TokenModifiers.None),
            new SemanticToken(new(1, 0), 4, TokenType.Keyword, TokenModifiers.None),
            new SemanticToken(new(1, 5), 1, TokenType.Class, TokenModifiers.Declaration),
        });
        Assert.Equal(new[] { 0, 0, 7, (int)TokenType.Comment, 0, 1, 0, 4, (int)TokenType.Keyword, 0, 0, 5, 1, (int)TokenType.Class, 1 }, data);
        Assert.Equal("enumMember", SemanticTokenEncoding.Legend.TokenTypes[(int)TokenType.EnumMember]);
        Assert.Equal(new[] { "declaration", "defaultLibrary" }, SemanticTokenEncoding.Legend.TokenModifiers);
    }

    [Fact]
    public async Task Semantic_tokens_and_symbols_over_the_protocol()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var (_, messages, _) = await Session(service,
            Initialize, DidOpen("file:///w/a.scopes", 1, "unit a { let x = 1; }"),
            """{"jsonrpc":"2.0","id":2,"method":"textDocument/semanticTokens/full","params":{"textDocument":{"uri":"file:///w/a.scopes"}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"textDocument/documentSymbol","params":{"textDocument":{"uri":"file:///w/a.scopes"}}}""",
            Shutdown, Exit);

        var capabilities = messages[0].GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("documentSymbolProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("semanticTokensProvider").GetProperty("full").GetBoolean());

        var data = messages.Single(m => m.TryGetProperty("id", out var id) && id.GetInt32() == 2).GetProperty("result").GetProperty("data");
        Assert.Equal(0, data.GetArrayLength() % 5);
        Assert.Equal(new[] { 0, 0, 4, (int)TokenType.Keyword, 0 }, data.EnumerateArray().Take(5).Select(e => e.GetInt32()));

        var symbol = Assert.Single(messages.Single(m => m.TryGetProperty("id", out var id) && id.GetInt32() == 3).GetProperty("result").EnumerateArray());
        Assert.Equal("a", symbol.GetProperty("name").GetString());
        Assert.Equal((int)OutlineKind.Class, symbol.GetProperty("kind").GetInt32());
        Assert.Equal("x", symbol.GetProperty("children")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Navigation_and_rename_over_the_protocol()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        const string position = "\"textDocument\":{\"uri\":\"file:///w/a.scopes\"},\"position\":{\"line\":0,\"character\":28}";
        var (_, messages, _) = await Session(service,
            Initialize, DidOpen("file:///w/a.scopes", 1, "unit a { let x = 1; let y = x; }"),
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/definition\",\"params\":{" + position + "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/references\",\"params\":{" + position + ",\"context\":{\"includeDeclaration\":true}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"textDocument/documentHighlight\",\"params\":{" + position + "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/hover\",\"params\":{" + position + "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"textDocument/rename\",\"params\":{" + position + ",\"newName\":\"w\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/prepareRename\",\"params\":{\"textDocument\":{\"uri\":\"file:///w/a.scopes\"},\"position\":{\"line\":0,\"character\":2}}}",
            Shutdown, Exit);

        JsonElement Result(int id) => messages.Single(m => m.TryGetProperty("id", out var i) && i.GetInt32() == id);
        var capabilities = messages[0].GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("definitionProvider").GetBoolean());
        Assert.True(capabilities.GetProperty("renameProvider").GetProperty("prepareProvider").GetBoolean());

        var definition = Assert.Single(Result(2).GetProperty("result").EnumerateArray());
        Assert.Equal(13, definition.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, Result(3).GetProperty("result").GetArrayLength());
        Assert.Equal(new[] { 3, 2 }, Result(4).GetProperty("result").EnumerateArray().Select(h => h.GetProperty("kind").GetInt32()));
        Assert.Equal("markdown", Result(5).GetProperty("result").GetProperty("contents").GetProperty("kind").GetString());
        Assert.Equal(2, Result(6).GetProperty("result").GetProperty("changes").GetProperty("file:///w/a.scopes").GetArrayLength());
        Assert.Equal(LspServer.RequestFailed, Result(7).GetProperty("error").GetProperty("code").GetInt32()); // `unit` is a keyword
    }

    [Fact]
    public async Task Completion_over_the_protocol()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var (_, messages, _) = await Session(service,
            Initialize, DidOpen("file:///w/a.scopes", 1, "unit a { let xa = 1; let y = x; }"),
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":\"file:///w/a.scopes\"},\"position\":{\"line\":0,\"character\":30}}}",
            Shutdown, Exit);
        Assert.Equal(".", messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("completionProvider")
            .GetProperty("triggerCharacters")[0].GetString());
        var item = Assert.Single(messages.Single(m => m.TryGetProperty("id", out var id) && id.GetInt32() == 2).GetProperty("result").EnumerateArray());
        Assert.Equal("xa", item.GetProperty("label").GetString());
        Assert.Equal((int)CompletionKind.Variable, item.GetProperty("kind").GetInt32());
        Assert.Equal("xa", item.GetProperty("textEdit").GetProperty("newText").GetString());
    }
}
