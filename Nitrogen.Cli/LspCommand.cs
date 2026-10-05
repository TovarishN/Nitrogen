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
        registry.Add(new LanguageEntry("ngr", NgrParser.Language, new Dictionary<string, Rule> { [".ngr"] = NitrogenModule.File }, NgrStyles, new NgrAssist()));
        return registry;
    }

    static Presentation Styles(params (string Kind, TokenType Token, OutlineKind Outline)[] styles) =>
        new(styles.ToDictionary(s => s.Kind, s => new SymbolStyle(s.Token, s.Outline)));

    static readonly Presentation NgrStyles = Styles(
        ("module", TokenType.Namespace, OutlineKind.Module), ("rule", TokenType.Type, OutlineKind.Class),
        ("property", TokenType.Property, OutlineKind.Property));

    /// <param name="configRoot">The directory of a <c>--config</c> nitrogen.json; null serves the client's workspace.</param>
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log, CancellationToken cancel, string? configRoot = null)
    {
        using var service = new NitrogenLanguageService(Registry());
        return await new LspServer(new JsonRpcConnection(input, output), service, log, configRoot).RunAsync(cancel);
    }

    /// <summary>The directory of <paramref name="path"/>, which must be an existing file named nitrogen.json; null with an error otherwise.</summary>
    public static string? ConfigRoot(string path, out string error)
    {
        string full = Path.GetFullPath(path);
        error = Path.GetFileName(full) != "nitrogen.json" ? $"--config must name a nitrogen.json file, not '{path}'"
            : !File.Exists(full) ? $"no file '{path}'" : "";
        return error.Length == 0 ? Path.GetDirectoryName(full) : null;
    }
}
