using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class PackageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-package-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    PackageRequest Request(params string[] targets)
    {
        Write("project/a.ngr", "syntax module Calc { }");
        string config = Write("project/nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        Write("server/nitrogen.dll", "dll");
        Write("server/nitrogen.runtimeconfig.json", "{}");
        var request = PackageInput.ParseRequest(new[] { "package", "--config", config, "--output", Path.Combine(_root, "dist"),
            "--server", Path.Combine(_root, "server") }.Concat(targets).ToArray(), out string error);
        Assert.Equal("", error);
        return request!;
    }

    [Fact]
    public void Both_targets_by_default_or_the_named_ones()
    {
        Assert.Equal((true, true), (Request().VsCode, Request().Rider));
        Assert.Equal((true, false), (Request("--vscode").VsCode, Request("--vscode").Rider));
        Assert.Equal((false, true), (Request("--rider").VsCode, Request("--rider").Rider));
    }

    [Theory]
    [InlineData("vscode", "npm")]
    [InlineData("rider", "gradle")]
    public async Task A_missing_tool_is_reported_before_building(string target, string tool)
    {
        var request = Request("--" + target);
        var output = new StringWriter();
        var tools = new Tools(find: name => name == tool ? null : "/bin/" + name, javaHome: () => null);

        int code = await PackageCommand.RunAsync(request, output, CancellationToken.None, tools);

        Assert.Equal(2, code);
        Assert.Contains($"'{tool}'", output.ToString());
        Assert.False(Directory.Exists(Path.Combine(_root, "dist")));
    }

    [Fact]
    public async Task Rider_needs_a_java_runtime()
    {
        var output = new StringWriter();
        var tools = new Tools(find: name => name == "java" ? null : "/bin/" + name, javaHome: () => null);

        Assert.Equal(2, await PackageCommand.RunAsync(Request("--rider"), output, CancellationToken.None, tools));
        Assert.Contains("Java", output.ToString());
    }

    [Theory]
    [InlineData("--output out", "no --config")]
    [InlineData("--config nitrogen.json", "no --output")]
    [InlineData("--config nitrogen.json --output out --self-contained", "unknown option '--self-contained'")]
    public void Missing_or_unknown_options_are_reported(string options, string expected)
    {
        Assert.Null(PackageInput.ParseRequest(new[] { "package" }.Concat(options.Split(' ')).ToArray(), out string error));
        Assert.Equal(expected, error);
    }
}
