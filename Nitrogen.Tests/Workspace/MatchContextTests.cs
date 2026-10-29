using Xunit;
using static Nitrogen.Tests.NamedPatternGrammarTests;

namespace Nitrogen.Tests;

public sealed class MatchContextTests
{
    const string Input = "<config><credentials><user>sa</user><group><password>a</password></group><password>b</password></credentials><password>c</password></config>";

    static int[] Starts(string grammarMatch, string input) =>
        Diagnostics(CompileOnce(grammarMatch), input).Select(d => d.Start).ToArray();

    static readonly Dictionary<string, Nitrogen.Workspace.WorkspaceSnapshot> s_snapshots = new();

    static Nitrogen.Workspace.WorkspaceSnapshot CompileOnce(string match)
    {
        lock (s_snapshots)
        {
            if (!s_snapshots.TryGetValue(match, out var snapshot)) s_snapshots[match] = snapshot = Compile(NamedPatternGrammarTests.Grammar(match));
            return snapshot;
        }
    }

    [Fact]
    public void Inside_reports_each_inner_item()
    {
        var starts = Starts("""match <[ <password>..$_</password> ]> inside <[ <credentials>..$_</credentials> ]> => warning XP2001 : "p";""", Input);
        Assert.Equal([Input.IndexOf("<password>a"), Input.IndexOf("<password>b")], starts);
    }

    [Fact]
    public void Parent_requires_immediate_semantic_parent()
    {
        var starts = Starts("""match <[ <password>..$_</password> ]> parent <[ <credentials>..$_</credentials> ]> => warning XP2002 : "p";""", Input);
        Assert.Equal([Input.IndexOf("<password>b")], starts);
    }

    [Fact]
    public void Nearest_ancestor_supplies_captures()
    {
        const string input = "<credentials><x/><y/><credentials><password>p</password></credentials></credentials>";
        var snapshot = CompileOnce("""match <[ <password>..$_</password> ]> inside (c: Node*) <[ <credentials>..$c</credentials> ]> => warning XP2003 : $"{c.Count}";""");
        Assert.Equal("1", Assert.Single(Diagnostics(snapshot, input)).Message);
    }

    [Fact]
    public void Explicit_context_category()
    {
        string match = """match <[ <password>..$_</password> ]> inside Doc <[ <credentials>..$_</credentials> ]> => warning XP2004 : "p";""";
        Assert.Single(Starts(match, "<credentials><password>a</password></credentials>"));
        Assert.Empty(Starts(match, "<config><password>a</password></config>"));
    }

    [Fact]
    public void Broken_ancestor_is_skipped_silently()
    {
        var snapshot = CompileOnce("""match <[ <password>..$_</password> ]> inside <[ <credentials>..$_</credentials> ]> => warning XP2001 : "p";""");
        // This grammar does not recover structure from syntax errors; a template hole is input Validate rejects alike.
        using var parsed = snapshot.Language!.ParseTemplate("<credentials><password>a</password>$X</credentials>", "X.Doc",
            new Dictionary<string, string> { ["X"] = "X.Node" });
        Assert.True(parsed.Success);
        var project = new Nitrogen.Binding.Project(snapshot.Language!);
        project.Set("input.xml", parsed.Tree);
        using var semantics = new Nitrogen.Semantics.ProjectSemantics(project);
        // The password's own check skips the broken credentials ancestor. (The credentials node's own match still
        // rejects its broken subtree, as main patterns always have.)
        int password = "<credentials>".Length;
        Assert.DoesNotContain(semantics["input.xml"].Diagnostics(), d => d.Code == "XP2001" || d.Span.Start == password);
    }

    [Fact]
    public void Inside_on_root_has_no_ancestors()
    {
        Assert.Empty(Starts("""match <[ <credentials>..$_</credentials> ]> inside <[ <x>..$_</x> ]> => warning XP2005 : "p";""", "<credentials></credentials>"));
    }

    [Fact]
    public void Context_capture_collision_reports_NGR0165()
    {
        var diagnostics = Validate(NamedPatternGrammarTests.Grammar("""match (c: Node*) <[ <a>..$c</a> ]> inside (c: Node*) <[ <b>..$c</b> ]> => warning XP2006 : "p";"""));
        Assert.Contains(diagnostics, d => d.IsError && d.Code == "NGR0165");
    }

    [Fact]
    public void Deep_context_walk_stays_cheap()
    {
        // Ancestor validity is computed once per file, so walking every ancestor of every <a> stays linear per check.
        const int depth = 600;
        string input = string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));
        var snapshot = CompileOnce("""match <[ <a>..$_</a> ]> inside <[ <never>..$_</never> ]> => warning XP2007 : "p";""");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(Diagnostics(snapshot, input));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public void Budget_exhaustion_surfaces_as_check_failure()
    {
        // Only the innermost <b> matches; its context searches every ancestor's whole subtree for a Secret that is absent.
        const int depth = 600;
        string input = string.Concat(Enumerable.Repeat("<a>", depth)) + "<b>x</b>" + string.Concat(Enumerable.Repeat("</a>", depth));
        var snapshot = CompileOnce("""match <[ <b>..$_</b> ]> inside (s: ..Secret) <[ <a>..$_ $s ..$_</a> ]> => warning XP2008 : "p";""");
        var diagnostics = Diagnostics(snapshot, input);
        var failure = Assert.Single(diagnostics);
        Assert.Equal(Nitrogen.Semantics.SemanticCodes.Failed, failure.Code);
        Assert.Contains("NQP0006", failure.Message);
    }
}
