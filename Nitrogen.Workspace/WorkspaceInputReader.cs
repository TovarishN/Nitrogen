using System.Text;
using System.Text.Json;
using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

/// <summary>Filesystem adapter; resolvers and source generators still consume only explicit snapshots.</summary>
public static class WorkspaceInputReader
{
    public const int MaxFiles = 2000;
    public const int MaxFileBytes = 1024 * 1024;
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "artifacts", "dist" };

    public static InputSnapshot Read(string root, IEnumerable<DocumentInput>? overlays = null, IEnumerable<string>? registeredFiles = null)
    {
        root = Path.GetFullPath(root);
        var files = new Dictionary<string, DocumentInput>(DefinitionPaths.Comparer);
        bool registered = registeredFiles is not null;
        foreach (string path in (registeredFiles ?? Enumerate(root)).Take(MaxFiles)) Add(path);
        foreach (var overlay in overlays ?? []) files[Path.GetFullPath(overlay.Path)] = overlay with { Path = Path.GetFullPath(overlay.Path) };
        if (!registered)
        {
            // Explicit dependency paths can leave the workspace; broad name discovery cannot.
            var queue = new Queue<DocumentInput>(files.Values);
            var visited = new HashSet<string>(DefinitionPaths.Comparer);
            while (queue.Count > 0 && files.Count < MaxFiles)
            {
                var input = queue.Dequeue();
                if (!visited.Add(input.Path)) continue;
                var dependencies = new List<string>();
                var header = DocumentSignatures.Read(input.Text);
                dependencies.AddRange(RegionExtractor.References(input).Where(r => r.IsPath).Select(r => r.Value));
                dependencies.AddRange(header.Signatures.Where(s => s.Reference is { IsPath: true }).Select(s => s.Reference!.Value));
                if (Path.GetExtension(input.Path).Equals(".ngr", StringComparison.OrdinalIgnoreCase) || header.Signatures.Any(s => s.Mode == SignatureMode.Grammar))
                {
                    var file = GrammarParser.Parse(header.MaskedText).File;
                    if (file is not null)
                        dependencies.AddRange(file.Languages.SelectMany(l => l.Grammars.Concat(l.Sources)));
                }
                if (Path.GetFileName(input.Path) == "nitrogen.json")
                    try
                    {
                        using var json = JsonDocument.Parse(input.Text);
                        if (json.RootElement.TryGetProperty("languages", out var languages))
                            foreach (var language in languages.EnumerateArray())
                                foreach (string key in new[] { "grammars", "sources" })
                                    if (language.TryGetProperty(key, out var paths)) dependencies.AddRange(paths.EnumerateArray().Select(p => p.GetString() ?? ""));
                        if (json.RootElement.TryGetProperty("augmentations", out var augmentations))
                            foreach (var augmentation in augmentations.EnumerateArray())
                                if (augmentation.TryGetProperty("language", out var reference) && reference.GetString() is { } path && path.Contains('.')) dependencies.Add(path);
                    }
                    catch (Exception error) when (error is JsonException or InvalidOperationException) { }
                foreach (string reference in dependencies)
                    try
                    {
                        string path = DefinitionPaths.RelativeTo(reference, input.Path);
                        if (!files.ContainsKey(path) && Add(path)) queue.Enqueue(files[path]);
                    }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return new(root, files, registered);

        bool Add(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxFileBytes) return false;
                // Unix FIFOs (including .NET debug pipes) report zero length but block on open.
                // Empty candidates have no source text; retain empty regular files without opening either.
                string text = info.Length == 0 ? "" : File.ReadAllText(path, new UTF8Encoding(false, true));
                if (text.Contains('\0')) return false;
                files[path] = new(path, text, info.LastWriteTimeUtc.Ticks);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException) { return false; }
        }
    }

    static IEnumerable<string> Enumerate(string directory)
    {
        string[] files, children;
        try { files = Directory.GetFiles(directory); children = Directory.GetDirectories(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string file in files.Order(StringComparer.Ordinal)) yield return file;
        foreach (string child in children.Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || Skipped.Contains(name) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string file in Enumerate(child)) yield return file;
        }
    }
}
