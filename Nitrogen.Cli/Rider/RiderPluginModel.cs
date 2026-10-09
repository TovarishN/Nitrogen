namespace Nitrogen.Cli;

/// <param name="SelfContainedServer">The framework-dependent server to bundle (<c>--self-contained</c>); null for a plugin that runs an installed nitrogen.</param>
/// <param name="CarriesLanguage">Whether the bundle holds the language too (<c>--config</c>), else only the server, which then serves .ngr itself and the workspace's nitrogen.json (<c>--grammar</c>).</param>
internal sealed record RiderPluginRequest(
    LanguagePluginModel Model,
    string OutputDirectory,
    string NitrogenPath,
    IReadOnlyList<RiderBundleInput> Bundles,
    string? SelfContainedServer = null,
    bool CarriesLanguage = true);

internal sealed record RiderBundleInput(string Target, string Path)
{
    public static readonly string[] Targets = ["macos-aarch64", "macos-x64", "linux-x64", "windows-x64"];
}
