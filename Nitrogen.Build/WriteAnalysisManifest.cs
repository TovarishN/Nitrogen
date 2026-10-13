using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Nitrogen.Grammar;
using Nitrogen.Workspace;

namespace Nitrogen.Build;

/// <summary>Serializes registered inputs without putting source paths into shell arguments.</summary>
public sealed class WriteAnalysisManifest : Microsoft.Build.Utilities.Task
{
    [Required] public string Root { get; set; } = "";
    [Required] public string ManifestPath { get; set; } = "";
    public ITaskItem[] Inputs { get; set; } = [];
    [Output] public bool HasAnalysis { get; set; }
    [Output] public ITaskItem[] GeneratorInputs { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            var files = new List<string>();
            var texts = new Dictionary<string, string>(PathComparer);
            foreach (string path in Inputs.Select(i => Path.GetFullPath(i.ItemSpec, Path.GetFullPath(Root))).Distinct(PathComparer).Order(PathComparer))
            {
                if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules" or ".git")) continue;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 1024 * 1024) continue;
                string text;
                try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
                catch (DecoderFallbackException) { continue; }
                if (text.Contains('\0')) continue;
                files.Add(path);
                texts[path] = text;
                var header = DocumentSignatures.Read(text);
                HasAnalysis |= Path.GetFileName(path) == "nitrogen.json" || header.Signatures.Count > 0 || header.Diagnostics.Count > 0 ||
                    text.Contains("#!nitrogen region", StringComparison.Ordinal) && HostCommentExtractor.Read(path, text).Any(c => c.Text.Contains("#!nitrogen region", StringComparison.Ordinal));
            }
            if (files.Count > 2000) throw new InvalidOperationException("Nitrogen registered input limit (2000) exceeded; narrow NitrogenInput/None/Content items");
            var grammars = files.Where(p => p.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase) ||
                DocumentSignatures.Read(texts[p]).Signatures.Any(s => s.Mode == SignatureMode.Grammar)).ToHashSet(PathComparer);
            var queue = new Queue<string>(grammars);
            while (queue.TryDequeue(out string? grammar))
            {
                var parsed = GrammarParser.Parse(DocumentSignatures.Read(texts[grammar]).MaskedText);
                foreach (string reference in parsed.File?.Languages.SelectMany(l => l.Grammars) ?? [])
                {
                    string dependency = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(grammar)!, reference));
                    if (texts.ContainsKey(dependency) && grammars.Add(dependency)) queue.Enqueue(dependency);
                }
            }
            GeneratorInputs = grammars.Concat(files.Where(p => Path.GetFileName(p) == "nitrogen.json")).Distinct(PathComparer).Order(PathComparer).Select(p => (ITaskItem)new TaskItem(p)).ToArray();
            string json = JsonSerializer.Serialize(new { root = Path.GetFullPath(Root), files, documents = files });
            string manifest = Path.GetFullPath(ManifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            if (!File.Exists(manifest) || File.ReadAllText(manifest) != json) File.WriteAllText(manifest, json);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.LogError("Nitrogen input manifest: " + error.Message);
            return false;
        }
    }

    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
