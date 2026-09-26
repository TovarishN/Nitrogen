using System.Runtime.CompilerServices;
using Nitrogen.Binding;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Nitrogen.MotionDsl.Syntax;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>
/// The same .ngr through the workspace and through the build-time generator: every corpus file
/// must give the same tree and the same diagnostics (issue 236).
/// </summary>
public unsafe class WorkspaceEquivalenceTests
{
    static void UseMotionTrivia(LanguageBuilder builder) =>
        builder.WithTrivia(&Nitrogen.MotionDsl.MotionTrivia.Skip, Nitrogen.MotionDsl.MotionTrivia.StartChars);

    static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    static WorkspaceSnapshot Compiled(string file)
    {
        var workspace = new GrammarWorkspace(UseMotionTrivia);
        // Motion.ngr's semantics blocks use MotionTypes (issue 239).
        workspace.Usings.Add("Nitrogen.MotionDsl");
        workspace.References.Add(typeof(SkillType).Assembly);
        workspace.SetGrammar(file, File.ReadAllText(Path.Combine(Here(), "..", "..", "Nitrogen.MotionDsl", file)));
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    static readonly Lazy<WorkspaceSnapshot> s_motion = new(() => Compiled("Motion.ngr"));
    static readonly Lazy<WorkspaceSnapshot> s_policy = new(() => Compiled("Policy.ngr"));

    static string[] Messages(ParseResult result) =>
        result.Diagnostics.ToArray().Select(d => result.FormatMessage(d) + " " + d.Span).ToArray();

    static void AssertSame(Language built, Rule builtRule, WorkspaceSnapshot snapshot, string rule, string text)
    {
        using var expected = built.Parse(text, builtRule);
        using var actual = snapshot.Parse(text, rule);
        Assert.Equal(SyntaxDumper.Dump(expected.Tree), SyntaxDumper.Dump(actual.Tree));
        Assert.Equal(Messages(expected), Messages(actual));
    }

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.MotionFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void Motion_and_skill_files(string path)
    {
        if (path.Length == 0) return;
        AssertSame(NitrogenMotionParser.Language, MotionModule.File, s_motion.Value, "Motion.File", File.ReadAllText(path));
    }

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.PolicyFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void Policy_files(string path)
    {
        if (path.Length == 0) return;
        AssertSame(NitrogenPolicyParser.Language, PolicyModule.PolicyDocument, s_policy.Value, "Policy.PolicyDocument", File.ReadAllText(path));
    }

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.ComposeFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void Compose_files(string path)
    {
        if (path.Length == 0) return;
        AssertSame(NitrogenPolicyParser.Language, PolicyModule.ComposeDocument, s_policy.Value, "Policy.ComposeDocument", File.ReadAllText(path));
    }

    [Fact]
    public void Broken_input_recovers_the_same_way()
    {
        AssertSame(NitrogenMotionParser.Language, MotionModule.File, s_motion.Value, "Motion.File",
            "body b { part root box(1, 1 } mass = (1 + 2 }");
    }

    /// <summary>Declarations, references with what they resolve to, and diagnostics: everything binding produces.</summary>
    static string[] BindingDump(Language language, ParseResult result)
    {
        var project = new Project(language);
        var binding = project.Set("f", result.Tree);
        return binding.Declarations.Select(d => $"decl {d.Kind} {d.Name} {d.NameSpan}")
            .Concat(binding.References.Select(r =>
                $"ref {string.Join("|", r.Kinds)} {r.Name} {r.NameSpan} -> "
                + string.Join(",", project.Resolve(r).Select(s => s.IsBuiltin ? "builtin" : s.NameSpan.ToString()))))
            .Concat(project.Diagnostics("f").Select(d => d.ToString()))
            .ToArray();
    }

    static void AssertSameBinding(Language built, Rule builtRule, WorkspaceSnapshot snapshot, string rule, string text)
    {
        using var expected = built.Parse(text, builtRule);
        using var actual = snapshot.Parse(text, rule);
        Assert.Equal(BindingDump(built, expected), BindingDump(snapshot.Language!, actual));
    }

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.MotionFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void Motion_and_skill_files_bind_the_same(string path)
    {
        if (path.Length == 0) return;
        string text = File.ReadAllText(path);
        AssertSameBinding(NitrogenMotionParser.Language, MotionModule.File, s_motion.Value, "Motion.File", text);
        // And with a binding error: a behavior that inherits nothing declared.
        AssertSameBinding(NitrogenMotionParser.Language, MotionModule.File, s_motion.Value, "Motion.File",
            text + "\nbehavior extra { inherit nowhere }\n");
    }

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.PolicyFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void Policy_files_bind_the_same(string path)
    {
        if (path.Length == 0) return;
        AssertSameBinding(NitrogenPolicyParser.Language, PolicyModule.PolicyDocument, s_policy.Value, "Policy.PolicyDocument",
            File.ReadAllText(path));
    }
}
