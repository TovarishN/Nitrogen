using System.Text.Json;
using Nitrogen.Grammar;
using Nitrogen.LanguageService;
using Nitrogen.Workspace;

namespace Nitrogen.Cli;

internal static class DocumentCheckCommand
{
    sealed record Manifest(string Root, string[] Files, string[] Documents);

    internal static bool UsesSignatures(CliOptions options) => options.Samples.Where(File.Exists)
        .Any(path => DocumentSignatures.Read(File.ReadAllText(path)).Signatures.Count > 0);

    internal static async Task<int> RunAsync(CliOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            InputSnapshot inputs;
            string[] documents;
            if (options.Inputs is { } manifestPath)
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Root) || !Path.IsPathFullyQualified(manifest.Root) || manifest.Files is null || manifest.Documents is null ||
                    manifest.Files.Concat(manifest.Documents).Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)) || manifest.Files.Length > WorkspaceInputReader.MaxFiles)
                    throw new ArgumentException("invalid input manifest: root, files, and documents must contain absolute paths within the input limit");
                var registered = manifest.Files.Select(Path.GetFullPath).ToHashSet(PathComparer);
                documents = manifest.Documents.Select(Path.GetFullPath).Distinct(PathComparer).ToArray();
                if (documents.Any(p => !registered.Contains(p))) throw new ArgumentException("every manifest document must be registered in files");
                inputs = WorkspaceInputReader.Read(manifest.Root, registeredFiles: registered);
            }
            else
            {
                if (options.Samples.Count == 0) throw new ArgumentException("check/parse requires a document or workspace directory");
                var paths = options.Samples.Select(Path.GetFullPath).ToArray();
                foreach (string path in paths)
                    if (!File.Exists(path) && !Directory.Exists(path)) throw new ArgumentException($"no file or directory '{path}'");
                string root = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!;
                inputs = WorkspaceInputReader.Read(root, overlays: paths.Where(File.Exists).Select(p => new DocumentInput(p, File.ReadAllText(p), 0)));
                documents = paths.SelectMany(p => Directory.Exists(p) ? inputs.Files.Keys.Where(f => Under(f, p)) : [p]).Distinct(PathComparer).ToArray();
            }
            using var session = new DocumentAnalysisSession();
            var issues = new List<AnalysisIssue>();
            var selected = documents.Select(path => inputs.Files.TryGetValue(path, out var input) ? input
                : throw new ArgumentException($"document '{path}' is missing, unreadable, binary, or exceeds the input size limit")).ToArray();
            foreach (var result in await session.AnalyzeManyAsync(selected, inputs, cancellationToken))
            {
                var input = inputs.Files[result.Path];
                string path = input.Path;
                issues.AddRange(result.Diagnostics);
                if (options.Command != "parse") continue;
                var primary = result.Classification?.Selections.FirstOrDefault(s => s.IsPrimary);
                if (primary is null)
                {
                    issues.Add(new("NGR0402", "no Nitrogen language selected for this document", new(path, 0, 0), [], "nitrogen", true));
                    continue;
                }
                if (options.Grammars.Count > 0 && (options.Start != primary.Definition.Start || !options.Grammars.Any(g =>
                    PathComparer.Equals(Path.GetFullPath(g), primary.Definition.Id) || Directory.Exists(g) && Under(primary.Definition.Id, Path.GetFullPath(g)))))
                {
                    issues.Add(new("NGR0406", "explicit grammar/start settings conflict with the document signature", new(path, 0, 0), [], "nitrogen", true));
                    continue;
                }
                if (result.Diagnostics.All(d => !d.IsError)) output.WriteLine($"{path}: ok ({primary.Definition.Start})");
                if (options.Tree && session.SnapshotFor(primary.Definition.Id) is { } snapshot)
                {
                    using var parsed = snapshot.Parse(DocumentSignatures.Read(input.Text).MaskedText, primary.Definition.Start);
                    output.WriteLine(SyntaxDumper.Dump(parsed.Tree));
                }
            }
            foreach (var issue in issues.DistinctBy(i => (i.Code, i.Message, i.Location, i.Source)))
            {
                WriteIssue(issue.Location, issue.IsError ? "error" : "warning", issue.Code, issue.Message, issue.Source);
                foreach (var related in issue.Related) WriteIssue(related, "warning", issue.Code, "related source: " + issue.Message, issue.Source);
            }
            return issues.Any(i => i.IsError) ? 1 : 0;

            void WriteIssue(AnalysisLocation location, string severity, string code, string message, string source)
            {
                string text = inputs.Files.GetValueOrDefault(location.Path)?.Text ?? "";
                var position = new LineMap(text).PositionOf(location.Start);
                output.WriteLine($"{location.Path}({position.Line + 1},{position.Character + 1}): {severity} {code}: {message} [{source}]");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            output.WriteLine("error: " + error.Message);
            return 2;
        }
    }

    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    static bool Under(string path, string directory) => path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
