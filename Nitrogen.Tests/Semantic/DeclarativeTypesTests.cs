using Nitrogen.Binding;
using Nitrogen.Semantic;
using Nitrogen.Semantics;
using Nitrogen.Tests.Lowered;
using Xunit;

namespace Nitrogen.Tests.Semantic;

public sealed class DeclarativeTypesTests
{
    internal static void Run(string source, Action<FileSemantics> assert, params SemanticModule[] extra)
    {
        var language = DeclarativeCompositionTests.Build(extra);
        using var parsed = language.Parse(source, LoweredModule.File);
        Assert.True(parsed.Success, source);
        var project = new Project(language);
        project.Set("test.low", parsed.Tree);
        assert(new ProjectSemantics(project)["test.low"]);
    }

    static string At(string source, SemanticDiagnostic diagnostic) =>
        source.Substring(diagnostic.Span.Start, diagnostic.Span.Length);

    static int First(FileSemantics file, int kind) =>
        Enumerable.Range(0, file.Tree.NodeCount).First(node => file.Tree.Kind(node) == kind);

    [Fact]
    public void Operation_and_literals_type_check()
    {
        Run("add(1, 2);", file =>
        {
            Assert.Empty(file.Diagnostics());
            Assert.Equal(SemanticTypes.Scalar, file.DeclarativeTypes.TypeOf(First(file, LoweredKinds.Add)));
            Assert.Equal(SemanticTypes.Scalar, file.DeclarativeTypes.TypeOf(First(file, LoweredKinds.Num)));
        });
    }

    [Fact]
    public void Wrong_argument_type_is_NT0001_at_the_argument()
    {
        const string source = "turn(1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0001", diagnostic.Code);
            Assert.Equal("1", At(source, diagnostic));
            Assert.Contains("Units.Angle", diagnostic.Message);
            Assert.Contains("Core.Scalar", diagnostic.Message);
        });
    }

    [Fact]
    public void References_take_their_symbols_declared_types()
    {
        Run("input x : Scalar; fixed a; add(x, 1); turn(a);", file => Assert.Empty(file.Diagnostics()));
        const string source = "fixed a; add(a, 1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0001", diagnostic.Code);
            Assert.Equal("a", At(source, diagnostic));
        });
    }

    [Fact]
    public void Qualified_and_unique_unqualified_names_resolve() =>
        Run("input x : Core.Scalar; input y : Angle; add(x, 1); turn(y);", file => Assert.Empty(file.Diagnostics()));

    [Fact]
    public void Unknown_or_ambiguous_type_name_is_NT0002()
    {
        const string unknown = "input x : Nope; x;";
        Run(unknown, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0002", diagnostic.Code);
            Assert.Equal("Nope", At(unknown, diagnostic));
        });
        Run("input y : Angle;", file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0002", diagnostic.Code);
            Assert.Contains("qualify", diagnostic.Message);
        }, new SemanticModule("Other", [], [SemanticType.Named("Other", "Angle")], []));
    }

    [Fact]
    public void Nonfinite_literal_is_NT0003_without_a_cascade()
    {
        const string source = "add(1e999, 1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0003", diagnostic.Code);
            Assert.Equal("1e999", At(source, diagnostic));
        });
    }

    [Fact]
    public void Untyped_argument_is_NT0004()
    {
        const string source = "plain p; add(p, 1);";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0004", diagnostic.Code);
            Assert.Equal("p", At(source, diagnostic));
        });
    }

    [Fact]
    public void Pass_through_takes_its_single_typed_child()
    {
        Run("add(wrap(1), 2);", file => Assert.Empty(file.Diagnostics()));
        const string source = "turn(wrap(1));";
        Run(source, file =>
        {
            var diagnostic = Assert.Single(file.Diagnostics());
            Assert.Equal("NT0001", diagnostic.Code);
            Assert.Equal("wrap(1)", At(source, diagnostic));
        });
    }

    [Fact]
    public void Nodes_outside_lowers_arguments_are_not_checked() =>
        Run("plain p; p; wrap(p);", file => Assert.Empty(file.Diagnostics()));
}
