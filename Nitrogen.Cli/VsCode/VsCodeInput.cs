namespace Nitrogen.Cli;

internal sealed record VsCodeRequest(LanguagePluginModel Model, string OutputDirectory, string ServerDirectory);

internal static class VsCodeInput
{
    public const string Usage = "usage: nitrogen generate vscode --config <nitrogen.json> --output <directory> [--language <name>] [--server <directory>]";

    public static VsCodeRequest? ParseRequest(IReadOnlyList<string> args, out string error)
    {
        error = "";
        if (args.Count < 2 || args[0] != "generate" || args[1] != "vscode") return Fail("expected 'generate vscode'", out error);
        string? config = null, language = null, output = null, server = null;
        for (int i = 2; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is not ("--config" or "-c" or "--language" or "--output" or "-o" or "--server")) return Fail($"unknown option '{arg}'", out error);
            if (++i >= args.Count) return Fail($"{arg} needs a value", out error);
            switch (arg)
            {
                case "--config": case "-c": config = args[i]; break;
                case "--language": language = args[i]; break;
                case "--output": case "-o": output = args[i]; break;
                case "--server": server = args[i]; break;
            }
        }
        if (config is null) return Fail("no --config", out error);
        if (output is null) return Fail("no --output", out error);
        var model = LanguagePluginConfig.Load(config, language, out error);
        if (model is null) return null;
        string? serverDirectory = server is null ? LanguageBundle.DefaultServer() : Path.GetFullPath(server);
        if (serverDirectory is null) return Fail("this nitrogen is a single-file build with no nitrogen.dll; pass --server", out error);
        return new VsCodeRequest(model, Path.GetFullPath(output), serverDirectory);
    }

    static VsCodeRequest? Fail(string message, out string error)
    {
        error = message;
        return null;
    }
}
