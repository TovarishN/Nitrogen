using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class WorkspaceCompositionTests
{
    [Fact]
    public void Helper_uses_the_generated_factory_and_start_in_live_compilation()
    {
        var workspace = new GrammarWorkspace { GeneratedNamespace = "Demo.Syntax", CompositionName = "Demo", StartRule = "A.Program" };
        workspace.SetGrammar("a.ngr", "syntax module A { syntax Program = B.Word; } syntax module B { token Word = \"ok\"; }");
        workspace.Sources["Helper.cs"] = """
            namespace Demo.Syntax;
            public static class Helper
            {
                public static readonly Nitrogen.Semantic.SemanticModule Semantics = Verify();
                static Nitrogen.Semantic.SemanticModule Verify()
                {
                    var language = DemoComposition.CreateBuilder().Build();
                    if (language.Modules.Count != 2) throw new System.Exception("missing syntax module");
                    using var parsed = language.Parse("ok", DemoComposition.Start);
                    if (!parsed.Success) throw new System.Exception("wrong factory start");
                    return new("Helper", [], [], []);
                }
            }
            """;
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        Assert.Equal(new[] { "A", "B" }, snapshot.Language!.Modules.Select(m => m.Name));
    }

    [Fact]
    public void Validation_emits_factory_without_running_helpers()
    {
        var workspace = new GrammarWorkspace { GeneratedNamespace = "Demo.Syntax", CompositionName = "Demo", StartRule = "A.Program" };
        workspace.SetGrammar("a.ngr", "syntax module A { syntax Program = \"ok\"; }");
        workspace.Sources["Helper.cs"] = """
            namespace Demo.Syntax;
            public static class Helper
            {
                public static readonly Nitrogen.Language Language = Init();
                static Nitrogen.Language Init() => throw new System.Exception("must not execute");
                public static Nitrogen.LanguageBuilder Builder() => DemoComposition.CreateBuilder();
            }
            """;
        Assert.DoesNotContain(workspace.ValidateCompilation(), d => d.IsError);
        Assert.Null(workspace.Current);
    }

    [Fact]
    public void Helper_factory_type_collision_is_reported_before_loading()
    {
        var workspace = new GrammarWorkspace { GeneratedNamespace = "Demo.Syntax", CompositionName = "Demo", StartRule = "A.Program" };
        workspace.SetGrammar("a.ngr", "syntax module A { syntax Program = \"ok\"; }");
        workspace.Sources["Helper.cs"] = "namespace Demo.Syntax; public static class DemoComposition {}";
        Assert.Contains(workspace.ValidateCompilation(), d => d.IsError && d.Message.Contains("DemoComposition"));
    }
    sealed class Resolver(LanguageDefinition definition) : ILanguageDefinitionResolver
    {
        public LanguageDefinition Definition { get; set; } = definition;
        public DefinitionResult Resolve(Grammar.LanguageReference reference, string originPath, InputSnapshot inputs) => new(Definition, []);
    }

    [Fact]
    public async Task Metadata_only_start_and_name_edits_invalidate_the_live_factory()
    {
        string root = Path.Combine(Path.GetTempPath(), "composition-cache");
        string path = Path.Combine(root, "a.ngr");
        var grammar = new DocumentInput(path, "syntax module A { syntax First = \"x\"; syntax Second = \"y\"; }", 1);
        var helper = new DocumentInput(Path.Combine(root, "Helper.cs"),
            "namespace Demo.Syntax; public static class Helper { public static Nitrogen.Rule Start => DemoComposition.Start; }", 1);
        var definition = new LanguageDefinition(path, "Demo", "A.First", "Demo.Syntax", [grammar], [helper], [], [], []);
        var resolver = new Resolver(definition);
        using var session = new DocumentAnalysisSession(resolver);
        var inputs = new InputSnapshot(root, new[] { grammar, helper }.ToDictionary(f => f.Path), true);
        var document = new DocumentInput(Path.Combine(root, "program.txt"), "#!nitrogen language \"./a.ngr\"\nx", 1);
        Assert.Empty((await session.AnalyzeAsync(document, inputs, CancellationToken.None)).Diagnostics);
        var before = session.SnapshotFor(path)!;
        resolver.Definition = definition with { Start = "A.Second" };
        Assert.Empty((await session.AnalyzeAsync(document with { Text = document.Text.Replace("\nx", "\ny"), Version = 2 }, inputs, CancellationToken.None)).Diagnostics);
        var after = session.SnapshotFor(path)!;
        Assert.NotSame(before, after);
        var factory = after.Language!.Modules[0].GetType().Assembly.GetType("Demo.Syntax.DemoComposition", true)!;
        using var parsed = after.Language.Parse("y", (Rule)factory.GetProperty("Start")!.GetValue(null)!);
        Assert.True(parsed.Success);
        resolver.Definition = resolver.Definition with { Name = "Renamed" };
        var invalid = await session.AnalyzeAsync(document with { Version = 3 }, inputs, CancellationToken.None);
        Assert.Contains(invalid.Diagnostics, d => d.Code.StartsWith("CS", StringComparison.Ordinal) && d.Message.Contains("DemoComposition"));
    }
}
