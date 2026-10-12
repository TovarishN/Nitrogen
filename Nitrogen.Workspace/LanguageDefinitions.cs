using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

public sealed record DocumentInput(string Path, string Text, long Version);
public sealed record InputSnapshot(string Root, IReadOnlyDictionary<string, DocumentInput> Files, bool RegisteredInputsOnly);
public sealed record AnalysisLocation(string Path, int Start, int Length);
public sealed record AnalysisIssue(string Code, string Message, AnalysisLocation Location,
    IReadOnlyList<AnalysisLocation> Related, string Source, bool IsError, string? Layer = null);
public sealed record LanguageDefinition(string Id, string Name, string Start, string GeneratedNamespace,
    IReadOnlyList<DocumentInput> Grammars, IReadOnlyList<DocumentInput> Sources, IReadOnlyList<string> Usings,
    IReadOnlyList<string> Extensions, IReadOnlyList<LanguagePresentationOption> Presentation);
public sealed record DefinitionResult(LanguageDefinition? Definition, IReadOnlyList<AnalysisIssue> Diagnostics);

public interface ILanguageDefinitionResolver
{
    DefinitionResult Resolve(LanguageReference reference, string originPath, InputSnapshot inputs);
}

internal static class DefinitionPaths
{
    internal static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static string Canonical(string path) => Path.GetFullPath(path);
    internal static string RelativeTo(string reference, string origin) =>
        reference.Contains("://", StringComparison.Ordinal)
            ? throw new ArgumentException("only local language paths are supported")
            : Path.GetFullPath(reference, Path.GetDirectoryName(Canonical(origin))!);
    internal static DocumentInput? Find(InputSnapshot inputs, string path) =>
        inputs.Files.TryGetValue(path, out var found) ? found
        : inputs.Files.Values.FirstOrDefault(f => Comparer.Equals(Canonical(f.Path), path));
    internal static AnalysisIssue Error(string path, string message, GrammarSpan span = default, string code = "NGR0402") =>
        new(code, message, new AnalysisLocation(path, span.Start, span.Length), [], "nitrogen", true);
}
