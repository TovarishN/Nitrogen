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
}
