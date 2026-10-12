using System.Text.Json;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguageDiscoveryCliTests
{
    [Fact]
    public async Task Bare_names_work_for_programs_regions_and_json_but_not_unregistered_build_inputs()
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-discovery-cli-").FullName;
        try
        {
            string grammar = Path.Combine(root, "Calc.syntax");
            File.WriteAllText(grammar, "#!nitrogen grammar\nlanguage Calc { start M.R; } syntax module M { syntax R = \"ok\"; }");
            string sample = Path.Combine(root, "sample.txt");
            File.WriteAllText(sample, "#!nitrogen language Calc\nok");
            string host = Path.Combine(root, "Host.cs");
            File.WriteAllText(host, "/* #!nitrogen region Calc\nok\n#!nitrogen end\n*/");
            File.WriteAllText(Path.Combine(root,"nitrogen.json"), "{\"augmentations\":[{\"language\":\"Calc\",\"files\":[\"Host.cs\"],\"input\":\"regions\"}]}");
            var output = new StringWriter();
            Assert.Equal(0, await NitrogenCli.RunAsync(["parse", sample], output, CancellationToken.None));
            Assert.Equal(0, await NitrogenCli.RunAsync(["check", root], output, CancellationToken.None));
            string manifest = Path.Combine(root, "inputs.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new { root, files = new[] { sample }, documents = new[] { sample } }));
            Assert.Equal(1, await NitrogenCli.RunAsync(["check", "--inputs", manifest], output, CancellationToken.None));
            Assert.Contains("no registered workspace language named 'Calc'", output.ToString());
        }
        finally { Directory.Delete(root,true); }
    }
}
