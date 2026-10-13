extern alias generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Nitrogen.Tests;

public sealed class CompositionGeneratorTests
{
    internal sealed class Input(string name, string text) : AdditionalText
    {
        public override string Path => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "composition-inputs", name);
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) => Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }
    sealed class Options : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = "Demo.Syntax";
            return key == "build_property.RootNamespace";
        }
    }
    sealed class Config : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => new Options();
        public override AnalyzerConfigOptions GetOptions(Microsoft.CodeAnalysis.SyntaxTree tree) => new Options();
        public override AnalyzerConfigOptions GetOptions(AdditionalText text) => new Options();
    }
    internal static GeneratorDriver Driver(params AdditionalText[] inputs) => CSharpGeneratorDriver.Create(
        new[] { new generator::Nitrogen.Generator.NitrogenGenerator().AsSourceGenerator() }, inputs, optionsProvider: new Config());
    internal static GeneratorDriverRunResult Run(params AdditionalText[] inputs) => Driver(inputs)
        .RunGenerators(CSharpCompilation.Create("Factories")).GetRunResult();
    const string ConfigText = """
        {"languages":[
          {"name":"One","start":"A.Program","namespace":"Demo.Syntax","grammars":["a.ngr","shared.ngr"]},
          {"name":"Two","start":"B.Other","namespace":"Demo.Syntax","grammars":["b.ngr","shared.ngr"]}
        ],"custom":{"nested":[true,false,null,-2.5e+3,"escaped\u0020text"]}}
        """;

    [Fact]
    public void Configured_factories_keep_separate_membership_and_execute()
    {
        var result = Run(new Input("nitrogen.json", ConfigText),
            new Input("a.ngr", "syntax module A { syntax Program = Shared.Word \"a\"; }"),
            new Input("b.ngr", "syntax module B { syntax Other = Shared.Word \"b\"; }"),
            new Input("shared.ngr", "syntax module Shared { token Word = \"ok\"; }"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        using var assembly = CompositionTestAssembly.Create(result.Results.SelectMany(r => r.GeneratedSources)
            .Select(s => new Grammar.GeneratedSource(s.HintName, s.SourceText.ToString())));
        foreach (var (name, modules, sample) in new[] { ("One", new[] { "A", "Shared" }, "ok a"), ("Two", new[] { "B", "Shared" }, "ok b") })
        {
            var factory = assembly.Assembly.GetType("Demo.Syntax." + name + "Composition", true)!;
            var language = ((LanguageBuilder)factory.GetMethod("CreateBuilder")!.Invoke(null, null)!).Build();
            Assert.Equal(modules, language.Modules.Select(m => m.Name));
            using var parsed = language.Parse(sample, (Rule)factory.GetProperty("Start")!.GetValue(null)!);
            Assert.True(parsed.Success);
        }
    }

    [Fact]
    public void Config_change_regenerates_membership_and_start()
    {
        var config = new Input("nitrogen.json", """{"languages":[{"name":"Demo","start":"A.Program","namespace":"Demo.Syntax","grammars":["a.ngr"]}]}""");
        var driver = Driver(config, new Input("a.ngr", "syntax module A { syntax Program = \"a\"; }"),
            new Input("b.ngr", "syntax module B { syntax Other = \"b\"; }"))
            .RunGenerators(CSharpCompilation.Create("Factories"));
        var next = new Input("nitrogen.json", """{"languages":[{"name":"Demo","start":"B.Other","namespace":"Demo.Syntax","grammars":["b.ngr"]}]}""");
        var result = driver.ReplaceAdditionalText(config, next).RunGenerators(CSharpCompilation.Create("Factories")).GetRunResult();
        var factory = Assert.Single(result.Results.SelectMany(r => r.GeneratedSources), s => s.HintName.Contains("Composition"));
        Assert.Contains("BModule.Other", factory.SourceText.ToString());
        Assert.DoesNotContain("AModule", factory.SourceText.ToString());
    }

    [Theory]
    [InlineData("{\"languages\": [")]
    [InlineData("{\"languages\":[{\"name\":\"Demo\",\"start\":\"A.Program\",\"grammars\":[\"missing.ngr\"]}]}")]
    [InlineData("{\"languages\":[{\"name\":\"Bad.Name\",\"start\":\"A.Program\",\"grammars\":[\"a.ngr\"]}]}")]
    [InlineData("{\"languages\":[{\"name\":\"Demo\",\"start\":\"A.Nope\",\"grammars\":[\"a.ngr\"]}]}")]
    public void Invalid_configuration_reports_diagnostics(string text)
    {
        var result = Run(new Input("nitrogen.json", text), new Input("a.ngr", "syntax module A { syntax Program = \"a\"; }"));
        Assert.Contains(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Results.SelectMany(r => r.GeneratedSources), s => s.HintName.Contains("Composition"));
    }

    [Fact]
    public void Helper_type_collision_is_reported_as_a_generation_error()
    {
        var compilation = CSharpCompilation.Create("Collision", new[] { CSharpSyntaxTree.ParseText("namespace Demo.Syntax { public static class DemoComposition {} }") });
        var result = Driver(new Input("a.ngr", "language Demo { start A.Program; namespace Demo.Syntax; } syntax module A { syntax Program = \"a\"; }"))
            .RunGenerators(compilation).GetRunResult();
        Assert.Contains(result.Diagnostics, d => d.Id == "NGR0406" && d.GetMessage().Contains("collides"));
    }

    [Fact]
    public void Declaration_emits_its_own_factory_without_json()
    {
        var result = Run(new Input("a.ngr", "language Demo { start A.Program; namespace Demo.Syntax; } syntax module A { syntax Program = \"a\"; }"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains(result.Results.SelectMany(r => r.GeneratedSources), s => s.HintName.Contains("DemoComposition"));
    }
    [Theory]
    [InlineData("Other")]
    [InlineData("OtherComposition")]
    public void Unrelated_generated_type_cannot_collide_with_a_factory(string moduleName)
    {
        var result = Run(new Input("nitrogen.json", "{\"languages\":[{\"name\":\"Demo\",\"start\":\"A.Program\",\"namespace\":\"Demo.Syntax\",\"grammars\":[\"a.ngr\"]}]}"),
            new Input("a.ngr", "syntax module A { syntax Program = \"a\"; }"),
            new Input("other.ngr", "syntax module " + moduleName + " { extensible syntax Composition { | Demo = \"x\" } }"));
        Assert.Contains(result.Diagnostics, d => d.Id == "NGR0406" && d.GetMessage().Contains("collides"));
    }

    [Fact]
    public void Invalid_composed_grammar_reports_a_grammar_diagnostic_instead_of_crashing_generator()
    {
        var result = Run(new Input("nitrogen.json", "{\"languages\":[{\"name\":\"Demo\",\"start\":\"A.Program\",\"namespace\":\"Demo.Syntax\",\"grammars\":[\"a.ngr\"]}]}"),
            new Input("a.ngr", "syntax module A { syntax Program = \"a\"; extend syntax Missing.Expr { | Extra = \"x\" } }"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8785");
        Assert.Contains(result.Diagnostics, d => d.Id.StartsWith("NGR", StringComparison.Ordinal));
    }
    [Fact]
    public void Duplicate_factory_names_are_diagnosed_without_crashing()
    {
        var result = Run(new Input("nitrogen.json", "{\"languages\":[{\"name\":\"Demo\",\"start\":\"A.Program\",\"grammars\":[\"a.ngr\"]},{\"name\":\"Demo\",\"start\":\"A.Program\",\"grammars\":[\"a.ngr\"]}]}"),
            new Input("a.ngr", "syntax module A { syntax Program = \"a\"; }"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8785");
        Assert.Contains(result.Diagnostics, d => d.Id == "NGR0406" && d.GetMessage().Contains("multiple definitions"));
    }

    [Fact]
    public void Shared_grammar_with_conflicting_language_namespaces_is_diagnosed()
    {
        var result = Run(new Input("nitrogen.json", "{\"languages\":[{\"name\":\"One\",\"start\":\"A.Program\",\"namespace\":\"One.Syntax\",\"grammars\":[\"a.ngr\"]},{\"name\":\"Two\",\"start\":\"A.Program\",\"namespace\":\"Two.Syntax\",\"grammars\":[\"a.ngr\"]}]}"),
            new Input("a.ngr", "syntax module A { syntax Program = \"a\"; }"));
        Assert.Contains(result.Diagnostics, d => d.Id == "NGR0406" && d.GetMessage().Contains("conflicting namespaces"));
    }

    [Fact]
    public void Configured_globs_do_not_read_unregistered_files_from_disk()
    {
        string folder = Directory.CreateTempSubdirectory("composition-registered-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "outside.ngr"), "syntax module Outside { syntax Program = \"x\"; }");
            string json = System.Text.Json.JsonSerializer.Serialize(new { languages = new[] { new { name = "Demo", start = "Outside.Program", grammars = new[] { Path.Combine(folder, "*.ngr") } } } });
            var result = Run(new Input("nitrogen.json", json));
            Assert.Contains(result.Diagnostics, d => d.Id == "NGR0406" && d.GetMessage().Contains("registered"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
