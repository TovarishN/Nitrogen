using System.Text.Json;
using System.Text.RegularExpressions;
using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

public sealed record AugmentationRule(LanguageReference Language, IReadOnlyList<string> Files,
    IReadOnlyList<string> Exclude, AnalysisInputMode Input, string OriginPath);

/// <summary>Workspace-relative globs: ** crosses directories, * and ? stay within one segment.</summary>
public sealed record AugmentationConfiguration(IReadOnlyList<AugmentationRule> Rules, IReadOnlyList<AnalysisIssue> Diagnostics)
{
    public static AugmentationConfiguration Parse(string path, string text)
    {
        var rules = new List<AugmentationRule>();
        var issues = new List<AnalysisIssue>();
        try
        {
            using var json = JsonDocument.Parse(text);
            if (!json.RootElement.TryGetProperty("augmentations", out var entries)) return new(rules, issues);
            foreach (var entry in entries.EnumerateArray())
            {
                string language = entry.GetProperty("language").GetString()!;
                if (string.IsNullOrWhiteSpace(language)) throw new FormatException("augmentation requires a language");
                var files = Strings(entry.GetProperty("files"));
                if (files.Length == 0) throw new FormatException("augmentation requires at least one file glob");
                var exclude = entry.TryGetProperty("exclude", out var excluded) ? Strings(excluded) : [];
                string input = entry.TryGetProperty("input", out var mode) ? mode.GetString()! : "document";
                if (input is not ("document" or "regions")) throw new FormatException("augmentation input must be document or regions");
                bool isPath = language.Contains('/') || language.Contains('\\') || Path.IsPathRooted(language) || language.Contains('.');
                rules.Add(new(new LanguageReference(language, isPath), files, exclude,
                    input == "regions" ? AnalysisInputMode.Regions : AnalysisInputMode.Document, Path.GetFullPath(path)));
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            issues.Add(DefinitionPaths.Error(path, "invalid augmentation configuration: " + error.Message, code: "NGR0403"));
        }
        return new(rules, issues);

        static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value =>
            !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!
            : throw new FormatException("file globs must be non-empty strings")).ToArray();
    }

    public IEnumerable<AugmentationRule> Matching(string path)
    {
        foreach (var rule in Rules)
        {
            string relative = Path.GetRelativePath(Path.GetDirectoryName(rule.OriginPath)!, Path.GetFullPath(path)).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
            if (rule.Files.Any(glob => Matches(relative, glob)) && !rule.Exclude.Any(glob => Matches(relative, glob))) yield return rule;
        }
    }

    internal static bool Matches(string relative, string glob)
    {
        string pattern = "\\A" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace("\\*\\*/", "(?:.*/)?").Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "\\z";
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return Regex.IsMatch(relative, pattern, options, TimeSpan.FromSeconds(1));
    }
}
