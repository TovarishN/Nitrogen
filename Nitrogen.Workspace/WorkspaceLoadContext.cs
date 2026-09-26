using System.Reflection;
using System.Runtime.Loader;

namespace Nitrogen.Workspace;

/// <summary>
/// One compile's collectible context. It loads only the generated assembly; everything else,
/// Nitrogen.Runtime above all, resolves from the default context, so its types are shared with
/// the host.
/// </summary>
internal sealed class WorkspaceLoadContext() : AssemblyLoadContext("NitrogenWorkspace", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
