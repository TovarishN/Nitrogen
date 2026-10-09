using System.Text.Json;
using Nitrogen.LanguageService;
using Nitrogen.LanguageService.Lsp;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Diagnostics of closed workspace files and grammars, checked while the server is idle.</summary>
public sealed class ClosedDiagnosticsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-closed-").FullName;

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    string Uri(string relative) => new System.Uri(Path.Combine(_root, relative)).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    NitrogenLanguageService Service(string grammar = WorkspaceIndexTests.Grammar)
    {
        Write("nitrogen.json", WorkspaceIndexTests.Config);
        Write("links.ngr", grammar);
        var service = new NitrogenLanguageService(new LanguageRegistry());
        service.ConfigureWorkspace(_root);
        service.IndexWorkspace(_root);
        return service;
    }

    [Fact]
    public void A_closed_file_reports_its_errors()
    {
        Write("a.links", "use alpha;");
        using var service = Service();
        Assert.Contains(service.Diagnostics(Uri("a.links")), d => d.Code == "NB0001");
    }

    [Fact]
    public void A_closed_grammar_reports_its_compile_errors()
    {
        using var service = Service(WorkspaceIndexTests.Grammar.Replace("token Word = ['a'..'z']+;", "token Word = ;"));
        Assert.Contains(service.Diagnostics(Uri("links.ngr")), d => d.Severity == ServiceSeverity.Error);
        Assert.Contains(Uri("links.ngr"), service.ClosedDiagnosticFiles());
    }

    [Fact]
    public void Closed_diagnostic_files_are_the_closed_files_and_grammars()
    {
        Write("a.links", "def alpha;");
        Write("b.links", "def beta;");
        using var service = Service();
        service.Open(Uri("b.links"), 1, "def beta;");
        Assert.Equal([Uri("a.links"), Uri("links.ngr")], service.ClosedDiagnosticFiles());
    }
}
