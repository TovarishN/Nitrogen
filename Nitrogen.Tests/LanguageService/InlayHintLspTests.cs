using System.Text.Json;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Statement values over LSP: the inlay hint request, and a refresh when the language is recompiled.</summary>
public sealed class InlayHintLspTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-inlay-").FullName;

    public InlayHintLspTests()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    string Initialize(string capabilities) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + new System.Uri(_root).AbsoluteUri
        + "\",\"capabilities\":" + capabilities + "}}";

    const string Initialized = """{"jsonrpc":"2.0","method":"initialized","params":{}}""";
    const string Shutdown = """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""";
    const string Exit = """{"jsonrpc":"2.0","method":"exit"}""";

    async Task<List<JsonElement>> Session(params string[] bodies)
    {
        using var service = new Nitrogen.LanguageService.NitrogenLanguageService(Nitrogen.Cli.LspCommand.Registry());
        return (await LspServerTests.Session(service, bodies)).Messages;
    }

    [Fact]
    public async Task The_server_advertises_and_answers_inlay_hints_within_the_range()
    {
        string sample = Uri("sample.datecalc");
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + sample
            + "\",\"languageId\":\"datecalc\",\"version\":1,\"text\":" + JsonSerializer.Serialize(File.ReadAllText(Path.Combine(_root, "sample.datecalc"))) + "}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/inlayHint\",\"params\":{\"textDocument\":{\"uri\":\"" + sample
            + "\"},\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":2,\"character\":100}}}}";

        var messages = await Session(Initialize("{}"), Initialized, open, request, Shutdown, Exit);

        Assert.True(messages[0].GetProperty("result").GetProperty("capabilities").GetProperty("inlayHintProvider").GetBoolean());
        var hints = messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5)
            .GetProperty("result").EnumerateArray().ToList();
        Assert.Equal(2, hints.Count);
        Assert.Equal("= 2026-10-05 Mon", hints[0].GetProperty("label").GetString());
        Assert.Equal(1, hints[0].GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal("let start = 2026-10-05;".Length, hints[0].GetProperty("position").GetProperty("character").GetInt32());
        Assert.True(hints[0].GetProperty("paddingLeft").GetBoolean());
        Assert.False(hints[0].TryGetProperty("tooltip", out _));
        Assert.Equal("= 14 days", hints[1].GetProperty("label").GetString());
    }

    [Fact]
    public async Task A_csharp_file_gets_the_values_of_its_tagged_strings()
    {
        string host = Uri("Host.cs");
        const string text = "class C { const string D = /*lang=datecalc*/ \"1 + 1;\"; }";
        string open = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + host
            + "\",\"languageId\":\"csharp\",\"version\":1,\"text\":" + JsonSerializer.Serialize(text) + "}}}";
        string request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/inlayHint\",\"params\":{\"textDocument\":{\"uri\":\"" + host
            + "\"},\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":1,\"character\":0}}}}";

        var messages = await Session(Initialize("{}"), Initialized, open, request, Shutdown, Exit);

        var hint = Assert.Single(messages.Single(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 5)
            .GetProperty("result").EnumerateArray());
        Assert.Equal("= 2", hint.GetProperty("label").GetString());
        Assert.Equal(0, hint.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(text.IndexOf("\"1 + 1;\"", StringComparison.Ordinal) + "\"1 + 1;\"".Length,
            hint.GetProperty("position").GetProperty("character").GetInt32());
    }

    [Theory]
    [InlineData("""{"workspace":{"inlayHint":{"refreshSupport":true}}}""", 1)]
    [InlineData("{}", 0)]
    public async Task A_recompiled_language_refreshes_hints_when_the_client_supports_it(string capabilities, int refreshes)
    {
        string watched = "{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeWatchedFiles\",\"params\":{\"changes\":[{\"uri\":\""
            + Uri("DateCalcEvaluator.cs") + "\",\"type\":2}]}}";

        var messages = await Session(Initialize(capabilities), Initialized, watched, Shutdown, Exit);

        Assert.Equal(refreshes, messages.Count(m => m.TryGetProperty("method", out var method) && method.GetString() == "workspace/inlayHint/refresh"));
    }
}
