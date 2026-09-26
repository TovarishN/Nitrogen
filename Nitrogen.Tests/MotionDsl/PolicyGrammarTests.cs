using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Policy.ngr accepts the whole .policy / .compose corpus, and the tail MotionLexer lexes.</summary>
public class PolicyGrammarTests
{
    static TheoryData<string> Data(IReadOnlyList<string> files)
    {
        var data = new TheoryData<string>();
        foreach (string file in files) data.Add(file);
        if (data.Count == 0) data.Add("");
        return data;
    }

    public static TheoryData<string> PolicyCorpusFiles() => Data(MotionCorpus.PolicyFiles());

    public static TheoryData<string> ComposeCorpusFiles() => Data(MotionCorpus.ComposeFiles());

    static void AssertParses(string text, Rule start, string what)
    {
        using var result = NitrogenPolicyParser.Language.Parse(text, start);
        Assert.True(result.Success, result.Success ? "" : $"{what}: {result.FormatMessage(result.Diagnostics[0])} at {result.Diagnostics[0].Span}");
        Assert.False(result.HasErrors);
    }

    [Theory]
    [MemberData(nameof(PolicyCorpusFiles))]
    public void Generated_parser_accepts_every_policy_file(string path)
    {
        if (path.Length == 0) return;
        AssertParses(File.ReadAllText(path), PolicyModule.PolicyDocument, path);
    }

    [Theory]
    [MemberData(nameof(ComposeCorpusFiles))]
    public void Generated_parser_accepts_every_compose_file(string path)
    {
        if (path.Length == 0) return;
        AssertParses(File.ReadAllText(path), PolicyModule.ComposeDocument, path);
    }

    [Fact]
    public void Policy_corpus_is_present_on_this_machine()
    {
        Assert.True(MotionCorpus.PolicyFiles().Count >= 23, $"expected the .policy corpus under {MotionCorpus.Root}");
        Assert.True(MotionCorpus.ComposeFiles().Count >= 1, $"expected the .compose corpus under {MotionCorpus.Root}");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" trailing 1.5 .. >= <= == != \"x\" { } [ ] ( ) + - * / ? : $ . = < > , // comment")]
    [InlineData(" 3easing 1e5 1.x \"unterminated")]
    public void Anything_MotionLexer_lexes_may_follow_the_document(string tail) =>
        AssertParses("compose c { }" + tail, PolicyModule.ComposeDocument, tail);
}
