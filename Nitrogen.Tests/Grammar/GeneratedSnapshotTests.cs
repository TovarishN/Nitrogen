using System.Runtime.CompilerServices;
using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// The emitted code for every test grammar is committed under Grammar/Snapshots, so emitter changes
/// show up as reviewable diffs. Regenerate with <c>NITROGEN_UPDATE_SNAPSHOTS=1 dotnet test ...</c>.
/// </summary>
public class GeneratedSnapshotTests
{
    static string SnapshotDirectory([CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");

    public static TheoryData<string> HintNames() =>
        new() { "Calc.g.cs", "Calc.Power.g.cs", "Calc.Clash.g.cs", "Mini.g.cs", "Lexical.g.cs", "Scopes.g.cs", "Lowered.g.cs" };

    [Theory]
    [MemberData(nameof(HintNames))]
    public void Generated_code_matches_the_snapshot(string hintName)
    {
        var result = GrammarCompiler.Compile(GrammarCompilerTests.TestInputs());
        string code = result.Sources.Single(s => s.HintName == hintName).Code;
        string file = Path.Combine(SnapshotDirectory(), hintName + ".txt");

        if (Environment.GetEnvironmentVariable("NITROGEN_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(SnapshotDirectory());
            File.WriteAllText(file, code);
            return;
        }

        Assert.True(File.Exists(file), $"missing snapshot {file}; run with NITROGEN_UPDATE_SNAPSHOTS=1");
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n"), code.Replace("\r\n", "\n"));
    }
}
