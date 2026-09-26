using Gravity.MotionDSL.Compiler;
using Gravity.MotionDSL.Lexer;
using Gravity.MotionDSL.Parser;
using Nitrogen.MotionDsl;
using Nitrogen.MotionDsl.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Nitrogen.Tests;

/// <summary>Motion.ngr's binding against the corpus and the hand compiler: the three oracles of spec §6 (issue 237).</summary>
public class MotionBindingTests(ITestOutputHelper output)
{
    static readonly BindingOracle Oracle = new(NitrogenMotionParser.Language, _ => MotionModule.File, NameError);

    static IEnumerable<string> Corpus() => MotionCorpus.Files().Concat(MotionCorpus.SkillFiles());

    [Theory]
    [MemberData(nameof(RecoverySoundnessTests.MotionFiles), MemberType = typeof(RecoverySoundnessTests))]
    public void A_clean_corpus_file_binds_without_diagnostics(string path)
    {
        if (path.Length == 0) return;
        Assert.Empty(Oracle.CleanFileDiagnostics(path));
    }

    [Fact]
    public void Every_compiler_name_error_is_a_binding_diagnostic_at_that_name()
    {
        if (MotionCorpus.Files().Count == 0) return;
        var (checkedMutants, failures) = Oracle.CompilerAgreement(Corpus());
        output.WriteLine($"{checkedMutants} mutants gave a compiler name error");
        Assert.True(checkedMutants >= 100, $"only {checkedMutants} mutants gave a compiler name error");
        Assert.True(failures.Count == 0, BindingOracle.Report($"of {checkedMutants} mutants disagree", failures));
    }

    [Fact]
    public void Definitions_and_references_round_trip_over_the_corpus()
    {
        if (MotionCorpus.Files().Count == 0) return;
        var (references, failures) = Oracle.RoundTrips(Corpus());
        output.WriteLine($"{references} references round-tripped");
        Assert.True(references > 1000, $"only {references} references in the corpus");
        Assert.True(failures.Count == 0, BindingOracle.Report("round-trip failures", failures));
    }

    [Fact]
    public void Renaming_a_declaration_changes_exactly_the_references_to_it()
    {
        if (MotionCorpus.Files().Count == 0) return;
        var (renamed, failures) = Oracle.Renames(Corpus());
        output.WriteLine($"{renamed} declarations renamed");
        Assert.True(renamed > 100, $"only {renamed} declarations renamed");
        Assert.True(failures.Count == 0, BindingOracle.Report("rename failures", failures));
    }

    /// <summary>True when the hand parser or compiler rejects the text for a name: unknown, undefined or duplicate.</summary>
    static (bool, string) NameError(string path, string text)
    {
        try
        {
            MotionCompiler.Compile(new MotionParser(new MotionLexer(text).Tokenize()).ParseFile());
            return (false, "");
        }
        catch (SkillCompileException e) when (e.Code is SkillDiagnosticCode.UnknownSymbol or SkillDiagnosticCode.DuplicateDeclaration
            or SkillDiagnosticCode.MissingRequiredMotor or SkillDiagnosticCode.UnknownEasing or SkillDiagnosticCode.InvalidSourceGraph
            || e.Message.StartsWith("Duplicate", StringComparison.Ordinal))
        {
            return (true, e.Message);
        }
        catch (SkillCompileException)
        {
            return (false, "");
        }
        catch (CompileException e) when (e.Message.StartsWith("Cannot inherit", StringComparison.Ordinal)
            || e.Message.StartsWith("Undefined variable", StringComparison.Ordinal)
            || e.Message.StartsWith("Unknown function", StringComparison.Ordinal)
            || e.Message.StartsWith("Duplicate", StringComparison.Ordinal))
        {
            return (true, e.Message);
        }
        catch (ParseException e) when (e.Message.StartsWith("Duplicate", StringComparison.Ordinal))
        {
            return (true, e.Message);
        }
        catch (ArgumentException e) when (e.Message.Contains("Requested value", StringComparison.Ordinal)
            || e.Message.Contains("same key", StringComparison.Ordinal))
        {
            return (true, e.Message); // Enum.Parse of an unknown reward function; a dictionary's duplicate key
        }
        catch (Exception)
        {
            return (false, "");
        }
    }
}
