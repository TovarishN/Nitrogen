extern alias generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Nitrogen.Tests;

public sealed class SignedGeneratorTests
{
    sealed class Input(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }
    sealed class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
    sealed class Config(string? ns = null) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => new Options(new Dictionary<string, string> { ["build_property.RootNamespace"] = "Legacy" });
        public override AnalyzerConfigOptions GetOptions(Microsoft.CodeAnalysis.SyntaxTree tree) => new Options(new Dictionary<string, string>());
        public override AnalyzerConfigOptions GetOptions(AdditionalText text) => new Options(ns is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["build_metadata.AdditionalFiles.Namespace"] = ns });
    }
    static string Root(string body = "syntax module M { syntax R = \"ok\"; }") => "#!nitrogen grammar\nlanguage L { start M.R; }\n" + body;
    static string PathOf(string name) => Path.Combine(Path.GetTempPath(), "nitrogen-generator", name);
    static GeneratorDriver Driver(Config? config = null, params AdditionalText[] inputs) => CSharpGeneratorDriver.Create(
        [new generator::Nitrogen.Generator.NitrogenGenerator().AsSourceGenerator()], inputs, optionsProvider: config ?? new Config());
    static GeneratorDriverRunResult Run(GeneratorDriver driver) => driver.RunGenerators(CSharpCompilation.Create("Signed")).GetRunResult();

    [Fact]
    public void Signed_arbitrary_extension_uses_declaration_namespace()
    {
        var result = Run(Driver(null, new Input(PathOf("L.syntax"), Root())));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("namespace L.Syntax", Assert.Single(result.GeneratedTrees, tree => !tree.FilePath.Contains("Composition.g.cs")).ToString());
    }

    [Fact]
    public void Dependencies_are_supplied_and_change_incrementally()
    {
        var root = new Input(PathOf("L.syntax"), "#!nitrogen grammar\nlanguage L { start M.R; grammars \"./Common.part\"; }\nsyntax module M { using Common; syntax R = Common.Word; }");
        var dependency = new Input(PathOf("Common.part"), "syntax module Common { token Word = \"ok\"; }");
        GeneratorDriver driver = Driver(null, root, dependency).RunGenerators(CSharpCompilation.Create("Signed"));
        Assert.Empty(driver.GetRunResult().Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Equal(3, driver.GetRunResult().GeneratedTrees.Length);
        var updated = new Input(dependency.Path, "syntax module Common { token Word = \"changed\"; }");
        driver = driver.ReplaceAdditionalText(dependency, updated).RunGenerators(CSharpCompilation.Create("Signed"));
        Assert.Contains("changed", string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(t => t.ToString())));
        driver = driver.RemoveAdditionalTexts([updated]).RunGenerators(CSharpCompilation.Create("Signed"));
        Assert.Contains(driver.GetRunResult().Diagnostics, d => d.GetMessage().Contains("registered", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_extension_dependency_inherits_signed_root_namespace()
    {
        var root = new Input(PathOf("root.syntax"), "#!nitrogen grammar\nlanguage L { start M.R; grammars \"./Common.ngr\"; } syntax module M { syntax R = Common.Word; }");
        var dependency = new Input(PathOf("Common.ngr"), "syntax module Common { token Word = \"ok\"; }");
        var result = Run(Driver(null, dependency, root));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.All(result.GeneratedTrees, tree => Assert.Contains("namespace L.Syntax", tree.ToString()));
    }

    [Fact]
    public void Namespace_metadata_conflicts_are_reported()
    {
        Assert.Contains(Run(Driver(new Config("Conflicting"), new Input(PathOf("L.syntax"), Root()))).Diagnostics,
            d => d.GetMessage().Contains("namespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unsigned_arbitrary_file_is_ignored_and_legacy_namespace_is_preserved()
    {
        Assert.Empty(Run(Driver(null, new Input(PathOf("ignored.txt"), "syntax module Ignored {}"))).GeneratedTrees);
        Assert.Contains("namespace Legacy", Assert.Single(Run(Driver(null, new Input(PathOf("legacy.ngr"), "syntax module M { syntax R = \"ok\"; }"))).GeneratedTrees).ToString());
    }

    [Fact]
    public void Missing_dependency_does_not_read_an_existing_disk_file()
    {
        string directory = Directory.CreateTempSubdirectory("nitrogen-registered-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "Common.part"), "syntax module Common {}");
            var input = new Input(Path.Combine(directory, "L.syntax"), "#!nitrogen grammar\nlanguage L { start M.R; grammars \"./Common.part\"; } syntax module M { syntax R = \"ok\"; }");
            Assert.Contains(Run(Driver(null, input)).Diagnostics, d => d.GetMessage().Contains("registered", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Duplicate_modules_in_signed_roots_are_diagnostics()
    {
        Assert.Contains(Run(Driver(null, new Input(PathOf("a.syntax"), Root()), new Input(PathOf("b.syntax"), Root()))).Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }
}
