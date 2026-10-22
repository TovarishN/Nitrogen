using System.Text.Json;
using Nitrogen.LanguageService;

namespace Nitrogen.Cli;

/// <summary>
/// <c>nitrogen tokens &lt;file&gt; [--config &lt;nitrogen.json&gt;]</c>: the server's semantic tokens with the scope an
/// opted-in editor colours each with (its palette scope, else the standard type's), as a JSON array. Compared with a
/// TextMate grammar's tokenization, it shows where the first frame and the server disagree (eng/check-palette.sh).
/// </summary>
internal static class TokensCommand
{
    public const string Usage = "usage: nitrogen tokens <file> [--config <nitrogen.json>]";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, CancellationToken cancel)
    {
        string? file = null, config = null;
        for (int i = 1; i < args.Count; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Count) config = args[++i];
            else if (file is null && !args[i].StartsWith("--", StringComparison.Ordinal)) file = args[i];
            else return await Fail($"unexpected argument '{args[i]}'", output);
        }
        if (file is null || !File.Exists(file)) return await Fail(file is null ? "no file" : $"no file '{file}'", output);

        using var service = new NitrogenLanguageService(LspCommand.Registry());
        if (config is not null) service.ConfigureWorkspace(Path.GetDirectoryName(Path.GetFullPath(config))!);
        string uri = new Uri(Path.GetFullPath(file)).AbsoluteUri;
        service.Open(uri, 1, await File.ReadAllTextAsync(file, cancel));
        var tokens = service.SemanticTokens(uri).Select(t => new
        {
            line = t.Start.Line,
            character = t.Start.Character,
            length = t.Length,
            scope = t.Palette is { } palette ? Palette.Named(palette).Scope : Palette.StandardScope(t.Type),
        });
        await output.WriteLineAsync(JsonSerializer.Serialize(tokens));
        return 0;
    }

    static async Task<int> Fail(string message, TextWriter output)
    {
        await output.WriteLineAsync($"error: {message}");
        await output.WriteLineAsync(Usage);
        return 2;
    }
}
