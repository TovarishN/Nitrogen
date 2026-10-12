using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class ListSpliceNavigationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-list-navigation-").FullName;
    readonly string _grammar = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Grammars", "QuoteLists.ngr"));
    string Uri => new System.Uri(Path.Combine(_root, "QuoteLists.ngr")).AbsoluteUri;
    static DocumentPosition At(string text, string value, int shift = 0) =>
        new LineMap(text).PositionOf(text.IndexOf(value, StringComparison.Ordinal) + shift);

    NitrogenLanguageService Service()
    {
        File.WriteAllText(Path.Combine(_root, "QuoteLists.ngr"), _grammar);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "languages": [ { "name": "QuoteLists", "extensions": [".quote"], "grammars": ["*.ngr"], "start": "QuoteLists.Program" } ] }
            """);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri, 1, _grammar);
        return service;
    }

    [Fact]
    public void List_splice_navigates_and_shares_usages_with_its_own_field()
    {
        using var service = Service();
        var target = Assert.Single(service.Definition(Uri, At(_grammar, "..$Args", 4)));
        Assert.Equal(At(_grammar, "Args:(Expr; \";\")*"), target.Range.Start);
        Assert.Contains("Args", service.Hover(Uri, At(_grammar, "..$Args", 4))!.Markdown);
        Assert.Equal(2, service.References(Uri, At(_grammar, "..$Args", 4), true).Count);
    }

    [Fact]
    public void Completion_filters_scalar_and_list_fields()
    {
        using var service = Service();
        string edited = _grammar.Replace("f(1, ..$Args, 2)", "f(1, ..$Ar, 2)", StringComparison.Ordinal);
        service.Change(Uri, 2, edited);
        Assert.Contains(service.Completion(Uri, At(edited, "..$Ar,", 5)), i => i.Label == "Args");
        edited = edited.Replace("..$Ar,", "$Ar,", StringComparison.Ordinal);
        service.Change(Uri, 3, edited);
        Assert.DoesNotContain(service.Completion(Uri, At(edited, "$Ar,", 3)), i => i.Label == "Args");
    }

    public void Dispose() => Directory.Delete(_root, true);
}
