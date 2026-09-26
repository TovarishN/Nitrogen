using Nitrogen.Ngr;
using Nitrogen.Ngr.Syntax;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// Spec §4's soundness check (issue 235): run directly, the recovery pass must parse every valid
/// corpus file exactly as the fast pass does, without a single repair. A repair here is an unsound
/// commit point.
/// </summary>
public class RecoverySoundnessTests
{
    static void AssertSound(Language language, Rule start, string text, string what)
    {
        using var clean = language.Parse(text, start);
        Assert.True(clean.Success, $"{what} does not parse");
        using var recovering = language.ParseRecovering(text, start);
        Assert.True(recovering.Success, recovering.Success ? "" :
            $"{what}: the recovery pass repaired valid input: {recovering.FormatMessage(recovering.Diagnostics[0])} at {recovering.Diagnostics[0].Span}");
        Assert.Equal(SyntaxDumper.Dump(clean.Tree), SyntaxDumper.Dump(recovering.Tree));
    }

    [Theory]
    [MemberData(nameof(NgrGrammarTests.GrammarFiles), MemberType = typeof(NgrGrammarTests))]
    public void Grammar_files(string file) =>
        AssertSound(NgrParser.Language, NitrogenModule.File, TestGrammarFileTests.ReadGrammar(file), file);
}
