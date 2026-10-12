using Nitrogen.Grammar;
using Nitrogen.Ngr;
using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguageDeclarationTests
{
    const string Grammar = """
        language DateCalc {
            start DateCalc.Program;
            namespace DateCalc.Syntax;
            grammars "./Common.syntax", "./Units.syntax";
            sources "./DateCalcLanguage.cs", "./MathModule.cs";
            usings System, System.Linq;
            extensions ".datecalc", ".dates";
            tokens value "variable";
            types Core.Scalar "number";
        }
        syntax module DateCalc { syntax Program = "hello"; }
        """;

    [Fact]
    public void Both_parsers_preserve_language_metadata_and_spans()
    {
        var bootstrap = GrammarParser.Parse(Grammar);
        var hosted = NgrParser.Parse(Grammar);
        Assert.True(bootstrap.Success, string.Join("\n", bootstrap.Diagnostics));
        Assert.True(hosted.Success, string.Join("\n", hosted.Diagnostics));
        Assert.Equal(bootstrap.File, hosted.File);
        var declaration = Assert.Single(bootstrap.File!.Languages);
        Assert.Equal("DateCalc", declaration.Name);
        Assert.Equal("DateCalc.Program", declaration.Start);
        Assert.Equal("DateCalc.Syntax", declaration.Namespace);
        Assert.Equal(new[] { "./Common.syntax", "./Units.syntax" }, declaration.Grammars.ToArray());
        Assert.Equal(new[] { "./DateCalcLanguage.cs", "./MathModule.cs" }, declaration.Sources.ToArray());
        Assert.Equal(new[] { "System", "System.Linq" }, declaration.Usings.ToArray());
        Assert.Equal(new[] { ".datecalc", ".dates" }, declaration.Extensions.ToArray());
        Assert.Equal(2, declaration.Presentation.Count);
        Assert.Equal("Core.Scalar", declaration.Presentation[1].Name);
        Assert.Contains("DateCalc.Program", GrammarDumper.Dump(bootstrap.File));
    }

    [Theory]
    [InlineData("language L { }")]
    [InlineData("language L { start M.R; start M.R; }")]
    [InlineData("language L { start M.R; namespace A; namespace B; }")]
    [InlineData("language L { start M.R; sources \"\"; }")]
    [InlineData("language L { start M.R; unknown \"x\"; }")]
    [InlineData("language L { start M.R; } language Q { start M.R; }")]
    [InlineData("language L { start M.R; tokens value \"variable\"; tokens value \"function\"; }")]
    public void Invalid_declarations_fail_in_both_parsers(string declaration)
    {
        string text = declaration + " syntax module M { syntax R = \"x\"; }";
        Assert.False(GrammarParser.Parse(text).Success);
        Assert.False(NgrParser.Parse(text).Success);
    }

    [Fact]
    public void Namespace_can_be_omitted_and_modules_remain_compatible()
    {
        var result = GrammarParser.Parse("language L { start M.R; } syntax module M { syntax R = \"x\"; }");
        Assert.True(result.Success);
        Assert.Null(Assert.Single(result.File!.Languages).Namespace);
        Assert.Empty(GrammarParser.Parse("syntax module M { }").File!.Languages);
    }

    [Fact]
    public void Declaration_formatting_retains_metadata()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        const string uri = "file:///w/a.ngr";
        const string text = "language L {\nstart M.R;\n}\nsyntax module M { syntax R = \"x\"; }";
        service.Open(uri, 1, text);
        string formatted = FormattingTests.Apply(text, service.Format(uri, new FormattingOptions(2, true)));
        Assert.Contains("\n  start M.R;", formatted);
        Assert.Equal(GrammarDumper.Dump(GrammarParser.Parse(text).File!),
            GrammarDumper.Dump(GrammarParser.Parse(formatted).File!));
    }

    [Fact]
    public void Helper_path_navigates_to_original_source()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nitrogen-declaration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string helper = Path.Combine(directory, "Helper.cs");
            File.WriteAllText(helper, "public static class Helper {}");
            using var service = new NitrogenLanguageService(LspCommand.Registry());
            string uri = new Uri(Path.Combine(directory, "test.ngr")).AbsoluteUri;
            const string text = "language L { start M.R; sources \"./Helper.cs\"; } syntax module M { syntax R = \"x\"; }";
            service.Open(uri, 1, text);
            var target = Assert.Single(service.Definition(uri, new LineMap(text).PositionOf(text.IndexOf("Helper.cs", StringComparison.Ordinal))));
            Assert.Equal(new Uri(helper).AbsoluteUri, target.Uri);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Declaration_keywords_complete_and_start_rule_is_navigable()
    {
        using var service = new NitrogenLanguageService(LspCommand.Registry());
        const string uri = "file:///w/a.ngr";
        const string partial = "language DateCalc { sta";
        service.Open(uri, 1, partial);
        Assert.Contains(service.Completion(uri, new LineMap(partial).PositionOf(partial.Length)), i => i.Label == "start");
        const string text = "language L { start M.R; } syntax module M { syntax R = \"x\"; }";
        service.Change(uri, 2, text);
        var definition = Assert.Single(service.Definition(uri, new LineMap(text).PositionOf(text.IndexOf("M.R", StringComparison.Ordinal) + 2)));
        Assert.Equal(text.IndexOf("R =", StringComparison.Ordinal), new LineMap(text).OffsetOf(definition.Range.Start));
    }
}
