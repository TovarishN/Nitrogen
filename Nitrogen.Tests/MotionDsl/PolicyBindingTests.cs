using Gravity.MotionDSL.Compiler;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.PolicySyntax;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

/// <summary>Policy.ngr's binding against the .policy/.compose corpus and the hand compilers (issue 237).</summary>
public class PolicyBindingTests(ITestOutputHelper output)
{
    static readonly BindingOracle Oracle = new(NitrogenPolicyParser.Language, Start, NameError);

    static IEnumerable<string> Corpus() => MotionCorpus.PolicyFiles().Concat(MotionCorpus.ComposeFiles());

    static Rule Start(string path) =>
        path.EndsWith(".compose", StringComparison.Ordinal) ? PolicyModule.ComposeDocument : PolicyModule.PolicyDocument;

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.PolicyFiles), MemberType = typeof(RecoverySoundnessTests))]
    [MemberData(nameof(RecoverySoundnessTests.ComposeFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void A_clean_corpus_file_binds_without_diagnostics(string path)
    {
        if (path.Length == 0) return;
        Assert.Empty(Oracle.CleanFileDiagnostics(path));
    }

    [Fact]
    public void Every_compiler_name_error_is_a_binding_diagnostic_at_that_name()
    {
        if (MotionCorpus.PolicyFiles().Count == 0) return;
        var (checkedMutants, failures) = Oracle.CompilerAgreement(Corpus());
        output.WriteLine($"{checkedMutants} mutants gave a compiler name error");
        Assert.True(checkedMutants >= 30, $"only {checkedMutants} mutants gave a compiler name error");
        Assert.True(failures.Count == 0, BindingOracle.Report($"of {checkedMutants} mutants disagree", failures));
    }

    [Fact]
    public void Definitions_and_references_round_trip_over_the_corpus()
    {
        if (MotionCorpus.PolicyFiles().Count == 0) return;
        var (references, failures) = Oracle.RoundTrips(Corpus());
        output.WriteLine($"{references} references round-tripped");
        Assert.True(references >= 100, $"only {references} references in the corpus");
        Assert.True(failures.Count == 0, BindingOracle.Report("round-trip failures", failures));
    }

    [Fact]
    public void Renaming_a_declaration_changes_exactly_the_references_to_it()
    {
        if (MotionCorpus.PolicyFiles().Count == 0) return;
        var (renamed, failures) = Oracle.Renames(Corpus());
        output.WriteLine($"{renamed} declarations renamed");
        Assert.True(renamed >= 20, $"only {renamed} declarations renamed");
        Assert.True(failures.Count == 0, BindingOracle.Report("rename failures", failures));
    }

    /// <summary>True when PolicyParser, PolicyCompiler or ComposeCompiler rejects the text for a name.</summary>
    static (bool, string) NameError(string path, string text)
    {
        try
        {
            var parser = new PolicyParser(new MotionLexer(text).Tokenize());
            if (path.EndsWith(".compose", StringComparison.Ordinal)) ComposeCompiler.Compile(parser.ParseCompose());
            else PolicyCompiler.Compile(parser.ParsePolicy());
            return (false, "");
        }
        catch (Exception e) when (IsNameMessage(e.Message))
        {
            return (true, e.Message);
        }
        catch (Exception)
        {
            return (false, "");
        }
    }

    static bool IsNameMessage(string message) =>
        message.Contains("which is not declared", StringComparison.Ordinal)
        || message.Contains("unknown noise channel", StringComparison.Ordinal)
        || message.Contains("is not a goal channel", StringComparison.Ordinal)
        || message.Contains("unknown goal family", StringComparison.Ordinal)
        || message.Contains("which is not a declared channel", StringComparison.Ordinal)
        || message.Contains("unknown scenario", StringComparison.Ordinal)
        || message.Contains("is not a pose", StringComparison.Ordinal)
        || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
        || message.Contains("declared twice", StringComparison.Ordinal)
        || (message.Contains(" twice", StringComparison.Ordinal) && !message.Contains("owned twice", StringComparison.Ordinal));
}
