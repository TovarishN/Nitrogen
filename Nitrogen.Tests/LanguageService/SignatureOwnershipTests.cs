using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SignatureOwnershipTests
{
    [Fact]
    public void Broad_synchronization_keeps_unknown_text_for_later_signature_insertion()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.Open("file:///tmp/nitrogen-unknown.txt", 1, "ordinary text");
        Assert.Equal("ordinary text", service.TextOf("file:///tmp/nitrogen-unknown.txt"));
        Assert.False(service.IsOpen("file:///tmp/nitrogen-unknown.txt"));
    }

    [Fact]
    public void Nonowning_generated_server_does_not_analyze_signatures()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ServeSignatureDocuments(false);
        service.Open("file:///tmp/nitrogen-nonowner.txt", 1, "#!nitrogen language \"./missing.syntax\"\nsource");
        Assert.Empty(service.Diagnostics("file:///tmp/nitrogen-nonowner.txt"));
    }
}
