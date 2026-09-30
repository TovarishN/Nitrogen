namespace Nitrogen.Cli;

internal sealed record RiderPluginRequest(
    LanguagePluginModel Model,
    string OutputDirectory,
    string NitrogenPath,
    IReadOnlyList<RiderBundleInput> Bundles);

internal sealed record RiderBundleInput(string Target, string Path)
{
    public static readonly string[] Targets = ["macos-aarch64", "macos-x64", "linux-x64", "windows-x64"];
}
