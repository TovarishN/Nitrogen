using System.Text.Json;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Incremental sync, coalescing and cancellation (spec: sync coalescing).</summary>
public class IncrementalSyncTests
{
    [Fact]
    public void TextOf_gives_the_text_of_open_documents_hosts_and_unserved_files()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        service.Open("file:///w/a.scopes", 1, "unit a { }");
        service.Open("file:///w/C.cs", 1, "class C { }");
        service.Open("file:///w/x.unknown", 1, "anything");

        Assert.Equal("unit a { }", service.TextOf("file:///w/a.scopes"));
        Assert.Equal("class C { }", service.TextOf("file:///w/C.cs"));
        Assert.Equal("anything", service.TextOf("file:///w/x.unknown"));
        Assert.Null(service.TextOf("file:///w/closed.scopes"));
    }

    const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""";
    const string Shutdown = """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""";
    const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";
    const string Doc = "file:///w/a.scopes";

    static string Open(string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Doc
        + "\",\"languageId\":\"scopes\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";

    static string Edit(int version, int line, int start, int end, string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + Doc + "\",\"version\":" + version
        + "},\"contentChanges\":[{\"range\":{\"start\":{\"line\":" + line + ",\"character\":" + start + "},\"end\":{\"line\":" + line
        + ",\"character\":" + end + "}},\"text\":" + JsonSerializer.Serialize(text) + "}]}}";

    static List<JsonElement> Publishes(List<JsonElement> messages) => messages
        .Where(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && m.GetProperty("params").GetProperty("uri").GetString() == Doc)
        .Select(m => m.GetProperty("params")).ToList();

    /// <summary>
    /// A session whose messages arrive as one burst, so the server may take them in batches of any size;
    /// then shutdown and exit, each once the server is idle, as a client sends them.
    /// </summary>
    static async Task<List<JsonElement>> Batched(params string[] bodies)
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var input = new LockstepInput(bodies, Shutdown, Exit);
        var output = new MemoryStream();
        var server = new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null);
        input.Idle = server.Idle;
        await server.RunAsync(CancellationToken.None);
        output.Position = 0;
        var messages = new List<JsonElement>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message) messages.Add(message.RootElement.Clone());
        return messages;
    }

    [Fact]
    public async Task Ranged_changes_give_the_diagnostics_of_the_edited_text()
    {
        using var service = new NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        // "unit a { let y = q; }": q (column 17) is unresolved; replace it with 1, then rename y to z.
        var (_, messages, _) = await LspServerTests.Session(service, Initialize, Open("unit a { let y = q; }"),
            Edit(2, 0, 17, 18, "1"), Edit(3, 0, 13, 14, "z"), Shutdown, Exit);

        Assert.Equal([1, 0, 0], Publishes(messages).Select(p => p.GetProperty("diagnostics").GetArrayLength()));
        Assert.Equal("unit a { let z = 1; }", service.TextOf(Doc));
    }

    [Fact]
    public async Task After_a_burst_of_changes_the_last_diagnostics_are_those_of_the_final_text()
    {
        // Twenty edits: nineteen add spaces before the closing brace, the last fixes q.
        var edits = Enumerable.Range(0, 19).Select(i => Edit(2 + i, 0, 20, 20, " ")).Append(Edit(21, 0, 17, 18, "1"));
        var messages = await Batched([Initialize, Open("unit a { let y = q; }"), .. edits]);

        var last = Publishes(messages)[^1];
        Assert.Equal(21, last.GetProperty("version").GetInt32());
        Assert.Equal(0, last.GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public async Task Every_request_gets_exactly_one_response()
    {
        static string Hover(int id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + Doc
            + "\"},\"position\":{\"line\":0,\"character\":13}}}";
        static string Cancel(int id) => "{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":" + id + "}}";
        var messages = await Batched(Initialize, Open("unit a { let y = 1; }"), Hover(5), Cancel(5), Hover(6), Cancel(42));

        var responses = messages.Where(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number).ToList();
        Assert.Equal([1, 5, 6, 99], responses.Select(r => r.GetProperty("id").GetInt32()).Order());
        var five = responses.Single(r => r.GetProperty("id").GetInt32() == 5);
        Assert.True(five.TryGetProperty("result", out _)
            || five.GetProperty("error").GetProperty("code").GetInt32() == LspServer.RequestCancelled);
        Assert.True(responses.Single(r => r.GetProperty("id").GetInt32() == 6).TryGetProperty("result", out _));
    }
}
