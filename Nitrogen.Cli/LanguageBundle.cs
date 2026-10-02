using System.Text;
using System.Text.Json;

namespace Nitrogen.Cli;

/// <summary>
/// A self-contained language for an editor plugin: <c>language/nitrogen.json</c> with its grammars and
/// helper sources, and <c>server/</c>, a framework-dependent Nitrogen build run as <c>dotnet server/nitrogen.dll lsp --config language/nitrogen.json</c>.
/// </summary>
internal static class LanguageBundle
{
    public const string ConfigPath = "language/nitrogen.json";
    public const string ServerPath = "server/nitrogen.dll";

    static readonly UTF8Encoding s_utf8 = new(false);

    /// <summary>The running nitrogen's directory when it is a framework-dependent build; null for a single-file one.</summary>
    public static string? DefaultServer() => IsServer(AppContext.BaseDirectory) ? Path.GetFullPath(AppContext.BaseDirectory) : null;

    public static bool IsServer(string directory) =>
        File.Exists(Path.Combine(directory, "nitrogen.dll")) && File.Exists(Path.Combine(directory, "nitrogen.runtimeconfig.json"));

    /// <summary>Replaces <paramref name="destination"/> with the bundle. Throws <see cref="ArgumentException"/> for unusable input.</summary>
    public static void Stage(LanguagePluginModel model, string serverDirectory, string destination)
    {
        serverDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDirectory));
        destination = Path.GetFullPath(destination);
        if (!IsServer(serverDirectory))
            throw new ArgumentException($"'{serverDirectory}' is not a framework-dependent Nitrogen build (nitrogen.dll and nitrogen.runtimeconfig.json); pass --server");
        if (destination.StartsWith(serverDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("the bundle cannot be written inside the server directory");
        var grammars = Match(model.GrammarPaths, "grammar");
        var sources = Match(model.SourcePaths, "source");

        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        string language = Directory.CreateDirectory(Path.Combine(destination, "language")).FullName;
        Copy(grammars, Path.Combine(language, "grammars"));
        Copy(sources, Path.Combine(language, "sources"));
        File.WriteAllText(Path.Combine(destination, ConfigPath), Config(model, grammars, sources), s_utf8);
        CopyDirectory(serverDirectory, Path.Combine(destination, "server"));
    }

    /// <summary>The files the patterns name, as the language service expands them; every pattern must match and names must be unique.</summary>
    static List<string> Match(IReadOnlyList<string> patterns, string kind)
    {
        var files = new List<string>();
        foreach (string pattern in patterns)
        {
            string directory = Path.GetDirectoryName(pattern)!;
            string[] matched = Directory.Exists(directory) ? Directory.GetFiles(directory, Path.GetFileName(pattern)) : [];
            if (matched.Length == 0) throw new ArgumentException($"no {kind} file matches '{pattern}'");
            files.AddRange(matched.Select(Path.GetFullPath));
        }
        files = files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var duplicate = files.GroupBy(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"two {kind} files named '{duplicate.Key}': {string.Join(", ", duplicate)}");
        return files;
    }

    static void Copy(IReadOnlyList<string> files, string directory)
    {
        if (files.Count == 0) return;
        Directory.CreateDirectory(directory);
        foreach (string file in files) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source).Order(StringComparer.Ordinal))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source).Order(StringComparer.Ordinal))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    static string Config(LanguagePluginModel model, IReadOnlyList<string> grammars, IReadOnlyList<string> sources)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("languages");
            writer.WriteStartObject();
            writer.WriteString("name", model.DisplayName);
            Strings(writer, "extensions", model.Extensions);
            writer.WriteString("start", model.StartRule);
            Strings(writer, "grammars", grammars.Select(f => "grammars/" + Path.GetFileName(f)));
            Strings(writer, "sources", sources.Select(f => "sources/" + Path.GetFileName(f)));
            Strings(writer, "usings", model.Usings);
            if (model.Namespace is not null) writer.WriteString("namespace", model.Namespace);
            if (model.TokensJson is not null)
            {
                writer.WritePropertyName("tokens");
                using var tokens = JsonDocument.Parse(model.TokensJson);
                tokens.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
