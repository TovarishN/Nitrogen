using System.Text.Json;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DocumentCheckTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-check-").FullName;
    const string Grammar = "#!nitrogen grammar\nlanguage L { start M.R; }\nsyntax module M { syntax R = \"allowed\"; }";
    string Write(string name, string text) { string path = Path.Combine(_root, name); File.WriteAllText(path, text); return path; }
    async Task<(int Code, string Output)> Run(params string[] args)
    {
        var output = new StringWriter();
        return (await NitrogenCli.RunAsync(args, output, CancellationToken.None), output.ToString());
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Parse_infers_start_and_namespace_from_signed_arbitrary_files()
    {
        Write("L.syntax", Grammar);
        string sample = Write("sample.txt", "#!nitrogen language \"./L.syntax\"\nallowed");
        var (code, output) = await Run("parse", "--tree", sample);
        Assert.Equal(0, code);
        Assert.Contains("ok", output);
        Assert.Contains("\"allowed\"", output); // R is a leaf-choice alias: the tree is its literal, not an (R ...) node
    }

    [Theory]
    [InlineData("Page.razor", "<!-- #!nitrogen augment \"./L.syntax\" regions -->\r\n<!-- #!nitrogen region \"./L.syntax\"\r\nforbidden\r\n#!nitrogen end\r\n-->", 3)]
    [InlineData("Host.cs", "// #!nitrogen augment \"./L.syntax\"\nclass C {}", 2)]
    public async Task Native_errors_use_original_locations(string name, string text, int line)
    {
        Write("L.syntax", Grammar);
        string sample = Write(name, text);
        var (code, output) = await Run("check", sample);
        Assert.Equal(1, code);
        Assert.Contains(sample + "(" + line + ",", output);
        Assert.Contains(": error ", output);
    }

    [Fact]
    public async Task Registered_manifest_cannot_read_unregistered_disk_dependencies()
    {
        string grammar = Write("L.syntax", Grammar.Replace("start M.R;", "start M.R; grammars \"./Common.part\";"));
        Write("Common.part", "syntax module Common {}");
        string sample = Write("sample.txt", "#!nitrogen language \"./L.syntax\"\nallowed");
        string manifest = Write("inputs.json", JsonSerializer.Serialize(new { root = _root, files = new[] { grammar, sample }, documents = new[] { sample } }));
        var (code, output) = await Run("check", "--inputs", manifest);
        Assert.Equal(1, code);
        Assert.Contains("missing registered grammar input", output);
        Assert.Contains(": error NGR0402:", output);
    }

    [Fact]
    public async Task Workspace_check_includes_closed_glob_matches_and_respects_excludes()
    {
        Write("L.syntax", Grammar);
        Write("nitrogen.json", "{\"augmentations\":[{\"language\":\"./L.syntax\",\"files\":[\"*.cs\"],\"exclude\":[\"Excluded.cs\"]}]}");
        string checkedPath = Write("Host.cs", "forbidden");
        string excludedPath = Write("Excluded.cs", "forbidden");
        var (code, output) = await Run("check", _root);
        Assert.Equal(1, code);
        Assert.Contains(checkedPath + "(", output);
        Assert.DoesNotContain(excludedPath + "(", output);
    }

    [Fact]
    public async Task Conflicting_explicit_settings_and_malformed_headers_fail()
    {
        string grammar = Write("L.syntax", Grammar);
        string sample = Write("sample.txt", "#!nitrogen language \"./L.syntax\"\nallowed");
        var conflict = await Run("parse", "--grammar", grammar, "--start", "Other.Start", sample);
        Assert.Equal(1, conflict.Code);
        Assert.Contains("conflict", conflict.Output);
        File.WriteAllText(sample, "#!nitrogen language\nallowed");
        Assert.Equal(1, (await Run("check", sample)).Code);
    }

    [Fact]
    public async Task Empty_workspace_is_success_but_missing_target_and_bad_manifest_are_argument_errors()
    {
        Assert.Equal(0, (await Run("check", _root)).Code);
        Assert.Equal(2, (await Run("check", Path.Combine(_root, "missing"))).Code);
        Assert.Equal(2, (await Run("check", "--inputs", Write("bad.json", "{}"))).Code);
    }

    [Fact]
    public async Task Missing_helper_is_a_definition_error()
    {
        Write("L.syntax", Grammar.Replace("start M.R;", "start M.R; sources \"./Missing.cs\";"));
        string sample = Write("sample.txt", "#!nitrogen language \"./L.syntax\"\nallowed");
        var result = await Run("check", sample);
        Assert.Equal(1, result.Code);
        Assert.Contains("missing registered helper input", result.Output);
    }
}
