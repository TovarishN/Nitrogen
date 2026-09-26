using System.Runtime.CompilerServices;
using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// Every grammar's commit points, listed under Grammar/Snapshots/Recovery so analysis changes show
/// up as reviewable diffs (issue 235, spec §4). Regenerate with
/// <c>NITROGEN_UPDATE_SNAPSHOTS=1 dotnet test ...</c>.
/// </summary>
public class CommitSnapshotTests
{
    static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    static string Nitrogen(string relative) => File.ReadAllText(Path.Combine(Here(), "..", "..", relative));

    static string[] Grammars(string name) => name switch
    {
        "Calc" => new[] { "Calc.ngr", "Power.ngr", "Clash.ngr" }.Select(TestGrammarFileTests.ReadGrammar).ToArray(),
        "Mini" => new[] { TestGrammarFileTests.ReadGrammar("Mini.ngr") },
        "Lexical" => new[] { TestGrammarFileTests.ReadGrammar("Lexical.ngr") },
        "Nitrogen" => new[] { Nitrogen("Nitrogen.Ngr/Nitrogen.ngr") },
        _ => throw new ArgumentException(name),
    };

    public static TheoryData<string> Names() => new() { "Calc", "Mini", "Lexical", "Nitrogen" };

    [Theory]
    [MemberData(nameof(Names))]
    public void Commit_points_match_the_snapshot(string name)
    {
        string listing = CommitAnalysisTests.Analyze(Grammars(name)).Dump();
        string directory = Path.Combine(Here(), "Snapshots", "Recovery");
        string file = Path.Combine(directory, name + ".commits.txt");

        if (Environment.GetEnvironmentVariable("NITROGEN_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(file, listing);
            return;
        }

        Assert.True(File.Exists(file), $"missing snapshot {file}; run with NITROGEN_UPDATE_SNAPSHOTS=1");
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n"), listing.Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_listing_names_each_sequence_then_its_sites()
    {
        var listing = CommitAnalysisTests.Analyze("""
            syntax module M
            {
              token Id = ['a'..'z']+;
              syntax File = Items:Item*;
              syntax Item = "item" Name:Id "{" "}";
            }
            """).Dump();
        Assert.Equal(
            "// commit points: 3 committed elements (0 composed) in 1 sequences\n"
            + "Item: \"item\" Name:Id \"{\" \"}\"\n"
            + "  1 Name:Id  static  insert {\"{\"}  sync {Id} @1, {\"{\"} @2, {\"}\"} @3\n"
            + "  2 \"{\"  static  insert {\"}\"}  sync {\"{\"} @2, {\"}\"} @3\n"
            + "  3 \"}\"  static  insert {\"item\", $}  sync {\"}\"} @3\n"
            + "// calls with recovery bits: 0\n",
            listing);
    }

}
