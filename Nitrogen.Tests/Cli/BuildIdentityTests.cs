using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class BuildIdentityTests
{
    [Fact]
    public async Task Editor_server_advertises_the_same_identity_as_the_cli()
    {
        using var service = new Nitrogen.LanguageService.NitrogenLanguageService(LanguageServiceTests.ScopesRegistry());
        var (_, messages, _) = await LspServerTests.Session(service,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""",
            """{"jsonrpc":"2.0","method":"exit"}""");
        using var output = new StringWriter();
        await NitrogenCli.RunAsync(["--version"], output, CancellationToken.None);
        Assert.Equal(output.ToString().Trim(),
            messages[0].GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Version_reports_release_build_number_and_commit_without_loading_a_workspace()
    {
        using var output = new StringWriter();
        int code = await NitrogenCli.RunAsync(["--version"], output, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Matches(@"^\d+\.\d+\.\d+\+build\.(?:\d+\.[0-9a-f]{40}|0\.unknown)$", output.ToString().Trim());
    }
}
