using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class GrammarHelperNavigationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-helper-navigation-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;

    static DocumentPosition At(string source, string text, int shift = 0) =>
        new LineMap(source).PositionOf(source.IndexOf(text, StringComparison.Ordinal) + shift);

    [Theory]
    [InlineData("DateCalcLanguage.NoCall", "NoCall", 17)]
    [InlineData("DateCalcLanguage.NoCall", "NoCall", 18)]
    [InlineData("DateCalcLanguage.NoCall", "NoCall", 22)]
    [InlineData("DateCalcLanguage.Fits", "Fits", 18)]
    [InlineData("DateCalcLanguage.Builtin", "Builtin", 18)]
    [InlineData("DateCalcLanguage.NoCall", "DateCalcLanguage", 2)]
    public void DateCalc_helper_references_resolve_to_their_csharp_declarations(string reference, string declaration, int shift)
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        string grammar = File.ReadAllText(Path.Combine(_root, "DateCalc.ngr"));
        string helper = File.ReadAllText(Path.Combine(_root, "DateCalcLanguage.cs"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("DateCalc.ngr"), 1, grammar);
        var target = Assert.Single(service.Definition(Uri("DateCalc.ngr"), At(grammar, reference, shift)));
        Assert.Equal(Uri("DateCalcLanguage.cs"), target.Uri);
        string marker = declaration == "DateCalcLanguage" ? "class DateCalcLanguage" : $" {declaration}(";
        Assert.Equal(At(helper, marker, declaration == "DateCalcLanguage" ? 6 : 1), target.Range.Start);
    }

    [Theory]
    [InlineData("Symbol?.Type")]
    [InlineData("Symbol?.Initializer")]
    [InlineData("Symbol is null")]
    public void Implicit_Symbol_navigates_to_its_generated_csharp_property(string reference)
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DateCalcLanguage")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        string grammar = File.ReadAllText(Path.Combine(_root, "DateCalc.ngr"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("DateCalc.ngr"), 1, grammar);
        var location = Assert.Single(service.Definition(Uri("DateCalc.ngr"), At(grammar, reference, 2)));
        string source = File.ReadAllText(new System.Uri(location.Uri).LocalPath);
        var lines = new LineMap(source);
        int start = lines.OffsetOf(location.Range.Start);
        Assert.Equal("Symbol", source.Substring(start, lines.OffsetOf(location.Range.End) - start));
        string declaration = source.Split('\n')[location.Range.Start.Line];
        Assert.Contains("Symbol => _s.SymbolOf(_node)", declaration);
        Assert.EndsWith(".cs", new System.Uri(location.Uri).LocalPath);
    }

    [Fact]
    public void Generated_Symbol_navigation_does_not_require_csharp_helper_files()
    {
        const string grammar = """
            syntax module M
            {
              symbols { value }
              token Id = ['a'..'z']+;
              syntax Ref = Name:Id references value Name
              {
                out Found : bool = false;
                Found = Symbol is not null;
              }
            }
            """;
        File.WriteAllText(Path.Combine(_root, "M.ngr"), grammar);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "languages": [{ "name": "M", "extensions": [".m"], "grammars": ["M.ngr"], "start": "M.Ref" }] }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("M.ngr"), 1, grammar);
        var location = Assert.Single(service.Definition(Uri("M.ngr"), At(grammar, "Symbol is")));
        string path = new System.Uri(location.Uri).LocalPath;
        Assert.Contains("Symbol => _s.SymbolOf(_node)", File.ReadAllText(path));
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public void Overloads_are_resolved_semantically_and_open_helper_text_updates_the_location()
    {
        const string grammar = """
            syntax module M
            {
              token Word = ['a'..'z']+;
              syntax Hello = Name:Word
              {
                out Message : string = "Helpers.NoCall";
                check TT0001 true : Helpers.NoCall(Name.Text);
              }
            }
            """;
        const string helper = """
            namespace Test.Syntax;
            public static class Helpers
            {
                public static string NoCall(int value) => "number";
                public static string NoCall(string value) => value;
            }
            """;
        File.WriteAllText(Path.Combine(_root, "M.ngr"), grammar);
        File.WriteAllText(Path.Combine(_root, "Helpers.cs"), helper);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "languages": [{ "name": "M", "extensions": [".m"], "grammars": ["M.ngr"],
              "sources": ["Helpers.cs"], "namespace": "Test.Syntax", "start": "M.Hello" }] }
            """);
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("M.ngr"), 1, grammar);
        var position = At(grammar, "Helpers.NoCall(Name", 9);
        var target = Assert.Single(service.Definition(Uri("M.ngr"), position));
        Assert.Equal(At(helper, "NoCall(string"), target.Range.Start);
        Assert.Empty(service.Definition(Uri("M.ngr"), At(grammar, "Helpers.NoCall\"", 9)));

        string edited = "\n\n" + helper;
        service.Open(Uri("Helpers.cs"), 1, edited);
        target = Assert.Single(service.Definition(Uri("M.ngr"), position));
        Assert.Equal(At(edited, "NoCall(string"), target.Range.Start);

        string ambiguous = grammar.Replace("Helpers.NoCall(Name.Text)", "Helpers.NoCall(default)");
        service.Change(Uri("M.ngr"), 2, ambiguous);
        Assert.Empty(service.Definition(Uri("M.ngr"), position));
    }
}
