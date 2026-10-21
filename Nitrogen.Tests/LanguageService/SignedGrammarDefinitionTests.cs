using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SignedGrammarDefinitionTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-definition-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rule_definition_works_with_augmentation_only_configuration(bool signed)
    {
        string text = (signed ? "#!nitrogen grammar\nlanguage M { start M.Document; }\n" : "") +
            "syntax module M { syntax Document = Body:CodeBody; syntax CodeBody = \"x\"; }";
        string path = Path.Combine(_root, signed ? "GridCopyValue.syntax" : "GridCopyValue.ngr");
        File.WriteAllText(path, text);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "augmentations": [{ "language": "./GridCopyValue.syntax", "files": ["*.razor"], "input": "document" }] }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string uri = new Uri(path).AbsoluteUri;
        service.Open(uri, 1, text);
        var lines = new LineMap(text);
        var target = Assert.Single(service.Definition(uri, lines.PositionOf(text.IndexOf("Body:CodeBody", StringComparison.Ordinal) + 6)));
        Assert.Equal(uri, target.Uri);
        Assert.Equal(lines.PositionOf(text.IndexOf("syntax CodeBody", StringComparison.Ordinal) + 7), target.Range.Start);
    }

    [Fact]
    public void Unconfigured_quotation_navigation_tolerates_augmentation_only_configuration()
    {
        const string text = "syntax module M { syntax R = \"x\" rewrites <[ x ]>; }";
        string path = Path.Combine(_root, "Other.ngr");
        File.WriteAllText(path, text);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """{ "augmentations": [] }""");
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        string uri = new Uri(path).AbsoluteUri;
        service.Open(uri, 1, text);
        Assert.Empty(service.Definition(uri, new LineMap(text).PositionOf(text.IndexOf("<[ x", StringComparison.Ordinal) + 3)));
    }
}
