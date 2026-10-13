using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nitrogen.Grammar;
using Nitrogen.Workspace;

namespace Nitrogen.LanguageService;

public sealed partial class NitrogenLanguageService
{
    sealed record PendingExtraction(string Root, InputSnapshot Inputs, string Uri, DocumentRange Range,
        Dictionary<string, int?> Versions, DateTime Created);
    readonly Dictionary<string, PendingExtraction> _extractions = new(StringComparer.Ordinal);

    public ExtractionPreparation PrepareExtraction(string uri, DocumentRange range)
    {
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile || IsEmbedded(uri))
                return new(null, null, null, "Extraction requires a workspace grammar file.");
            string? root = ExtractionRoot(parsed.LocalPath);
            if (root is null) return new(null, null, null, "Open a workspace folder before extracting grammar definitions.");
            var inputs = ExtractionInputs(root);
            if (inputs.Files.Count >= WorkspaceInputReader.MaxFiles)
                return new(null, null, null, "The workspace input limit was reached; use a smaller language workspace for extraction.");
            if (!inputs.Files.TryGetValue(parsed.LocalPath, out var input))
                return new(null, null, null, "The grammar is outside the registered workspace inputs.");
            var map = new LineMap(input.Text);
            var selected = GrammarExtraction.Select(new(uri, input.Text),
                GrammarSpan.FromBounds(map.OffsetOf(range.Start), map.OffsetOf(range.End)));
            if (selected.Selection is null) return new(null, null, null, selected.Problems.First().Message);
            var compositions = ExtractionCompositions(inputs);
            if (!compositions.Any(c => c.Definition.Grammars.Any(g => SamePath(g.Path, parsed.LocalPath))))
                return new(null, null, null, "No valid configured or declared language composes this grammar.");
            foreach (string expired in _extractions.Where(e => DateTime.UtcNow - e.Value.Created > TimeSpan.FromMinutes(10)).Select(e => e.Key).ToArray())
                _extractions.Remove(expired);
            if (_extractions.Count >= 16) _extractions.Remove(_extractions.Keys.First());
            string id = Guid.NewGuid().ToString("N");
            _extractions[id] = new(root, inputs, uri, range, inputs.Files.Keys.ToDictionary(p => p, ExtractionVersion), DateTime.UtcNow);
            string suggested = selected.Selection.Module + ".Extracted";
            return new(id, suggested, suggested.Split('.').Last() + ".ngr", null);
        }
        catch (Exception error) when (ExtractionFailure(error))
        {
            return new(null, null, null, "Cannot prepare extraction: " + error.Message);
        }
    }

    public ExtractionProposal ResolveExtraction(ExtractionRequest request)
    {
        ExtractionProposal Refuse(string message) => new(request.DestinationUri, "", [], [], message);
        try
        {
            if (!_extractions.TryGetValue(request.Id, out var pending) || DateTime.UtcNow - pending.Created > TimeSpan.FromMinutes(10))
                return Refuse("The extraction proposal expired; select the definitions again.");
            string sourcePath = new Uri(pending.Uri).LocalPath;
            if (ExtractionRoot(sourcePath) != pending.Root) return Refuse("The workspace changed; prepare extraction again.");
            var current = ExtractionInputs(pending.Root);
            if (current.Files.Count != pending.Inputs.Files.Count || pending.Inputs.Files.Any(f =>
                !current.Files.TryGetValue(f.Key, out var input) || input.Text != f.Value.Text || ExtractionVersion(f.Key) != pending.Versions[f.Key]))
                return Refuse("Workspace files changed while extraction was being prepared; select the definitions again.");
            if (!Uri.TryCreate(request.DestinationUri, UriKind.Absolute, out var destination) || !destination.IsFile)
                return Refuse("Choose a new local .ngr file inside the workspace.");
            string destinationPath = Path.GetFullPath(destination.LocalPath);
            string? pathProblem = ExtractionDestination(pending.Root, destinationPath, current);
            if (pathProblem is not null) return Refuse(pathProblem);
            var source = current.Files[sourcePath];
            var map = new LineMap(source.Text);
            var selection = GrammarExtraction.Select(new(pending.Uri, source.Text),
                GrammarSpan.FromBounds(map.OffsetOf(pending.Range.Start), map.OffsetOf(pending.Range.End))).Selection;
            if (selection is null) return Refuse("The selected definitions changed.");
            var compositions = ExtractionCompositions(current);
            var affected = compositions.Where(c => c.Definition.Grammars.Any(g => SamePath(g.Path, sourcePath))).ToArray();
            if (affected.Length == 0) return Refuse("The language configuration changed; prepare extraction again.");
            var allEdits = new List<ExtractionTextEdit>();
            ExtractionTransform? transformation = null;
            foreach (var composition in affected)
            {
                var result = GrammarExtraction.Transform(composition.Definition.Grammars
                    .Select(g => new ExtractionSource(new Uri(g.Path).AbsoluteUri, current.Files[g.Path].Text)).ToArray(),
                    selection, destination.AbsoluteUri, request.Module);
                if (result.Problems.Count > 0) return Refuse(result.Problems.First().Message);
                if (transformation is not null && result.DestinationText != transformation.DestinationText)
                    return Refuse("The selected grammar has incompatible dependencies across its language compositions.");
                transformation = result;
                allEdits.AddRange(result.Edits);
                allEdits.AddRange(ExtractionConfigurationEdits(composition, result, destinationPath, current));
            }
            foreach (var unrelated in compositions.Except(affected))
                if (ExtractionIncludes(unrelated, destinationPath))
                    return Refuse("The destination would also change unrelated language '" + unrelated.Definition.Name + "' through grammar discovery.");
            var edits = allEdits.Distinct().ToArray();
            if (edits.Any(e => !UnderExtractionRoot(new Uri(e.Uri).LocalPath, pending.Root)))
                return Refuse("Extraction would edit a dependency outside the workspace.");
            if (edits.Any(e => ExtractionLinkedPath(pending.Root, new Uri(e.Uri).LocalPath)))
                return Refuse("Editing a source or configuration through a symbolic link is not supported.");
            foreach (var group in edits.GroupBy(e => e.Uri))
            {
                var ordered = group.OrderBy(e => e.Span.Start).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                    if (ordered[i - 1].Span.End > ordered[i].Span.Start || ordered[i - 1].Span.Start == ordered[i].Span.Start)
                        return Refuse("The proposed grammar/configuration changes conflict across languages.");
            }
            var files = current.Files.ToDictionary(f => f.Key, f => f.Value);
            foreach (var group in edits.GroupBy(e => new Uri(e.Uri).LocalPath))
            {
                var input = files[group.Key];
                string text = input.Text;
                foreach (var edit in group.OrderByDescending(e => e.Span.Start))
                    text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
                files[group.Key] = input with { Text = text };
            }
            files[destinationPath] = new(destinationPath, transformation!.DestinationText, 0);
            var proposed = current with { Files = files };
            var proposedCompositions = ExtractionCompositions(proposed);
            foreach (var before in affected)
            {
                string? baseline = ValidateExtractionComposition(before.Definition);
                if (baseline is not null) return Refuse("The original language must compile before extraction: " + baseline);
                var after = proposedCompositions.FirstOrDefault(c => c.Id == before.Id);
                if (after is null || !after.Definition.Grammars.Any(g => SamePath(g.Path, destinationPath)))
                    return Refuse("The proposed configuration does not compose the extracted grammar.");
                string? failure = ValidateExtractionComposition(after.Definition);
                if (failure is not null) return Refuse("Extraction cannot preserve the generated/helper API: " + failure);
                string moduleClass = selection.Module.Split('.').Last() + "Module";
                if (before.Definition.Sources.Any(source => CSharpSyntaxTree.ParseText(source.Text).GetRoot().DescendantNodes()
                    .OfType<MemberAccessExpressionSyntax>().Any(member => member.Name.Identifier.ValueText == "Instance" &&
                        member.Expression.ToString().Split('.').Last() == moduleClass)))
                    return Refuse("A helper composes the original module directly. Update that module factory to compose the extracted module too before extracting.");
                // Constant category names in C# quote APIs are not checked by a C# compilation.
                foreach (var move in transformation.Symbols)
                    if (before.Definition.Sources.Any(s => s.Text.Contains('"' + move.Before + '"', StringComparison.Ordinal)))
                        return Refuse("A helper uses the moved rule's name as a string; update that quotation/API consumer explicitly before extracting.");
            }
            // A destination may have appeared during compilation; CreateFile also rejects a race after this check.
            if (ExtractionDestination(pending.Root, destinationPath, ExtractionInputs(pending.Root)) is { } finalProblem)
                return Refuse(finalProblem);
            var changes = edits.GroupBy(e => e.Uri).Select(group =>
            {
                string path = new Uri(group.Key).LocalPath;
                var lines = new LineMap(current.Files[path].Text);
                return new ExtractionDocumentChange(group.Key, pending.Versions[path], group.Select(e =>
                    new DocumentEdit(lines.RangeOf(new(e.Span.Start, e.Span.Length)), e.NewText)).ToArray(), current.Files[path].Text);
            }).ToArray();
            return new(destination.AbsoluteUri, transformation.DestinationText, changes,
                ["Generated C# names may change. Consumers outside this workspace are not verified."], null);
        }
        catch (Exception error) when (ExtractionFailure(error))
        {
            return Refuse("Cannot extract these definitions: " + error.Message);
        }
    }

    static string? ValidateExtractionComposition(LanguageDefinition definition)
    {
        var workspace = new GrammarWorkspace { GeneratedNamespace = definition.GeneratedNamespace, CompositionName = definition.Name, StartRule = definition.Start };
        foreach (var grammar in definition.Grammars) workspace.SetGrammar(grammar.Path, grammar.Text);
        foreach (var source in definition.Sources) workspace.Sources[source.Path] = source.Text;
        foreach (string imported in definition.Usings) workspace.Usings.Add(imported);
        return workspace.ValidateCompilation().FirstOrDefault(d => d.IsError)?.Message;
    }

    string? ExtractionRoot(string path) => (_signatureRoots ?? (_root is null ? [] : new[] { _root }))
        .Where(root => UnderExtractionRoot(path, root)).OrderByDescending(root => root.Length).FirstOrDefault();

    InputSnapshot ExtractionInputs(string root)
    {
        var overlays = _documents.Keys.Concat(_hosts.Keys).Concat(_unserved.Keys).Concat(_signatureOwnedOpen)
            .Distinct().Where(uri => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile && UnderExtractionRoot(parsed.LocalPath, root))
            .Select(uri => new DocumentInput(new Uri(uri).LocalPath, TextOf(uri) ?? _signatureInputs[uri].Text, ExtractionVersion(new Uri(uri).LocalPath) ?? 0));
        return WorkspaceInputReader.Read(root, overlays);
    }

    int? ExtractionVersion(string path)
    {
        string uri = new Uri(path).AbsoluteUri;
        return VersionOf(uri) ?? (_unserved.TryGetValue(uri, out var unserved) ? unserved.Version : null);
    }

    static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool UnderExtractionRoot(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool ExtractionLinkedPath(string root, string path)
    {
        if (new FileInfo(path).LinkTarget is not null) return true;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null) return true;
            if (SamePath(directory.FullName, root)) break;
        }
        return false;
    }

    static string? ExtractionDestination(string root, string path, InputSnapshot inputs)
    {
        if (!UnderExtractionRoot(path, root) || !path.EndsWith(".ngr", StringComparison.OrdinalIgnoreCase))
            return "Choose a new .ngr file inside the workspace.";
        string? parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent)) return "Choose an existing destination folder.";
        if (ExtractionLinkedPath(root, path)) return "A destination through a symbolic link is not supported.";
        if (File.Exists(path) || Directory.Exists(path) || inputs.Files.Keys.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) ||
            Directory.EnumerateFileSystemEntries(parent).Any(p => string.Equals(Path.GetFileName(p), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
            return "The destination already exists; extraction never overwrites files.";
        return null;
    }

    static bool ExtractionFailure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or NotSupportedException or System.Text.RegularExpressions.RegexMatchTimeoutException;
}
