using Nitrogen.Cli;
using Nitrogen.LanguageService;
using Xunit;

namespace Nitrogen.Tests;

public sealed class CSharpQuoteNavigationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-csharp-quotes-").FullName;
    readonly string _grammar = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Grammars", "QuoteLists.ngr"));
    string Uri(string name) => new System.Uri(Path.Combine(_root, name)).AbsoluteUri;
    static DocumentPosition At(string text, string value, int shift = 0) =>
        new LineMap(text).PositionOf(text.IndexOf(value, StringComparison.Ordinal) + shift);

    static string Helper(string literal, string method = "language.CompileQuote") => """
        using Nitrogen;
        using System.Collections.Generic;
        static class Helper
        {
            static void Build(Language language)
            {
                const string Category = "QuoteLists.Expr";
                using var quote = METHOD(
                    text: LITERAL,
                    holes: new Dictionary<string, QuoteHole>
                    {
                        ["Args"] = QuoteHole.List("QuoteLists.Expr"),
                        ["Value"] = QuoteHole.Node("QuoteLists.Expr"),
                    },
                    category: Category);
            }
        }
        """.Replace("LITERAL", literal, StringComparison.Ordinal).Replace("METHOD", method, StringComparison.Ordinal);

    NitrogenLanguageService Service(string helper)
    {
        File.WriteAllText(Path.Combine(_root, "QuoteLists.ngr"), _grammar);
        File.WriteAllText(Path.Combine(_root, "Helper.cs"), helper);
        File.WriteAllText(Path.Combine(_root, "nitrogen.json"), """
            { "languages": [ { "name": "QuoteLists", "extensions": [".quote"], "grammars": ["*.ngr"], "start": "QuoteLists.Program" } ] }
            """);
        var service = new NitrogenLanguageService(LspCommand.Registry());
        service.ConfigureWorkspace(_root);
        service.Open(Uri("Helper.cs"), 1, helper);
        return service;
    }

    [Theory]
    [InlineData("\"f(..$Args, $Value)\"")]
    [InlineData("@\"f(..$Args, $Value)\"")]
    [InlineData("\"\"\"\nf(..$Args, $Value)\n\"\"\"")]
    public void Hole_and_keyword_navigation_use_host_and_grammar_locations(string literal)
    {
        string helper = Helper(literal);
        using var service = Service(helper);
        var target = Assert.Single(service.Definition(Uri("Helper.cs"), At(helper, "..$Args", 4)));
        Assert.Equal(Uri("Helper.cs"), target.Uri);
        Assert.Equal(At(helper, "[\"Args\"]"), target.Range.Start);
        target = Assert.Single(service.Definition(Uri("Helper.cs"), At(helper, "f(..$Args")));
        Assert.Equal(Uri("QuoteLists.ngr"), target.Uri);
        Assert.Equal(At(_grammar, "Call ="), target.Range.Start);
        Assert.Contains("Args", service.Hover(Uri("Helper.cs"), At(helper, "..$Args", 4))!.Markdown);
        Assert.Equal(2, service.References(Uri("Helper.cs"), At(helper, "..$Args", 4), true).Count);
    }

    [Theory]
    [InlineData("..$Ar", "Args", "Value")]
    [InlineData("$Va", "Value", "Args")]
    public void Completion_filters_shape_and_replaces_the_csharp_source_range(string marker, string expected, string excluded)
    {
        string helper = Helper("\"\"\"\n    f(" + marker + ")\n    \"\"\"");
        using var service = Service(helper);
        int nameOffset = marker.LastIndexOf('$') + 1;
        var items = service.Completion(Uri("Helper.cs"), At(helper, marker, marker.Length));
        var item = Assert.Single(items, i => i.Label == expected);
        Assert.Equal(At(helper, marker, nameOffset), item.Replace.Start);
        Assert.Equal(At(helper, marker, marker.Length), item.Replace.End);
        Assert.DoesNotContain(items, i => i.Label == excluded);
    }

    [Fact]
    public void Unsaved_host_offsets_and_regular_escapes_are_mapped()
    {
        string helper = Helper("\"f(\\u002e.$Args, $Value)\"");
        using var service = Service(helper);
        string edited = "\n\n" + helper;
        service.Change(Uri("Helper.cs"), 2, edited);
        Assert.Equal(At(edited, "[\"Args\"]"),
            Assert.Single(service.Definition(Uri("Helper.cs"), At(edited, "$Args", 2))).Range.Start);
    }

    [Fact]
    public void Unrelated_or_dynamic_calls_receive_no_guessed_assistance()
    {
        string helper = Helper("\"f(..$Args)\"", "Other.CompileQuote");
        using var service = Service(helper);
        Assert.Empty(service.Definition(Uri("Helper.cs"), At(helper, "..$Args", 4)));
        helper = Helper("GetText()");
        service.Change(Uri("Helper.cs"), 2, helper);
        Assert.Empty(service.Definition(Uri("Helper.cs"), At(helper, "GetText")));
    }

    [Fact]
    public void Constant_concatenation_maps_each_literal_in_source_order_independently()
    {
        string helper = Helper("\"f(\" + Marker + \", $Value)\"")
            .Replace("const string Category", "const string Marker = \"..$Args\";\n        const string Category", StringComparison.Ordinal);
        using var service = Service(helper);
        Assert.Equal(At(helper, "[\"Args\"]"),
            Assert.Single(service.Definition(Uri("Helper.cs"), At(helper, "..$Args", 4))).Range.Start);
        Assert.Empty(service.Definition(Uri("Helper.cs"), At(helper, " + Marker + ", 4)));
    }

    [Theory]
    [InlineData("f(1 /* ..$Ar */)")]
    [InlineData("f(1 /* ..$Ar")]
    public void Comment_contents_do_not_offer_splice_completion(string template)
    {
        string helper = Helper("\"" + template + "\"");
        using var service = Service(helper);
        Assert.DoesNotContain(service.Completion(Uri("Helper.cs"), At(helper, "..$Ar", 5)), i => i.Label == "Args");
    }

    [Fact]
    public void Keyword_completion_uses_the_actual_template_category()
    {
        string helper = Helper("\"requ\"");
        using var service = Service(helper);
        Assert.Contains(service.Completion(Uri("Helper.cs"), At(helper, "requ", 4)), i => i.Label == "required");
    }


    [Theory]
    [InlineData("\"f(..$Args, $Value)\"")]
    [InlineData("@\"f(..$Args, $Value)\"")]
    [InlineData("\"\"\"f(..$Args, $Value)\"\"\"")]
    [InlineData("\"f(\" + \"..$Args, $Value)\"")]
    public void Pattern_api_navigation_uses_actual_method_identity_and_constant_source_maps(string literal)
    {
        string helper=Helper(literal,"language.CompilePattern").Replace("holes:","captures:");
        using var service=Service(helper);
        Assert.Equal(At(helper,"[\"Args\"]"),Assert.Single(service.Definition(Uri("Helper.cs"),At(helper,"..$Args",4))).Range.Start);
        Assert.Equal(Uri("QuoteLists.ngr"),Assert.Single(service.Definition(Uri("Helper.cs"),At(helper,"f("))).Uri);
        string unrelated=helper.Replace("language.CompilePattern","Other.CompilePattern");
        service.Change(Uri("Helper.cs"),2,unrelated);
        Assert.Empty(service.Definition(Uri("Helper.cs"),At(unrelated,"..$Args",4)));
    }

    [Fact]
    public void Pattern_diagnostics_map_to_the_host_string()
    {
        string helper=Helper("\"f(..$Args, ..$Args, $Value)\"","language.CompilePattern").Replace("holes:","captures:");
        using var service=Service(helper);
        var diagnostic=Assert.Single(service.Diagnostics(Uri("Helper.cs")),error=>error.Code=="NQP0003");
        Assert.Equal(At(helper,"..$Args"),diagnostic.Range.Start);
    }


    [Fact]
    public void A_source_type_named_Nitrogen_Language_is_not_the_runtime_API()
    {
        string helper=Helper("\"f(..$Args, $Value)\"","language.CompilePattern").Replace("holes:","captures:") +
            "\nnamespace Nitrogen { public sealed class Language { public PatternCompilation CompilePattern(string category, string text, IReadOnlyDictionary<string,QuoteHole> captures) => throw new System.Exception(); } }";
        using var service=Service(helper);
        Assert.Empty(service.Definition(Uri("Helper.cs"),At(helper,"..$Args",4)));
        Assert.DoesNotContain(service.Diagnostics(Uri("Helper.cs")),error=>error.Code.StartsWith("NQP",StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
