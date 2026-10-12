using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguageDiscoveryLspTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-name-lsp-").FullName;
    const string Grammar = "#!nitrogen grammar\nlanguage Calc { start M.R; } syntax module M { syntax R = \"ok\"; }";
    string PathOf(string file) => Path.Combine(_root, file);
    string UriOf(string file) => new Uri(PathOf(file)).AbsoluteUri;
    public void Dispose() => Directory.Delete(_root, true);
    [Fact]
    public void Unsaved_renames_additions_and_deletions_reclassify_without_restart()
    {
        File.WriteAllText(PathOf("Calc.syntax"), Grammar);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(UriOf("sample.txt"), 1, "#!nitrogen language Calc\nok");
        Assert.Empty(service.Diagnostics(UriOf("sample.txt")));
        service.Open(UriOf("Calc.syntax"), 1, Grammar.Replace("language Calc", "language Calendar"));
        Assert.Contains(service.Diagnostics(UriOf("sample.txt")), d => d.Message.Contains("no registered workspace language", StringComparison.Ordinal));
        service.Close(UriOf("Calc.syntax"));
        Assert.Empty(service.Diagnostics(UriOf("sample.txt")));
        File.WriteAllText(PathOf("Duplicate.syntax"), Grammar);
        service.FileChanged(PathOf("Duplicate.syntax"));
        Assert.Contains(service.Diagnostics(UriOf("sample.txt")), d => d.Message.Contains("ambiguous", StringComparison.Ordinal));
        Assert.Empty(service.Completion(UriOf("sample.txt"), new DocumentPosition(1, 1)));
        File.Delete(PathOf("Duplicate.syntax"));
        service.FileChanged(PathOf("Duplicate.syntax"));
        Assert.Empty(service.Diagnostics(UriOf("sample.txt")));
        File.Move(PathOf("Calc.syntax"), PathOf("Renamed.syntax"));
        service.FileChanged(PathOf("Calc.syntax"));
        service.FileChanged(PathOf("Renamed.syntax"));
        Assert.Empty(service.Diagnostics(UriOf("sample.txt")));
        File.Delete(PathOf("Renamed.syntax"));
        service.FileChanged(PathOf("Renamed.syntax"));
        Assert.Contains(service.Diagnostics(UriOf("sample.txt")), d => d.Code == "NGR0402");
        Assert.DoesNotContain(service.Diagnostics(UriOf("sample.txt")), d => d.Code == "NGR0405");
    }
}
