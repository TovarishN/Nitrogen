using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>A workspace language takes its semantic modules from its helper sources, so its declarative lowering runs.</summary>
public class WorkspaceSemanticsTests
{
    const string Sum = """
        syntax module Sum
        {
          token Number = ['0'..'9']+;
          syntax Doc = Item:Add;
          syntax Add = "add" Left:Num Right:Num ";" lowers Sum.Add(Left, Right);
          syntax Num = Text:Number lowers literal Core.Scalar Text;
        }
        """;

    const string SemanticSource = """
        using Nitrogen.Semantic;

        public static class SumSemantics
        {
            public static readonly OperationSignature Add = new("Sum.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
            public static readonly SemanticModule Module = new("Sum", [], [], [Add]);
        }
        """;

    static GrammarWorkspace Workspace(string source)
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("sum.ngr", Sum);
        workspace.Sources["SumSemantics.cs"] = source;
        return workspace;
    }

    static LoweringResult Lower(WorkspaceSnapshot snapshot, string text)
    {
        using var parsed = snapshot.Parse(text, "Sum.Doc");
        Assert.True(parsed.Success);
        var project = new Project(snapshot.Language!);
        project.Set("a.sum", parsed.Tree);
        return HirLowering.Lower(new ProjectSemantics(project)["a.sum"], snapshot.Language!.SemanticCatalog);
    }

    [Fact]
    public void A_semantic_module_in_the_sources_types_and_lowers_the_language()
    {
        using var snapshot = Workspace(SemanticSource).Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        Assert.True(snapshot.Language!.SemanticCatalog.Operations.ContainsKey("Sum.Add"));

        var lowered = Lower(snapshot, "add 1 2;");
        Assert.Empty(lowered.Diagnostics);
        var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
        Assert.Equal("Sum.Add", root.Signature.Id);
        Assert.Equal([1f, 2f], root.Arguments.Select(argument => Assert.IsType<HirConstant>(argument).Value));
    }

    [Fact]
    public void A_module_descriptor_in_the_sources_supplies_its_semantics_once()
    {
        var workspace = Workspace("""
            using Nitrogen;
            using Nitrogen.Semantic;
            using Sum.Syntax;

            public static class SumDescriptor
            {
                public static readonly OperationSignature Add = new("Sum.Add", SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
                public static readonly SemanticModule Semantics = new("Sum", [], [], [Add]);
                public static ModuleDescriptor Descriptor { get; } = new("Sum", SumModule.Instance, Semantics, ["Doc"], [Add]);
            }
            """);
        workspace.GeneratedNamespace = "Sum.Syntax";
        using var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        Assert.IsType<HirOperation>(Assert.Single(Lower(snapshot, "add 1 2;").Roots));
    }

    [Fact]
    public void Without_semantic_modules_declarative_lowering_stays_dormant()
    {
        using var snapshot = Workspace("public static class Nothing { }").Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        Assert.Empty(Lower(snapshot, "add 1 2;").Roots);
    }

    [Fact]
    public void A_semantic_composition_error_fails_the_compile_with_its_code()
    {
        using var snapshot = Workspace(SemanticSource.Replace("new(\"Sum\", []", "new(\"Sum\", [\"Missing\"]")).Compile();
        Assert.False(snapshot.Succeeded);
        var error = Assert.Single(snapshot.Diagnostics, diagnostic => diagnostic.Code == "NC0001");
        Assert.True(error.IsError);
        Assert.Contains("Missing", error.Message);
    }

    [Fact]
    public void A_declarative_clause_naming_an_unexported_operation_fails_the_compile()
    {
        using var snapshot = Workspace(SemanticSource.Replace("\"Sum.Add\"", "\"Sum.Plus\"")).Compile();
        Assert.False(snapshot.Succeeded);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Code == "NM0008");
    }

    [Fact]
    public void A_throwing_semantic_module_initializer_is_a_diagnostic()
    {
        using var snapshot = Workspace("""
            using Nitrogen.Semantic;

            public static class Broken
            {
                public static readonly SemanticModule Module = Make();
                static SemanticModule Make() => throw new System.InvalidOperationException("no module today");
            }
            """).Compile();
        Assert.False(snapshot.Succeeded);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.Message.Contains("no module today"));
    }
}
