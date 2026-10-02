using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nitrogen.Cli;

/// <summary>
/// One language of a nitrogen.json, as the editor plugin generators need it. Grammar and source
/// paths are absolute and may end in a file pattern (<c>dir/*.ngr</c>), as the language service reads them.
/// </summary>
internal sealed record LanguagePluginModel(
    string PluginId,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> GrammarPaths,
    string StartRule)
{
    public IReadOnlyList<string> SourcePaths { get; init; } = [];
    public IReadOnlyList<string> Usings { get; init; } = [];

    /// <summary>The language's <c>tokens</c> object as written, or null.</summary>
    public string? TokensJson { get; init; }

    public string Version { get; init; } = "0.1.0";
}

internal static class LanguagePluginConfig
{
    static readonly Regex s_version = new(@"^\d+\.\d+\.\d+$");

    /// <summary>The language of <paramref name="path"/> named <paramref name="language"/> (or its only one); null with an error otherwise.</summary>
    public static LanguagePluginModel? Load(string path, string? language, out string error)
    {
        error = "";
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return Fail($"no config file '{path}'", out error);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
            if (!document.RootElement.TryGetProperty("languages", out JsonElement languages) || languages.ValueKind != JsonValueKind.Array || languages.GetArrayLength() == 0)
                return Fail("no languages", out error);
            JsonElement? selected = null;
            foreach (JsonElement entry in languages.EnumerateArray())
            {
                string? name = entry.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() : null;
                if (language is null || string.Equals(name, language, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected is not null && language is null) return Fail("multiple languages; specify --language", out error);
                    selected = entry;
                }
            }
            if (selected is null) return Fail($"no language '{language}'", out error);
            JsonElement entryValue = selected.Value;
            string? nameText = GetString(entryValue, "name");
            string? start = GetString(entryValue, "start");
            if (string.IsNullOrWhiteSpace(nameText)) return Fail("language name is required", out error);
            if (string.IsNullOrWhiteSpace(start)) return Fail("language start rule is required", out error);
            if (!entryValue.TryGetProperty("extensions", out JsonElement extensionValues) || extensionValues.ValueKind != JsonValueKind.Array)
                return Fail("language extensions are required", out error);
            var extensions = extensionValues.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (extensions.Any(x => string.IsNullOrWhiteSpace(x) || !x.StartsWith('.')))
                return Fail("extension must start with '.'", out error);
            if (!entryValue.TryGetProperty("grammars", out JsonElement grammarValues) || grammarValues.ValueKind != JsonValueKind.Array || grammarValues.GetArrayLength() == 0)
                return Fail("language grammars are required", out error);
            string version = "0.1.0";
            if (entryValue.TryGetProperty("version", out JsonElement versionValue))
            {
                if (versionValue.ValueKind != JsonValueKind.String || !s_version.IsMatch(versionValue.GetString()!))
                    return Fail("version must be MAJOR.MINOR.PATCH", out error);
                version = versionValue.GetString()!;
            }
            string baseDirectory = Path.GetDirectoryName(fullPath)!;
            string[] Paths(string property) => entryValue.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(x => Path.GetFullPath(Path.Combine(baseDirectory, x.GetString() ?? ""))).ToArray()
                : [];
            var model = Create(nameText, Paths("grammars"), start, extensions, out error);
            return model is null ? null : model with
            {
                SourcePaths = Paths("sources"),
                Usings = entryValue.TryGetProperty("usings", out JsonElement usings) && usings.ValueKind == JsonValueKind.Array
                    ? usings.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
                TokensJson = entryValue.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Object
                    ? tokens.GetRawText() : null,
                Version = version,
            };
        }
        catch (JsonException exception)
        {
            return Fail($"invalid JSON: {exception.Message}", out error);
        }
    }

    public static LanguagePluginModel? Create(string name, IReadOnlyList<string> grammars, string start, IEnumerable<string> extensions, out string error)
    {
        error = "";
        string pluginId = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (pluginId.Length == 0) return Fail("language name is invalid", out error);
        return new LanguagePluginModel(pluginId, name.Trim(), extensions.Select(x => x.ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(), grammars.OrderBy(x => x, StringComparer.Ordinal).ToArray(), start);
    }

    static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static LanguagePluginModel? Fail(string message, out string error)
    {
        error = message;
        return null;
    }
}
