using System.Text.Json;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SignatureProtocolTests
{
    [Fact]
    public void Related_diagnostics_serialize_original_locations()
    {
        var range = new LspRange(new(2, 3), new(2, 8));
        var diagnostic = new LspDiagnostic(range, 1, "AU0001", "nitrogen:Rules", "failure",
            [new LspDiagnosticRelatedInformation(new LspLocation("file:///Host.cs", range), "related source")]);
        var json = JsonSerializer.SerializeToElement(diagnostic, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("file:///Host.cs", json.GetProperty("relatedInformation")[0].GetProperty("location").GetProperty("uri").GetString());
        Assert.Equal(2, json.GetProperty("relatedInformation")[0].GetProperty("location").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Native_diagnostics_publish_source_and_clear_after_signature_removal()
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-protocol-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Rules.syntax"), """
                #!nitrogen grammar
                language Rules { start M.R; }
                syntax module M { syntax R = "allowed"; }
                """);
            string uri = new Uri(Path.Combine(root, "Host.cs")).AbsoluteUri;
            using var service = new NitrogenLanguageService(LspCommand.Registry());
            service.ConfigureWorkspace(root);
            string open = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { textDocument = new { uri, languageId = "csharp", version = 1, text = "// #!nitrogen augment \"./Rules.syntax\"\nclass C {}" } } });
            string change = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "textDocument/didChange", @params = new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text = "class C {}" } } } });
            var (_, messages, _) = await LspServerTests.Session(service,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""",
                open, change,
                """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""",
                """{"jsonrpc":"2.0","method":"exit"}""");
            var published = messages.Where(m => m.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics")
                .Select(m => m.GetProperty("params")).Where(p => p.GetProperty("uri").GetString() == uri).ToArray();
            var failure = published.First(p => p.GetProperty("diagnostics").GetArrayLength() > 0).GetProperty("diagnostics")[0];
            Assert.Equal("nitrogen:Rules", failure.GetProperty("source").GetString());
            Assert.Equal(1, failure.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
            Assert.Empty(published.Last().GetProperty("diagnostics").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }
}
