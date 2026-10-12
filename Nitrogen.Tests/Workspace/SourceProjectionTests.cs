using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SourceProjectionTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nitrogen-projection-tests"));
    static ProjectionResult Extract(string name, string text)
    {
        string path = Path.Combine(Root, name), grammar = Path.Combine(Root, "Calc.syntax");
        var input = new DocumentInput(path, text, 1);
        var inputs = new InputSnapshot(Root, new Dictionary<string, DocumentInput> {
            [path] = input, [grammar] = new(grammar, "#!nitrogen grammar\nlanguage Calc { start M.R; } syntax module M { syntax R = \"bad\"; }", 1) }, true);
        return RegionExtractor.Extract(input, new LanguageDefinitionResolver(), inputs);
    }

    [Theory]
    [InlineData("Host.cs", "/*", "*/")]
    [InlineData("Page.razor", "<!--", "-->")]
    public void Extracts_exact_bodies_and_maps_utf16_crlf_locations(string file, string open, string close)
    {
        string text = "// 😀\r\n" + open + " #!nitrogen region \"./Calc.syntax\"\r\nbad\r\n#!nitrogen end\r\n" + close;
        var result = Extract(file, text);
        Assert.Empty(result.Diagnostics);
        var region = Assert.Single(result.Inputs);
        Assert.Equal("bad\r\n", region.Projection.Text);
        Assert.Equal(new AnalysisLocation(Path.Combine(Root, file), text.IndexOf("bad", StringComparison.Ordinal), 3), region.Projection.Map(0, 3));
        Assert.Equal("Calc", region.Selection.Definition.Name);
    }

    [Fact]
    public void Multiple_regions_keep_distinct_offsets_and_keys()
    {
        const string region = "/* #!nitrogen region \"./Calc.syntax\"\nbad\n#!nitrogen end\n*/";
        var result = Extract("Host.cs", region + "\nclass C {}\n" + region);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Inputs.Count);
        Assert.NotEqual(result.Inputs[0].Selection.Key, result.Inputs[1].Selection.Key);
        Assert.NotEqual(result.Inputs[0].Projection.HostStart, result.Inputs[1].Projection.HostStart);
    }

    [Theory]
    [InlineData("/* #!nitrogen region \"./Calc.syntax\"\nbad\n*/")]
    [InlineData("/* #!nitrogen region \"./Calc.syntax\"\nbad\n#!nitrogen end\n")]
    [InlineData("/* #!nitrogen end\n*/")]
    [InlineData("/* #!nitrogen region \"./Calc.syntax\"\n#!nitrogen region \"./Calc.syntax\"\nbad\n#!nitrogen end\n*/")]
    [InlineData("/* #!nitrogen region \"./Calc.syntax\"\n#!nitrogen end\n*/")]
    [InlineData("/* #!nitrogen region \"./Calc.syntax\"\nbad\n#!nitrogen end extra\n*/")]
    [InlineData("// #!nitrogen region \"./Calc.syntax\"\nbad\n// #!nitrogen end")]
    public void Invalid_regions_have_local_diagnostics(string text)
    {
        var result = Extract("Host.cs", text);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, issue => Assert.InRange(issue.Location.Start, 0, text.Length));
        Assert.Empty(result.Inputs);
    }

    [Fact]
    public void Razor_markup_inside_control_flow_keeps_comments_after_apostrophes()
    {
        const string text = "@if (true) { <div>I'm here<!-- #!nitrogen region \"./Calc.syntax\"\nbad\n#!nitrogen end\n--></div> }";
        var result = Extract("Page.razor", text);
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Inputs);
    }

    [Theory]
    [InlineData("Host.cs", "var s = \"/* #!nitrogen region \\\"./Calc.syntax\\\" */\";")]
    [InlineData("Host.cs", "var s = @\"/* #!nitrogen region \"\"./Calc.syntax\"\" */\";")]
    [InlineData("Host.cs", "var s = \"\"\"\n/* #!nitrogen region \"./Calc.syntax\"\nbad\n#!nitrogen end\n*/\n\"\"\";")]
    [InlineData("Page.razor", "@{ var s = \"<!-- #!nitrogen region \\\"./Calc.syntax\\\" -->\"; }")]
    [InlineData("Page.razor", "<div title='<!-- #!nitrogen region \"./Calc.syntax\" -->'></div>")]
    [InlineData("Page.razor", "@* <!-- #!nitrogen region \"./Calc.syntax\" --> *@")]
    [InlineData("Page.razor", "@code { string s = \"\"\"\n<!-- #!nitrogen region \"./Calc.syntax\"\nbad\n#!nitrogen end\n-->\n\"\"\"; }")]
    [InlineData("Page.razor", "@{ var s = $\"<!-- #!nitrogen region {1} -->\"; }")]
    public void Native_strings_attributes_and_razor_comments_do_not_activate_regions(string file, string text)
    {
        var result = Extract(file, text);
        Assert.Empty(result.Inputs);
        Assert.Empty(result.Diagnostics);
    }
}
