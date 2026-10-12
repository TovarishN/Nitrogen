using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SignedDocumentTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-signed-").FullName;
    const string Grammar = """
        #!nitrogen grammar
        language Names { start Names.Program; }
        syntax module Names {
            symbols { value }
            token Id = ['a'..'z']+ except "let" "use";
            syntax Program = Items:Statement*;
            syntax Statement = Declare / Reference;
            syntax Declare = "let" Name:Id ";" declares value Name in file export;
            syntax Reference = "use" Name:Id ";" references value Name;
        }
        """;
    string UriOf(string name) => new Uri(Path.Combine(_root, name)).AbsoluteUri;
    NitrogenLanguageService Service()
    {
        File.WriteAllText(Path.Combine(_root, "Names.syntax"), Grammar);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        return service;
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Signed_arbitrary_extension_resolves_names_and_completes()
    {
        using var service = Service();
        const string text = "#!nitrogen language \"./Names.syntax\"\nlet item; use item;";
        string uri = UriOf("sample.txt");
        service.Open(uri, 1, text);
        Assert.Empty(service.Diagnostics(uri));
        var lines = new LineMap(text);
        var target = Assert.Single(service.Definition(uri, lines.PositionOf(text.LastIndexOf("item", StringComparison.Ordinal))));
        Assert.Equal(text.IndexOf("item", StringComparison.Ordinal), lines.OffsetOf(target.Range.Start));
        Assert.Contains(service.Completion(uri, lines.PositionOf(text.LastIndexOf("item", StringComparison.Ordinal) + 2)), i => i.Label == "item");
    }

    [Fact]
    public void Primary_signed_programs_share_binding_with_other_programs()
    {
        using var service = Service();
        service.Open(UriOf("defs.txt"), 1, "#!nitrogen language \"./Names.syntax\"\nlet item;");
        const string use = "#!nitrogen language \"./Names.syntax\"\nuse item;";
        service.Open(UriOf("use.txt"), 1, use);
        Assert.Empty(service.Diagnostics(UriOf("use.txt")));
        Assert.Equal(UriOf("defs.txt"), Assert.Single(service.Definition(UriOf("use.txt"), new DocumentPosition(1, 5))).Uri);
    }

    [Fact]
    public void Removing_signature_detaches_features_and_old_diagnostics()
    {
        using var service = Service();
        string uri = UriOf("sample.txt");
        service.Open(uri, 1, "#!nitrogen language \"./Names.syntax\"\nuse unknown;");
        Assert.Contains(service.Diagnostics(uri), d => d.Code == "NB0001");
        service.Change(uri, 2, "plain text");
        Assert.Empty(service.Diagnostics(uri));
        Assert.Empty(service.Definition(uri, new DocumentPosition(0, 1)));
        service.Change(uri, 1, "#!nitrogen language \"./Names.syntax\"\nuse unknown;");
        Assert.Empty(service.Diagnostics(uri));
        Assert.Equal(2, service.VersionOf(uri));
    }

    [Fact]
    public void Signed_grammar_start_metadata_navigates_to_its_rule()
    {
        using var service = Service();
        string uri = UriOf("Names.syntax");
        service.Open(uri, 1, Grammar);
        int offset = Grammar.IndexOf("Names.Program", StringComparison.Ordinal) + 7;
        var target = Assert.Single(service.Definition(uri, new LineMap(Grammar).PositionOf(offset)));
        Assert.Equal(Grammar.IndexOf("Program =", StringComparison.Ordinal), new LineMap(Grammar).OffsetOf(target.Range.Start));
    }

    [Fact]
    public void Signed_grammar_has_parser_features_without_ngr_extension()
    {
        using var service = Service();
        service.Open(UriOf("Names.syntax"), 1, Grammar);
        Assert.Empty(service.Diagnostics(UriOf("Names.syntax")));
        Assert.NotEmpty(service.DocumentSymbols(UriOf("Names.syntax")));
    }
}
