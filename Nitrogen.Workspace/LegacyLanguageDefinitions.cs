using System.Text.Json;
using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

public static partial class LanguageDefinitionLoader
{
    /// <summary>Adapts legacy JSON into the same normalized identity and dependency inputs as signatures.</summary>
    public static DefinitionResult LoadLegacy(JsonElement entry, string configurationPath, InputSnapshot inputs)
    {
        var issues = new List<AnalysisIssue>();
        try
        {
            string name = entry.GetProperty("name").GetString()!;
            string start = entry.GetProperty("start").GetString()!;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(start)) throw new FormatException("language name and start are required");
            var grammars = Match("grammars");
            if (grammars.Count == 0) issues.Add(DefinitionPaths.Error(configurationPath, "no registered grammars match the configured language"));
            var sources = Match("sources");
            var usings = Strings("usings");
            var extensions = Strings("extensions");
            string ns = entry.TryGetProperty("namespace", out var value) ? value.GetString()! : GrammarWorkspace.Namespace;
            var presentation = new List<LanguagePresentationOption>();
            foreach (string category in new[] { "tokens", "types" })
                if (entry.TryGetProperty(category, out var styles))
                    foreach (var style in styles.EnumerateObject())
                        presentation.Add(new(category, style.Name, style.Value.GetString()!));
            LanguageDefinition? declared = null;
            foreach (var grammar in grammars.ToArray())
            {
                var header = DocumentSignatures.Read(grammar.Text);
                issues.AddRange(header.Diagnostics.Select(d => new AnalysisIssue(d.Code, d.Message,
                    new(grammar.Path, d.Span.Start, d.Span.Length), [], "nitrogen", true)));
                var parsed = GrammarParser.Parse(header.MaskedText);
                issues.AddRange(parsed.Diagnostics.Select(d => new AnalysisIssue(d.Code, d.Message,
                    new(grammar.Path, d.Span.Start, d.Span.Length), [], "nitrogen", true)));
                if (parsed.File?.Languages.Count is not > 0) continue;
                var result = Load(grammar.Path, inputs);
                issues.AddRange(result.Diagnostics);
                if (result.Definition is not { } definition) continue;
                if (declared is not null && !DefinitionPaths.Comparer.Equals(declared.Id, definition.Id))
                    issues.Add(DefinitionPaths.Error(configurationPath, "multiple defining language declarations in one configured language"));
                declared = definition;
                var declaration = parsed.File.Languages[0];
                if (name != definition.Name || start != definition.Start ||
                    entry.TryGetProperty("namespace", out _) && ns != definition.GeneratedNamespace)
                    issues.Add(DefinitionPaths.Error(configurationPath, "configured language metadata conflicts with its grammar declaration"));
                foreach (var setting in declaration.Settings)
                    if (entry.TryGetProperty(setting.Name, out var configured))
                    {
                        if (setting.Name is "sources" or "grammars")
                        {
                            var actual = Strings(setting.Name).Select(p => DefinitionPaths.RelativeTo(p, configurationPath)).ToHashSet(DefinitionPaths.Comparer);
                            var expected = setting.Values.Select(v => DefinitionPaths.RelativeTo(v.Name, grammar.Path)).ToHashSet(DefinitionPaths.Comparer);
                            if (setting.Name == "grammars") expected.Add(grammar.Path);
                            if (!actual.SetEquals(expected)) Conflict(setting.Name);
                        }
                        else if (setting.Name is "extensions" or "usings")
                        {
                            if (!Strings(setting.Name).Order(StringComparer.Ordinal).SequenceEqual(setting.Values.Select(v => v.Name).Order(StringComparer.Ordinal))) Conflict(setting.Name);
                        }
                        else if (setting.Name is "tokens" or "types")
                        {
                            if (!configured.TryGetProperty(setting.Values[0].Name, out var style) || style.GetString() != setting.Values[1].Name) Conflict(setting.Name);
                        }
                    }
            }
            if (issues.Any(i => i.IsError)) return new(null, issues);
            var allGrammars = (declared?.Grammars ?? []).Concat(grammars.Select(g => g with { Text = DocumentSignatures.Read(g.Text).MaskedText }))
                .DistinctBy(g => g.Path, DefinitionPaths.Comparer).ToArray();
            var allSources = (declared?.Sources ?? []).Concat(sources).DistinctBy(s => s.Path, DefinitionPaths.Comparer).ToArray();
            return new(new LanguageDefinition(declared?.Id ?? configurationPath + "#" + name, name, start,
                declared?.GeneratedNamespace ?? ns, allGrammars, allSources,
                (declared?.Usings ?? []).Concat(usings).Distinct(StringComparer.Ordinal).ToArray(),
                declared is { Extensions.Count: > 0 } ? declared.Extensions : extensions,
                (declared?.Presentation ?? []).Concat(presentation).Distinct().ToArray()), issues);

            void Conflict(string setting) => issues.Add(DefinitionPaths.Error(configurationPath, $"configured '{setting}' conflicts with its grammar declaration"));
            string[] Strings(string property) => entry.TryGetProperty(property, out var items)
                ? items.EnumerateArray().Select(item => item.GetString() ?? throw new FormatException("expected a string value")).ToArray() : [];
            List<DocumentInput> Match(string property)
            {
                var found = new List<DocumentInput>();
                foreach (string pattern in Strings(property))
                {
                    string absolute = DefinitionPaths.RelativeTo(pattern, configurationPath).Replace('\\', '/');
                    var matches = inputs.Files.Values.Where(f => AugmentationConfiguration.Matches(DefinitionPaths.Canonical(f.Path).Replace('\\', '/'), absolute)).ToArray();
                    if (matches.Length == 0) issues.Add(DefinitionPaths.Error(configurationPath, $"no registered inputs match '{pattern}'"));
                    found.AddRange(matches);
                }
                return found.DistinctBy(f => f.Path, DefinitionPaths.Comparer).ToList();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or NotSupportedException)
        {
            issues.Add(DefinitionPaths.Error(configurationPath, "invalid language configuration: " + error.Message));
            return new(null, issues);
        }
    }
}
