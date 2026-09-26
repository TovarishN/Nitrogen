using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;

namespace Nitrogen.Cli;

/// <summary><c>nitrogen lsp</c>: the language server on stdin/stdout for .ngr and workspace languages.</summary>
internal static class LspCommand
{
    public static LanguageRegistry Registry()
    {
        var registry = new LanguageRegistry();
        registry.Add(new LanguageEntry("ngr", NgrParser.Language, new Dictionary<string, Rule> { [".ngr"] = NitrogenModule.File }, NgrStyles));
        return registry;
    }

    static Presentation Styles(params (string Kind, TokenType Token, OutlineKind Outline)[] styles) =>
        new(styles.ToDictionary(s => s.Kind, s => new SymbolStyle(s.Token, s.Outline)));

    static readonly Presentation NgrStyles = Styles(
        ("module", TokenType.Namespace, OutlineKind.Module), ("rule", TokenType.Type, OutlineKind.Class),
        ("property", TokenType.Property, OutlineKind.Property));

    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log, CancellationToken cancel)
    {
        using var service = new NitrogenLanguageService(Registry());
        return await new LspServer(new JsonRpcConnection(input, output), service, log).RunAsync(cancel);
    }
}
