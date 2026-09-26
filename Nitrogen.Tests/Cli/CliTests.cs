using System.Globalization;
using Nitrogen.Cli;
using Nitrogen.Grammar;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The `nitrogen` command core on a temporary directory (issue 236, spec §6).</summary>
public class CliTests
{
    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("nitrogen-cli-").FullName;

        public string Write(string name, string text)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    static string Broken => WorkspaceTests.Greet.Replace("Name:Word;", "Name:;");

    static string[] Lines(StringWriter output) =>
        output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    static async Task<(int Code, string[] Lines)> Run(params string[] args)
    {
        var output = new StringWriter();
        int code = await NitrogenCli.RunAsync(args, output, CancellationToken.None);
        return (code, Lines(output));
    }

    static GrammarSession Session(StringWriter output, params string[] args) =>
        new(CliOptions.Parse(args, out string error) ?? throw new ArgumentException(error), output);

    [Fact]
    public async Task A_first_run_compiles_and_parses_every_sample()
    {
        using var dir = new TempDirectory();
        dir.Write("greet.ngr", WorkspaceTests.Greet);
        string ok = dir.Write("ok.txt", "hello bob");
        var (code, lines) = await Run("parse", "--grammar", dir.Path, "--start", "Greet.Hello", "--tree", ok);
        Assert.Equal(0, code);
        Assert.StartsWith("compiled 1 grammar (version 1) in ", lines[0]);
        Assert.StartsWith($"{ok}: ok in ", lines[1]);
        Assert.Equal("(Hello \"hello\" Word:\"bob\")", lines[2]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task Timings_print_the_same_in_every_culture()
    {
        using var dir = new TempDirectory();
        dir.Write("greet.ngr", WorkspaceTests.Greet);
        string ok = dir.Write("ok.txt", "hello bob");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var (_, lines) = await Run("parse", "-g", dir.Path, "-s", "Greet.Hello", ok);
            Assert.Matches(@": ok in \d+\.\d ms$", lines[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_grammar_edit_shows_in_the_next_run()
    {
        using var dir = new TempDirectory();
        string grammar = dir.Write("greet.ngr", WorkspaceTests.Greet);
        string sample = dir.Write("ok.txt", "hello bob");
        var output = new StringWriter();
        using var session = Session(output, "watch", "-g", grammar, "-s", "Greet.Hello", sample);
        Assert.Equal(0, session.Run());

        File.WriteAllText(grammar, WorkspaceTests.Greet.Replace("Name:Word;", "Name:Word \"!\";"));
        output.GetStringBuilder().Clear();
        Assert.Equal(1, session.Run());
        var lines = Lines(output);
        Assert.StartsWith("compiled 1 grammar (version 2) in ", lines[0]);
        Assert.StartsWith($"{sample}: ", lines[1]);
        Assert.DoesNotContain(": ok in ", lines[1]);
        Assert.Contains(lines, l => l.StartsWith($"{sample}(1,10): error: expected ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_compile_error_prints_where_and_skips_the_samples()
    {
        using var dir = new TempDirectory();
        string grammar = dir.Write("greet.ngr", Broken);
        string sample = dir.Write("ok.txt", "hello bob");
        var (code, lines) = await Run("parse", "-g", grammar, "-s", "Greet.Hello", sample);
        Assert.Equal(1, code);
        Assert.StartsWith("compile failed (version 1) in ", lines[0]);
        Assert.Contains(lines, l => l.StartsWith($"{grammar}(4,", StringComparison.Ordinal) && l.Contains($"error {GrammarCodes.Syntax}:"));
        Assert.DoesNotContain(lines, l => l.StartsWith(sample, StringComparison.Ordinal));
    }

    [Fact]
    public void A_fix_after_a_failed_compile_recovers()
    {
        using var dir = new TempDirectory();
        string grammar = dir.Write("greet.ngr", Broken);
        string sample = dir.Write("ok.txt", "hello bob");
        var output = new StringWriter();
        using var session = Session(output, "watch", "-g", dir.Path, "-s", "Greet.Hello", sample);
        Assert.Equal(1, session.Run());

        File.WriteAllText(grammar, WorkspaceTests.Greet);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, session.Run());
        Assert.StartsWith("compiled 1 grammar (version 2) in ", Lines(output)[0]);
    }

    [Fact]
    public async Task Unusable_arguments_print_the_usage_and_exit_2()
    {
        var (code, lines) = await Run("parse", "-g", "greet.ngr");
        Assert.Equal(2, code);
        Assert.Equal("error: no --start", lines[0]);
        Assert.StartsWith("usage: nitrogen parse", lines[1]);
    }

    [Fact]
    public async Task A_missing_grammar_or_rule_exits_1()
    {
        using var dir = new TempDirectory();
        string missing = Path.Combine(dir.Path, "nope.ngr");
        var (code, lines) = await Run("parse", "-g", missing, "-s", "Greet.Hello");
        Assert.Equal(1, code);
        Assert.Equal($"error: no grammar file or directory '{missing}'", lines[0]);

        dir.Write("greet.ngr", WorkspaceTests.Greet);
        (code, lines) = await Run("parse", "-g", dir.Path, "-s", "Greet.Nope");
        Assert.Equal(1, code);
        Assert.Equal("error: no rule 'Greet.Nope'", lines[^1]);
    }

    [Fact]
    public async Task Bind_reports_binding_diagnostics_and_what_references_resolve_to()
    {
        using var dir = new TempDirectory();
        dir.Write("scopes.ngr", TestGrammarFileTests.ReadGrammar("Scopes.ngr"));
        string good = dir.Write("good.txt", "unit a { let x = 1; let y = x + pi; }");
        string bad = dir.Write("bad.txt", "unit b { let z = q; }");
        var (code, lines) = await Run("parse", "-g", dir.Path, "-s", "Scopes.File", "--bind", "--tree", good, bad);
        Assert.Equal(1, code);
        Assert.Contains($"{bad}(1,18): error NB0001: unresolved value or func 'q'", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith($"{good}(", StringComparison.Ordinal));
        Assert.Contains($"{good} references:", lines);
        Assert.Contains("  x (1,29) -> value x (1,14)", lines);
        Assert.Contains("  pi (1,33) -> builtin value pi", lines);
        Assert.Contains("  q (1,18) -> unresolved", lines);
    }

    [Fact]
    public async Task Without_bind_nothing_is_bound()
    {
        using var dir = new TempDirectory();
        dir.Write("scopes.ngr", TestGrammarFileTests.ReadGrammar("Scopes.ngr"));
        string bad = dir.Write("bad.txt", "unit b { let z = q; }");
        var (code, lines) = await Run("parse", "-g", dir.Path, "-s", "Scopes.File", bad);
        Assert.Equal(0, code);
        Assert.DoesNotContain(lines, l => l.Contains("NB0001", StringComparison.Ordinal));
    }
}
