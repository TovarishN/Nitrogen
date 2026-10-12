using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

/// <summary>Resolution consumes explicit input snapshots and never performs hidden disk reads.</summary>
public sealed class LanguageDefinitionResolver(WorkspaceLanguageIndex? index = null) : ILanguageDefinitionResolver
{
    readonly WorkspaceLanguageIndex _index = index ?? new();
    readonly object _gate = new();
    InputSnapshot? _indexed;

    public DefinitionResult Resolve(LanguageReference reference, string originPath, InputSnapshot inputs)
    {
        try
        {
            string path;
            if (reference.IsPath) path = DefinitionPaths.RelativeTo(reference.Value, originPath);
            else
                lock (_gate)
                {
                    if (!ReferenceEquals(_indexed, inputs)) { _index.Update(inputs); _indexed = inputs; }
                    var candidates = _index.Find(reference.Value);
                    if (candidates.Count == 0)
                        return new(null, [DefinitionPaths.Error(originPath, $"no registered workspace language named '{reference.Value}'" )]);
                    if (candidates.Count > 1)
                        return new(null, [DefinitionPaths.Error(originPath, $"ambiguous workspace language '{reference.Value}': {string.Join(", ", candidates)}; use a quoted local path")]);
                    path = candidates[0];
                }
            return LanguageDefinitionLoader.Load(path, inputs) with { CandidateId = path };
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, [DefinitionPaths.Error(originPath, error.Message)]);
        }
    }
}
