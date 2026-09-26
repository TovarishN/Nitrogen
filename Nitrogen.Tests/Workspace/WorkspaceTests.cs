using System.Runtime.CompilerServices;
using Nitrogen.Grammar;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The grammar authoring loop (issue 236): compile, reload, report, unload.</summary>
public class WorkspaceTests
{
    internal const string Greet = """
        syntax module Greet
        {
          token Word = ['a'..'z']+;
          syntax Hello = "hello" Name:Word;
        }
        """;

    static WorkspaceSnapshot Compile(GrammarWorkspace workspace)
    {
        var snapshot = workspace.Compile();
        Assert.True(snapshot.Succeeded, string.Join("\n", snapshot.Diagnostics));
        return snapshot;
    }

    [Fact]
    public void A_compiled_grammar_parses_by_rule_name()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("greet.ngr", Greet);
        using var snapshot = Compile(workspace);
        Assert.Same(snapshot, workspace.Current);
        using var result = snapshot.Parse("hello bob", "Greet.Hello");
        Assert.True(result.Success);
        Assert.Equal("(Hello \"hello\" Word:\"bob\")", SyntaxDumper.Dump(result.Tree));
        Assert.Null(snapshot.FindRule("Greet.Nope"));
        Assert.Null(snapshot.FindRule("Nope.Hello"));
    }

    [Fact]
    public void An_edit_shows_in_the_next_snapshot_and_the_old_one_keeps_its_grammar()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("greet.ngr", Greet);
        using var before = Compile(workspace);
        workspace.SetGrammar("greet.ngr", Greet.Replace("Name:Word;", "Name:Word \"!\";"));
        using var after = Compile(workspace);
        Assert.Equal(before.Version + 1, after.Version);
        using (var old = before.Parse("hello bob", "Greet.Hello")) Assert.True(old.Success);
        using (var fresh = after.Parse("hello bob !", "Greet.Hello")) Assert.True(fresh.Success);
        using (var stale = after.Parse("hello bob", "Greet.Hello")) Assert.False(stale.Success);
    }

    [Fact]
    public void A_broken_grammar_reports_where_and_keeps_the_last_good_snapshot()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("greet.ngr", Greet);
        using var good = Compile(workspace);

        workspace.SetGrammar("greet.ngr", Greet.Replace("Name:Word;", "Name:;"));
        using var broken = workspace.Compile();
        Assert.False(broken.Succeeded);
        Assert.Same(good, workspace.Current);
        var error = broken.Diagnostics.First(d => d.IsError);
        Assert.Equal("greet.ngr", error.Path);
        Assert.Equal(4, error.Line);
        Assert.Equal(GrammarCodes.Syntax, error.Code);
        Assert.Throws<InvalidOperationException>(() => broken.Parse("hello bob", "Greet.Hello"));

        workspace.SetGrammar("greet.ngr", Greet);
        using var repaired = Compile(workspace);
        Assert.Same(repaired, workspace.Current);
    }

    [Fact]
    public void Modules_that_use_each_other_compile_together()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("base.ngr", TestGrammarFileTests.ReadGrammar("Base.ngr"));
        workspace.SetGrammar("uses.ngr", TestGrammarFileTests.ReadGrammar("Uses.ngr"));
        using var snapshot = Compile(workspace);
        using var result = snapshot.Parse(CrossModuleTests.Sample, "Uses.Doc");
        Assert.Equal(CrossModuleTests.Expected, SyntaxDumper.Dump(result.Tree));
    }

    [Fact]
    public void Twenty_compiles_leave_no_earlier_load_context_alive()
    {
        var contexts = CompileAndDiscard(new GrammarWorkspace(), 20);
        for (int i = 0; i < 20 && contexts.Any(c => c.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.All(contexts, context => Assert.False(context.IsAlive));
    }

    /// <summary>Kept out of line so no local of the caller's frame roots a snapshot or a tree.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<WeakReference> CompileAndDiscard(GrammarWorkspace workspace, int count)
    {
        var contexts = new List<WeakReference>();
        for (int i = 0; i < count; i++)
        {
            workspace.SetGrammar("greet.ngr", Greet + $"\n// edit {i}\n");
            var snapshot = workspace.Compile();
            using (var result = snapshot.Parse("hello bob", "Greet.Hello")) Assert.True(result.Success);
            contexts.Add(snapshot.LoadContextReference());
            snapshot.Dispose();
        }
        return contexts;
    }

    const string Sized = "syntax module Sized\n{\n  token Id = ['a'..'z']+;\n  syntax R = \"r\" Name:Id\n  {\n    out Size : int = 0;\n    Size = Name.Text.Length;\n  }\n}\n";

    [Fact]
    public void A_csharp_error_in_a_semantics_block_points_into_the_grammar()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("sized.ngr", Sized.Replace("Name.Text.Length", "Undefined + 1"));
        using var snapshot = workspace.Compile();
        Assert.False(snapshot.Succeeded);
        var error = Assert.Single(snapshot.Diagnostics, d => d.Code == "CS0103");
        Assert.Equal(("sized.ngr", 7, 12), (error.Path, error.Line, error.Column));
    }

    [Fact]
    public void A_workspace_grammar_with_semantics_compiles_and_evaluates()
    {
        var workspace = new GrammarWorkspace();
        workspace.SetGrammar("sized.ngr", Sized);
        using var snapshot = Compile(workspace);
        using var parsed = snapshot.Parse("r abc", "Sized.R");
        var project = new Nitrogen.Binding.Project(snapshot.Language!);
        project.Set("x", parsed.Tree);
        var file = new Nitrogen.Semantics.ProjectSemantics(project)["x"];
        var module = snapshot.Language!.Modules.Single();
        var size = (Nitrogen.Semantics.Property<int>)module.GetType().GetField("P_R_Size")!.GetValue(null)!;
        Assert.Equal(3, file.Get(file.Tree.Root, size));
    }
}
