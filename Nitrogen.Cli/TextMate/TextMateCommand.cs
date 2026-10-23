using System.Text.Json;
using Nitrogen.Grammar;
using Nitrogen.LanguageService;

namespace Nitrogen.Cli;

/// <summary>
/// <c>nitrogen generate textmate</c>: a language's TextMate grammar, coloured with the server's palette so the
/// first frame matches the semantic tokens that follow.
/// </summary>
internal static class TextMateCommand
{
    public const string Usage = "usage: nitrogen generate textmate --config <nitrogen.json> [--language <name>] [--id <language id>] --output <file>";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancel)
    {
        string? config = null, language = null, id = null, file = null;
        for (int i = 2; i < args.Count; i++)
        {
            string? value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--config": config = value; i++; break;
                case "--language": language = value; i++; break;
                case "--id": id = value; i++; break;
                case "--output": file = value; i++; break;
                default: return Fail($"unknown option '{args[i]}'", output);
            }
        }
        if (config is null || file is null) return Fail("--config and --output are required", output);
        if (LanguagePluginConfig.Load(config, language, out string error) is not { } model) return Fail(error, output);

        string? json;
        try
        {
            json = Generate(model, id ?? model.PluginId, out var diagnostics);
            foreach (var diagnostic in diagnostics) await output.WriteLineAsync($"{diagnostic.Path}: {diagnostic.Diagnostic}");
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message, output);
        }
        if (json is null) return 1;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        await File.WriteAllTextAsync(file, json, cancel);
        return 0;
    }

    /// <summary>The grammar of <paramref name="model"/> as language <paramref name="id"/> (scope <c>source.id</c>); null after errors.</summary>
    public static string? Generate(LanguagePluginModel model, string id, out IReadOnlyList<CompiledDiagnostic> diagnostics)
    {
        var inputs = LanguageBundle.Match(model.GrammarPaths, "grammar")
            .Select(path => new GrammarInput(path, File.ReadAllText(path), model.Namespace ?? "Nitrogen.Workspace"));
        var result = GrammarCompiler.TextMate(inputs, new TextMateOptions(id, "source." + id,
            Enum.GetValues<GrammarLiteralRole>().ToDictionary(r => r, r => Palette.Of(Enum.Parse<LiteralRole>(r.ToString())).Scope),
            DeclarationScopes(model.TokensJson), Palette.StandardScope(SymbolStyle.Default.Token)));
        diagnostics = result.Diagnostics.ToList();
        return result.Json;
    }

    /// <summary>The language's <c>tokens</c> styles (symbol kind → token type) as scopes.</summary>
    static Dictionary<string, string> DeclarationScopes(string? tokensJson)
    {
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tokensJson is null) return scopes;
        foreach (var style in JsonDocument.Parse(tokensJson).RootElement.EnumerateObject())
            if (Enum.TryParse<TokenType>(style.Value.GetString(), ignoreCase: true, out var type))
                scopes[style.Name] = Palette.StandardScope(type);
        return scopes;
    }

    static int Fail(string message, TextWriter output)
    {
        output.WriteLine($"error: {message}");
        output.WriteLine(Usage);
        return 2;
    }
}
