using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SignatureExampleTests
{
    static string Example(string file) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../examples/Signatures", file));
    [Fact]
    public async Task Signed_DateCalc_example_parses_and_checks()
    {
        var output = new StringWriter();
        Assert.Equal(0, await NitrogenCli.RunAsync(["parse", Example("sample.txt")], output, CancellationToken.None));
        Assert.Contains("DateCalc.Program", output.ToString());
    }
    [Theory]
    [InlineData("Host.cs")]
    [InlineData("Page.razor")]
    public async Task Native_examples_are_clean_and_detect_a_marker_in_original_source(string file)
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-examples-").FullName;
        try
        {
            File.Copy(Example("NativeChecks.syntax"), Path.Combine(root, "NativeChecks.syntax"));
            string text = File.ReadAllText(Example(file));
            string path = Path.Combine(root, file);
            File.WriteAllText(path, text);
            var output = new StringWriter();
            Assert.Equal(0, await NitrogenCli.RunAsync(["check", path], output, CancellationToken.None));
            File.WriteAllText(path, text.Replace("approved text", "NITROGEN_FORBIDDEN", StringComparison.Ordinal));
            Assert.Equal(1, await NitrogenCli.RunAsync(["check", path], output, CancellationToken.None));
            Assert.Contains(": error NC1001:", output.ToString());
        }
        finally { Directory.Delete(root, true); }
    }
}
