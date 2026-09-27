namespace Nitrogen.Cli;

internal sealed record RiderPluginModel(
    string PluginId,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> GrammarPaths,
    string StartRule);

internal sealed record RiderPluginRequest(
    RiderPluginModel Model,
    string OutputDirectory,
    string NitrogenPath,
    IReadOnlyList<RiderBundleInput> Bundles);

internal sealed record RiderBundleInput(string Target, string Path)
{
    public static readonly string[] Targets = ["macos-aarch64", "macos-x64", "linux-x64", "windows-x64"];
}
