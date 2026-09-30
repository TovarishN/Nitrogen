namespace Nitrogen.Cli;

/// <param name="SelfContainedServer">The framework-dependent server to bundle with the language (<c>--self-contained</c>); null for a plugin that runs an installed nitrogen.</param>
internal sealed record RiderPluginRequest(
    LanguagePluginModel Model,
    string OutputDirectory,
    string NitrogenPath,
    IReadOnlyList<RiderBundleInput> Bundles,
    string? SelfContainedServer = null);

internal sealed record RiderBundleInput(string Target, string Path)
{
    public static readonly string[] Targets = ["macos-aarch64", "macos-x64", "linux-x64", "windows-x64"];
}
