using Nitrogen.Binding;
using Nitrogen.Semantics;
using Nitrogen.Tests.Typed;
using Nitrogen.Tests.Typed.Extra;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>Generated semantics on the Typed test language (issue 239).</summary>
public sealed class SemanticsTests : IDisposable
{
    static readonly Language Typed = new LanguageBuilder().Add(TypedModule.Instance).Add(TypedExtraModule.Instance).Build();

    readonly List<ParseResult> _parsed = new();
    readonly Project _project = new(Typed);
    readonly ProjectSemantics _semantics;

    public SemanticsTests() => _semantics = new ProjectSemantics(_project);

    FileSemantics Open(string path, string text)
    {
        var parsed = Typed.Parse(text, TypedModule.File);
        _parsed.Add(parsed);
        _project.Set(path, parsed.Tree);
        return _semantics[path];
    }

    static int NodeAt(FileSemantics file, int kind, int start)
    {
        for (int node = 0; node < file.Tree.NodeCount; node++)
            if (file.Tree.Kind(node) == kind && file.Tree.Span(node).Start == start) return node;
        throw new InvalidOperationException($"no node of kind {kind} at {start}");
    }

    static string TypeOf(FileSemantics file, int kind, int start) => file.Get(NodeAt(file, kind, start), TypedModule.P_Expr_Type);

    static string? ExpectedOf(FileSemantics file, int kind, int start) => file.Get(NodeAt(file, kind, start), TypedModule.P_Expr_Expected);

    [Fact]
    public void Types_flow_up_from_expressions_and_across_declarations()
    {
        const string text = "let a = 1 + 2; let b = a; let c = yes;";
        var file = Open("a.typed", text);
        Assert.Equal("num", TypeOf(file, TypedKinds.Add, text.IndexOf('1')));
        Assert.Equal("num", TypeOf(file, TypedKinds.Ref, text.IndexOf("a;", StringComparison.Ordinal)));
        Assert.Equal("bool", TypeOf(file, TypedKinds.Ref, text.IndexOf("yes", StringComparison.Ordinal)));
        Assert.Empty(file.Diagnostics());
    }

    [Fact]
    public void An_annotation_flows_down_as_the_expected_type_and_a_check_reports_the_mismatch()
    {
        const string text = "let x : bool = 1;";
        var file = Open("a.typed", text);
        Assert.Equal("bool", ExpectedOf(file, TypedKinds.Num, text.IndexOf('1')));
        var diagnostic = Assert.Single(file.Diagnostics());
        Assert.Equal(("TY0001", "'x' is bool, not num", 0), (diagnostic.Code, diagnostic.Message, diagnostic.Span.Start));
    }

    [Fact]
    public void A_list_child_assigns_every_item()
    {
        const string text = "sum 1, pi, 3;";
        var file = Open("a.typed", text);
        Assert.Equal("num", ExpectedOf(file, TypedKinds.Num, 4));
        Assert.Equal("num", ExpectedOf(file, TypedKinds.Ref, 7));
        Assert.Equal("num", ExpectedOf(file, TypedKinds.Num, 11));
    }

    [Fact]
    public void Generated_structs_read_children_properties_and_parents()
    {
        const string text = "let n : num = 2 + 3;";
        var file = Open("a.typed", text);
        var let = new LetNodeSemantics(file, NodeAt(file, TypedKinds.Let, 0));
        Assert.Equal("n", let.Name.Text);
        Assert.Equal("num", let.Annotation?.Spelled);
        Assert.Equal("num", let.Value.Type);
        Assert.Equal(file.Tree.Root, let.Parent?.Node);
    }

    [Fact]
    public void Symbol_properties_cross_files_and_a_change_resets_them()
    {
        Open("a.typed", "let shared = yes;");
        const string text = "let copy = shared;";
        var b = Open("b.typed", text);
        int start = text.IndexOf("shared", StringComparison.Ordinal);
        Assert.Equal("bool", TypeOf(b, TypedKinds.Ref, start));

        Open("a.typed", "let shared = 1;");
        Assert.Equal("num", TypeOf(_semantics["b.typed"], TypedKinds.Ref, start));
    }

    [Fact]
    public void An_alternative_from_another_module_implements_and_assigns_the_shared_properties()
    {
        const string text = "let n = -pi;";
        var file = Open("a.typed", text);
        Assert.Equal("num", TypeOf(file, TypedExtraKinds.Neg, text.IndexOf('-')));
        Assert.Equal("num", ExpectedOf(file, TypedKinds.Ref, text.IndexOf("pi", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_cycle_is_NS0001_and_yields_the_default()
    {
        const string text = "let c = cyc hint;";
        var file = Open("a.typed", text);
        Assert.Equal("error", TypeOf(file, TypedKinds.Cyc, text.IndexOf("cyc", StringComparison.Ordinal)));
        Assert.Contains(file.Diagnostics(), d => d.Code == SemanticCodes.Cycle);
    }

    [Fact]
    public void An_exception_is_NS0002_and_yields_the_default()
    {
        const string text = "let d = boom;";
        var file = Open("a.typed", text);
        Assert.Equal("error", TypeOf(file, TypedKinds.Boom, text.IndexOf("boom", StringComparison.Ordinal)));
        var failure = Assert.Single(file.Diagnostics());
        Assert.Equal(SemanticCodes.Failed, failure.Code);
        Assert.Contains("boom", failure.Message);
    }

    [Fact]
    public void A_missing_expression_takes_the_default_and_reports_nothing()
    {
        const string text = "let e : num = ;";
        var file = Open("a.typed", text);
        var let = new LetNodeSemantics(file, NodeAt(file, TypedKinds.Let, 0));
        Assert.True(let.Value.IsMissing);
        Assert.Equal("error", let.Value.Type);
        Assert.Empty(file.Diagnostics());
    }

    [Fact]
    public void Checks_report_in_text_order()
    {
        const string text = "let f = yes + 1; let g : num = true;";
        var file = Open("a.typed", text);
        Assert.Equal(new[] { "TY0002", "TY0001" }, file.Diagnostics().Select(d => d.Code));
    }

    public void Dispose()
    {
        foreach (var parsed in _parsed) parsed.Dispose();
    }

    [Fact]
    public void A_module_lists_its_properties_with_their_flags_and_reads_them_boxed()
    {
        var type = Assert.Single(TypedModule.Instance.Properties, p => p.Name == "Type");
        Assert.True(type.IsHover);
        Assert.True(Assert.Single(TypedModule.Instance.Properties, p => p.Name == "Expected").IsExpected);
        Assert.Equal("Type", Assert.Single(TypedModule.Instance.SymbolProperties).Name);

        const string text = "let a = 1;";
        var file = Open("a.typed", text);
        int num = NodeAt(file, TypedKinds.Num, text.IndexOf('1'));
        Assert.Equal("num", type.Read(file, num));
        Assert.True(file.Defines(num, type));
        Assert.False(file.Defines(NodeAt(file, TypedKinds.Let, 0), type));
    }

    [Fact]
    public void A_check_with_at_reports_at_that_child()
    {
        var file = Open("a.typed", "let reserved = 1;");
        var diagnostic = Assert.Single(file.Diagnostics(), d => d.Code == "TY0003");
        Assert.Equal(("'reserved' is a reserved name", 4, 8), (diagnostic.Message, diagnostic.Span.Start, diagnostic.Span.Length));
    }
}
