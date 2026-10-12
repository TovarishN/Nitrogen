using Nitrogen.Grammar;

namespace Nitrogen.Workspace;

/// <summary>Local resolution consumes explicit input snapshots and never performs hidden disk reads.</summary>
public sealed class LanguageDefinitionResolver : ILanguageDefinitionResolver
{
    public DefinitionResult Resolve(LanguageReference reference, string originPath, InputSnapshot inputs)
    {
        if (!reference.IsPath)
            return new(null, [DefinitionPaths.Error(originPath, $"language name '{reference.Value}' requires workspace discovery; use a quoted local path")]);
        try
        {
            return LanguageDefinitionLoader.Load(DefinitionPaths.RelativeTo(reference.Value, originPath), inputs);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, [DefinitionPaths.Error(originPath, error.Message)]);
        }
    }
}
