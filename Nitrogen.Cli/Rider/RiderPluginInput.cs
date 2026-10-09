namespace Nitrogen.Cli;

internal static class RiderPluginInput
{
    public static RiderPluginRequest? ParseRequest(IReadOnlyList<string> args, out string error)
    {
        error = "";
        if (args.Count < 2 || args[0] != "generate" || args[1] != "rider")
            return FailRequest("expected 'generate rider'", out error);

        string? config = null;
        string? grammar = null;
        string? language = null;
        string? start = null;
        string? output = null;
        string nitrogen = "nitrogen";
        var bundles = new List<RiderBundleInput>();
        bool selfContained = false;
        string? server = null;
        for (int i = 2; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "--config" or "-c" or "--grammar" or "-g" or "--language" or "--start" or "-s" or "--output" or "-o" or "--nitrogen" or "--server")
            {
                if (++i >= args.Count) return FailRequest($"{arg} needs a value", out error);
                string value = args[i];
                switch (arg)
                {
                    case "--config": case "-c": config = value; break;
                    case "--grammar": case "-g": grammar = value; break;
                    case "--language": language = value; break;
                    case "--start": case "-s": start = value; break;
                    case "--output": case "-o": output = value; break;
                    case "--nitrogen": nitrogen = value; break;
                    case "--server": server = value; break;
                }
            }
            else if (arg is "--self-contained") selfContained = true;
            else if (arg is "--bundle")
            {
                if (++i >= args.Count) return FailRequest("--bundle needs a value", out error);
                string[] bundle = args[i].Split('=', 2);
                if (bundle.Length != 2) return FailRequest("--bundle needs TARGET=PATH", out error);
                if (!RiderBundleInput.Targets.Contains(bundle[0], StringComparer.Ordinal))
                    return FailRequest($"unsupported bundle target '{bundle[0]}'", out error);
                string bundlePath = Path.GetFullPath(bundle[1]);
                if (!File.Exists(bundlePath)) return FailRequest($"no bundled executable '{bundle[1]}'", out error);
                bundles.Add(new RiderBundleInput(bundle[0], bundlePath));
            }
            else return FailRequest($"unknown option '{arg}'", out error);
        }

        // Input source first, as CliOptions reports it, then where to write.
        if (config is not null && grammar is not null) return FailRequest("use either --config or --grammar", out error);
        if (config is null && grammar is null) return FailRequest("no --config or --grammar", out error);
        if (grammar is not null && start is null) return FailRequest("no --start", out error);
        if (output is null) return FailRequest("no --output", out error);
        if (selfContained && bundles.Count > 0) return FailRequest("use either --self-contained or --bundle", out error);
        if (server is not null && !selfContained) return FailRequest("--server needs --self-contained", out error);
        string? selfContainedServer = null;
        if (selfContained)
        {
            selfContainedServer = server is null ? LanguageBundle.DefaultServer() : Path.GetFullPath(server);
            if (selfContainedServer is null) return FailRequest("this nitrogen is a single-file build with no nitrogen.dll; pass --server", out error);
        }

        LanguagePluginModel? model;
        if (config is not null)
        {
            model = LanguagePluginConfig.Load(config, language, out error);
        }
        else
        {
            string fullGrammar = Path.GetFullPath(grammar!);
            string displayName = Path.GetFileNameWithoutExtension(fullGrammar);
            model = LanguagePluginConfig.Create(displayName, new[] { fullGrammar }, start!, new[] { ".ngr" }, out error);
        }

        if (model is null) return null;
        if (start is not null && config is not null)
            model = model with { StartRule = start };
        return new RiderPluginRequest(model, Path.GetFullPath(output), nitrogen, bundles, selfContainedServer, CarriesLanguage: config is not null);
    }

    static RiderPluginRequest? FailRequest(string message, out string error)
    {
        error = message;
        return null;
    }
}
