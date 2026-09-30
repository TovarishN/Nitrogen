namespace Nitrogen.Cli;

internal sealed record PackageRequest(LanguagePluginModel Model, string OutputDirectory, string ServerDirectory, bool VsCode, bool Rider);

internal static class PackageInput
{
    public const string Usage = "usage: nitrogen package --config <nitrogen.json> --output <directory> [--language <name>] [--vscode] [--rider] [--server <directory>]";

    public static PackageRequest? ParseRequest(IReadOnlyList<string> args, out string error)
    {
        error = "";
        if (args.Count < 1 || args[0] != "package") return Fail("expected 'package'", out error);
        string? config = null, language = null, output = null, server = null;
        bool vscode = false, rider = false;
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--vscode") { vscode = true; continue; }
            if (arg == "--rider") { rider = true; continue; }
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
        if (!vscode && !rider) vscode = rider = true;
        return new PackageRequest(model, Path.GetFullPath(output), serverDirectory, vscode, rider);
    }

    static PackageRequest? Fail(string message, out string error)
    {
        error = message;
        return null;
    }
}
