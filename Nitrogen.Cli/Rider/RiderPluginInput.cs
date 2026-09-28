using System.Text.Json;

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
        for (int i = 2; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is "--config" or "-c" or "--grammar" or "-g" or "--language" or "--start" or "-s" or "--output" or "-o" or "--nitrogen")
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
                }
            }
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

        RiderPluginModel? model;
        if (config is not null)
        {
            model = LoadConfig(config, language, out error);
        }
        else
        {
            string fullGrammar = Path.GetFullPath(grammar!);
            string displayName = Path.GetFileNameWithoutExtension(fullGrammar);
            model = CreateModel(displayName, new[] { fullGrammar }, start!, new[] { ".ngr" }, out error);
        }

        if (model is null) return null;
        if (start is not null && config is not null)
            model = model with { StartRule = start };
        return new RiderPluginRequest(model, Path.GetFullPath(output), nitrogen, bundles);
    }

    static RiderPluginModel? LoadConfig(string path, string? language, out string error)
    {
        error = "";
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return FailModel($"no config file '{path}'", out error);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
            if (!document.RootElement.TryGetProperty("languages", out JsonElement languages) || languages.ValueKind != JsonValueKind.Array || languages.GetArrayLength() == 0)
                return FailModel("no languages", out error);
            JsonElement? selected = null;
            foreach (JsonElement entry in languages.EnumerateArray())
            {
                string? name = entry.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() : null;
                if (language is null || string.Equals(name, language, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected is not null && language is null) return FailModel("multiple languages; specify --language", out error);
                    selected = entry;
                }
            }
            if (selected is null) return FailModel($"no language '{language}'", out error);
            JsonElement entryValue = selected.Value;
            string? nameText = GetString(entryValue, "name");
            string? start = GetString(entryValue, "start");
            if (string.IsNullOrWhiteSpace(nameText)) return FailModel("language name is required", out error);
            if (string.IsNullOrWhiteSpace(start)) return FailModel("language start rule is required", out error);
            if (!entryValue.TryGetProperty("extensions", out JsonElement extensionValues) || extensionValues.ValueKind != JsonValueKind.Array)
                return FailModel("language extensions are required", out error);
            var extensions = extensionValues.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (extensions.Any(x => string.IsNullOrWhiteSpace(x) || !x.StartsWith('.')))
                return FailModel("extension must start with '.'", out error);
            if (!entryValue.TryGetProperty("grammars", out JsonElement grammarValues) || grammarValues.ValueKind != JsonValueKind.Array || grammarValues.GetArrayLength() == 0)
                return FailModel("language grammars are required", out error);
            string baseDirectory = Path.GetDirectoryName(fullPath)!;
            var grammars = grammarValues.EnumerateArray().Select(x => Path.GetFullPath(Path.Combine(baseDirectory, x.GetString() ?? ""))).ToArray();
            return CreateModel(nameText, grammars, start, extensions, out error);
        }
        catch (JsonException exception)
        {
            return FailModel($"invalid JSON: {exception.Message}", out error);
        }
    }

    static RiderPluginModel? CreateModel(string name, IReadOnlyList<string> grammars, string start, IEnumerable<string> extensions, out string error)
    {
        error = "";
        string pluginId = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (pluginId.Length == 0) return FailModel("language name is invalid", out error);
        return new RiderPluginModel(pluginId, name.Trim(), extensions.Select(x => x.ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(), grammars.OrderBy(x => x, StringComparer.Ordinal).ToArray(), start);
    }

    static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static RiderPluginRequest? FailRequest(string message, out string error)
    {
        error = message;
        return null;
    }

    static RiderPluginModel? FailModel(string message, out string error)
    {
        error = message;
        return null;
    }
}
