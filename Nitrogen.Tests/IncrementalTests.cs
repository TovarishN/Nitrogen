using Nitrogen.Tests.Calc;
using Nitrogen.Tests.Recovery;
using Nitrogen.Tests.Shapes;
using Xunit;

namespace Nitrogen.Tests;

public class IncrementalTests
{
    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();
    static readonly Language Recovery = new LanguageBuilder().Add(RecoveryModule.Instance).Build();
    static readonly Language Shapes = new LanguageBuilder().Add(ShapesModule.Instance).Build();

    internal static string CalcProgram(int statements) =>
        string.Concat(Enumerable.Range(0, statements).Select(i => $"x{i} = a*(b+{i}) - f(c, 2);\n"));

    public static TheoryData<string, string> Inputs => new()
    {
        { "calc", CalcProgram(30) },
        { "calc", "f;" },
        { "recovery", "item a { x = 1; }\nitem b { y = 2; z = 3; }" },
        { "recovery", "item a { x = 1 }" },
        { "shapes", "public static decl a : int { b, c }\ndecl d { }" },
        { "shapes", "" },
    };

    internal static (Language Language, Rule Start) Grammar(string name) => name switch
    {
        "calc" => (Calc, CalcModule.Program),
        "recovery" => (Recovery, RecoveryModule.File),
        "shapes" => (Shapes, ShapesModule.File),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Incremental_parse_equals_plain_parse(string grammar, string text)
    {
        var (language, start) = Grammar(grammar);
        using var plain = language.Parse(text, start);
        using var incremental = language.ParseIncremental(text, start);

        Assert.True(incremental.IsIncremental);
        TreeAssert.Same(plain, incremental);
    }

    [Fact]
    public void Plain_parse_keeps_no_reuse_data()
    {
        using var result = Calc.Parse(CalcProgram(5), CalcModule.Program);

        Assert.False(result.IsIncremental);
        Assert.Equal(0, result.Stats.ReusedNodes);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Reuse_table_entries_point_at_nodes_of_their_rule(string grammar, string text)
    {
        var (language, start) = Grammar(grammar);
        using var result = language.ParseIncremental(text, start);
        var tree = result.Tree;
        var table = result.Reuse!;

        if (result.Success) Assert.True(table.Count > 0);
        for (int i = 0; i < table.Count; i++)
        {
            var e = table[i];
            Assert.InRange(e.Node, 0, tree.NodeCount - 1);
            if (!e.IsExtensible) Assert.Equal((int)e.Key, tree.Kind(e.Node));
            var span = tree.Span(e.Node);
            Assert.True(e.Start <= span.Start && span.End <= e.End && e.End <= e.ExaminedEnd, $"{e} vs {span}");
            if (i > 0) Assert.True(table[i - 1].Start <= e.Start, "entries are sorted by start");
        }
    }
}
