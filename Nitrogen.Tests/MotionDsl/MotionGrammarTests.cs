using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Xunit;

namespace Nitrogen.Tests;

public unsafe class MotionGrammarTests
{
    static readonly Language Motion = new LanguageBuilder().Add(MotionModule.Instance).WithTrivia(&MotionTrivia.Skip, MotionTrivia.StartChars).Build();

    /// <summary>The corpus, or a single "" when there is no asset store (the tests then return early).</summary>
    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in MotionCorpus.Files()) data.Add(file);
        if (data.Count == 0) data.Add("");
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Generated_parser_accepts_every_motion_file(string path)
    {
        if (path.Length == 0) return;
        using var result = Motion.Parse(File.ReadAllText(path), MotionModule.File);
        Assert.True(result.Success, result.Success ? "" : $"{path}: {result.FormatMessage(result.Diagnostics[0])} at {result.Diagnostics[0].Span}");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Corpus_is_present_on_this_machine()
    {
        Assert.True(MotionCorpus.Files().Count >= 49, $"expected the .motion corpus under {MotionCorpus.Root}");
    }

    [Theory]
    [InlineData("anchor", -1)]
    [InlineData("anchors", 7)]
    [InlineData("s", -1)]
    [InlineData("s1", 2)]
    [InlineData("motor_bindings", -1)]
    public void Identifier_rejects_every_motion_keyword(string text, int end) =>
        Assert.Equal(end, MotionModule.MatchIdentifier(text, 0));

    [Fact]
    public void Motion_trivia_start_set_covers_every_character_trivia_can_start_at()
    {
        for (int c = 0; c < 0x3100; c++)
        {
            string text = (char)c + "//";
            if (MotionTrivia.Skip(text, 0) > 0)
                Assert.True(MotionTrivia.StartChars.Matches((char)c), $"U+{c:X4} starts trivia but is not in the start set");
        }
        Assert.False(MotionTrivia.StartChars.Matches('p'));
    }

    public static TheoryData<string> SkillCorpusFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in MotionCorpus.SkillFiles()) data.Add(file);
        if (data.Count == 0) data.Add("");
        return data;
    }

    [Theory]
    [MemberData(nameof(SkillCorpusFiles))]
    public void Generated_parser_accepts_every_skill_file(string path)
    {
        if (path.Length == 0) return;
        using var result = Motion.Parse(File.ReadAllText(path), MotionModule.File);
        Assert.True(result.Success, result.Success ? "" : $"{path}: {result.FormatMessage(result.Diagnostics[0])} at {result.Diagnostics[0].Span}");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Skill_corpus_is_present_on_this_machine()
    {
        Assert.True(MotionCorpus.SkillFiles().Count >= 35, $"expected the .skill corpus under {MotionCorpus.Root}");
    }
}
