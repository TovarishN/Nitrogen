using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nitrogen.Grammar;
using Nitrogen.Workspace;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    sealed record ExtractionComposition(string Id, LanguageDefinition Definition, string ConfigurationPath,
        GrammarSpan? EntrySpan, LanguageDecl? Declaration, IReadOnlyList<string> Patterns);

    static IReadOnlyList<ExtractionComposition> ExtractionCompositions(InputSnapshot inputs)
    {
        var result = new List<ExtractionComposition>();
        foreach (var input in inputs.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(input.Path) == "nitrogen.json")
            {
                using var json = JsonDocument.Parse(input.Text);
                if (!json.RootElement.TryGetProperty("languages", out var languages)) continue;
                int cursor = 0, index = 0;
                foreach (var entry in languages.EnumerateArray())
                {
                    string raw = entry.GetRawText();
                    int start = input.Text.IndexOf(raw, cursor, StringComparison.Ordinal);
                    cursor = start + raw.Length;
                    var definition = LanguageDefinitionLoader.LoadLegacy(entry, input.Path, inputs);
                    if (definition.Definition is null)
                        throw new InvalidOperationException("Language configuration must be valid before extraction: " +
                            definition.Diagnostics.FirstOrDefault()?.Message);
                    result.Add(new(input.Path + "#" + index++, definition.Definition, input.Path, new(start, raw.Length), null, entry.GetProperty("grammars").EnumerateArray().Select(p => p.GetString()!).ToArray()));
                }
            }
            else if (Path.GetExtension(input.Path).Equals(".ngr", StringComparison.OrdinalIgnoreCase) ||
                DocumentSignatures.Read(input.Text).Signatures.Any(s => s.Mode == SignatureMode.Grammar))
            {
                var parsed = GrammarParser.Parse(DocumentSignatures.Read(input.Text).MaskedText);
                if (parsed.File?.Languages.FirstOrDefault() is not { } declaration) continue;
                var definition = LanguageDefinitionLoader.Load(input.Path, inputs);
                if (definition.Definition is null)
                    throw new InvalidOperationException("Language declaration must be valid before extraction: " + definition.Diagnostics.FirstOrDefault()?.Message);
                result.Add(new(input.Path, definition.Definition, input.Path, null, declaration, []));
            }
        }
        return result;
    }

    static bool ExtractionIncludes(ExtractionComposition composition, string path)
    {
        if (composition.EntrySpan is null) return false; // Declaration dependencies are explicit file paths.
        // The normalized definition alone cannot tell whether a new file matches a configured glob.
        return composition.Patterns.Any(pattern => ExtractionGlobMatches(composition.ConfigurationPath, pattern, path));
    }

    static IEnumerable<ExtractionTextEdit> ExtractionConfigurationEdits(ExtractionComposition composition,
        ExtractionTransform transform, string destinationPath, InputSnapshot inputs)
    {
        string path = composition.ConfigurationPath;
        string uri = new Uri(path).AbsoluteUri;
        string text = inputs.Files[path].Text;
        string start = transform.Symbols.FirstOrDefault(m => m.Before == composition.Definition.Start)?.After ?? composition.Definition.Start;
        string relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, destinationPath).Replace('\\', '/');
        if (composition.EntrySpan is { } entrySpan)
        {
            string raw = text.Substring(entrySpan.Start, entrySpan.Length);
            using var json = JsonDocument.Parse(raw);
            var entry = json.RootElement;
            if (start != composition.Definition.Start)
                yield return new(uri, JsonPropertySpan(raw, entrySpan.Start, "start"), JsonSerializer.Serialize(start));
            var grammarSpan = JsonPropertySpan(raw, entrySpan.Start, "grammars");
            if (!entry.GetProperty("grammars").EnumerateArray().Any(p => ExtractionGlobMatches(path, p.GetString()!, destinationPath)))
            {
                int at = grammarSpan.End - 1;
                bool hasItems = entry.GetProperty("grammars").GetArrayLength() > 0;
                yield return new(uri, new(at, 0), (hasItems ? ", " : "") + JsonSerializer.Serialize(relative));
            }
        }
        else if (composition.Declaration is { } declaration)
        {
            if (start != declaration.Start)
            {
                var value = declaration.Settings.First(s => s.Name == "start").Values[0];
                yield return new(uri, value.Span, start);
            }
            var grammars = declaration.Settings.FirstOrDefault(s => s.Name == "grammars");
            if (grammars is not null)
                yield return new(uri, new(grammars.Span.End - 1, 0), ", " + JsonSerializer.Serialize(relative));
            else
            {
                string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                yield return new(uri, new(declaration.Span.End - 1, 0), "  grammars " + JsonSerializer.Serialize(relative) + ";" + newline);
            }
        }
    }

    static GrammarSpan JsonPropertySpan(string entry, int offset, string property)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(entry);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.GetString() == property)
            {
                reader.Read();
                int start = (int)reader.TokenStartIndex;
                reader.Skip();
                int end = (int)reader.BytesConsumed;
                return new(offset + Encoding.UTF8.GetCharCount(bytes, 0, start), Encoding.UTF8.GetCharCount(bytes, start, end - start));
            }
        throw new InvalidOperationException("Missing configuration property '" + property + "'.");
    }

    static bool ExtractionGlobMatches(string config, string glob, string path)
    {
        string pattern = Path.GetFullPath(glob, Path.GetDirectoryName(config)!).Replace('\\', '/');
        string expression = "\\A" + Regex.Escape(pattern).Replace("\\*\\*/", "(?:.*/)?")
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "\\z";
        return Regex.IsMatch(Path.GetFullPath(path).Replace('\\', '/'), expression,
            OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}
