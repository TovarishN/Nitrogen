using Xunit;

namespace Nitrogen.Tests;

public class MutatorTests
{
    // Tokens: a@0 =@2 1@4 ;@5 b@12 "s t"@14 (@28 c@29 )@30; the comments hold none.
    const string Text = "a = 1; // c\nb \"s t\" /* x */ (c)\n";

    [Fact]
    public void Tokens_skip_whitespace_and_comments_and_keep_strings_whole()
    {
        Assert.Equal(
            new[] { (0, 1), (2, 1), (4, 1), (5, 1), (12, 1), (14, 5), (28, 1), (29, 1), (30, 1) },
            Mutator.Tokens(Text).ToArray());
    }

    [Fact]
    public void Mutants_are_deterministic_single_edits_of_their_kind()
    {
        var mutants = Mutator.Mutants("x.motion", Text, 4).ToList();
        Assert.Equal(mutants, Mutator.Mutants("x.motion", Text, 4).ToList());
        Assert.Equal(20, mutants.Count);
        var tokens = Mutator.Tokens(Text);
        foreach (var mutant in mutants)
        {
            string inserted = mutant.Text.Substring(mutant.Start, mutant.Inserted);
            Assert.Equal(mutant.Text, Text[..mutant.Start] + inserted + Text[mutant.EditEnd..]);
            switch (mutant.Kind)
            {
                case MutationKind.DeleteToken:
                    Assert.Equal(0, mutant.Inserted);
                    Assert.Contains((mutant.Start, mutant.Removed), tokens);
                    break;
                case MutationKind.InsertToken:
                    Assert.Equal(0, mutant.Removed);
                    Assert.Contains(inserted, new[] { "} ", "; ", "zz ", "7 " });
                    break;
                case MutationKind.DuplicateLine:
                    Assert.Equal(0, mutant.Removed);
                    Assert.EndsWith("\n", inserted);
                    Assert.True(mutant.Start == 0 || Text[mutant.Start - 1] == '\n');
                    break;
                case MutationKind.SwapTokens:
                    Assert.Equal(mutant.Removed, mutant.Inserted);
                    break;
                default:
                    Assert.Equal(0, mutant.Inserted);
                    Assert.Equal(Text.Length, mutant.EditEnd);
                    break;
            }
        }
    }
}
