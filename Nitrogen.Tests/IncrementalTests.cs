using Nitrogen.Tests.Calc;
using Nitrogen.Tests.Divide;
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

    /// <summary>Applies edits given in old-text coordinates (sorted, non-overlapping) and returns the new text and the changes.</summary>
    internal static (string Text, TextChange[] Changes) Edit(string old, params (int Start, int OldLength, string New)[] edits)
    {
        var text = new System.Text.StringBuilder();
        int at = 0;
        foreach (var e in edits)
        {
            text.Append(old, at, e.Start - at).Append(e.New);
            at = e.Start + e.OldLength;
        }
        text.Append(old, at, old.Length - at);
        return (text.ToString(), edits.Select(e => new TextChange(e.Start, e.OldLength, e.New.Length)).ToArray());
    }

    static ParseResult Reparse(string grammar, string old, out string text, params (int Start, int OldLength, string New)[] edits)
    {
        var (language, start) = Grammar(grammar);
        using var previous = language.ParseIncremental(old, start);
        (text, var changes) = Edit(old, edits);
        return language.Reparse(previous, text, changes);
    }

    public static TheoryData<string, string, int, int, string> Edits => new()
    {
        { "calc", CalcProgram(20), 0, 0, "q = 1;\n" },                 // insert at the start
        { "calc", CalcProgram(20), 200, 3, "zz" },                      // replace in the middle
        { "calc", CalcProgram(20), CalcProgram(20).Length, 0, "y = 2;" }, // append at the end
        { "calc", CalcProgram(20), 30, 1, "" },                         // delete one character
        { "calc", "f;\n", 1, 1, "(1);" },                               // lookahead into the edit
        { "calc", "a;\nb;\n", 3, 0, "c;\n" },                           // insert at an item's start
        { "calc", "a;\nb;\n", 2, 0, "c;\n" },                           // insert at an item's end
        { "recovery", "item a { x = 1; }\nitem b { y = 2; }", 9, 1, "w" },
        { "recovery", "item a { x = 1; }\nitem b { y = 2; }", 17, 0, " item c { }" },
        { "shapes", "public static decl a : int { b, c }\ndecl d { }", 30, 0, ", e" },
        { "shapes", "public static decl a : int { b, c }\ndecl d { }", 21, 6, "" },
        { "shapes", "decl a { }", 0, 0, "public " },
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public void Reparse_equals_full_parse_after_edit(string grammar, string old, int start, int length, string replacement)
    {
        using var reparsed = Reparse(grammar, old, out string text, (start, length, replacement));
        var (language, rule) = Grammar(grammar);
        using var full = language.Parse(text, rule);

        TreeAssert.Same(full, reparsed);
        Assert.True(reparsed.IsIncremental);
    }

    [Fact]
    public void Untouched_items_are_reused()
    {
        string old = CalcProgram(200);
        int middle = old.IndexOf("x100 = a*(b+100)", StringComparison.Ordinal) + "x100 = a*(b+".Length;

        using var reparsed = Reparse("calc", old, out string text, (middle, 3, "7"));

        using var full = Calc.Parse(text, CalcModule.Program);
        TreeAssert.Same(full, reparsed);
        Assert.True(reparsed.Stats.ReusedNodes >= 0.9 * reparsed.Tree.NodeCount,
            $"{reparsed.Stats.ReusedNodes} of {reparsed.Tree.NodeCount} reused");
    }

    [Fact]
    public void Insert_at_node_start_is_not_swallowed()
    {
        using var reparsed = Reparse("calc", "a;\nb;\n", out string text, (3, 0, "c+"));

        Assert.Equal("a;\nc+b;\n", text);
        using var full = Calc.Parse(text, CalcModule.Program);
        TreeAssert.Same(full, reparsed);
    }

    [Fact]
    public void Lookahead_into_the_edit_prevents_reuse()
    {
        // Expr "f" ends at 1 but examined the ';' at 1; replacing it makes "f" a call.
        using var reparsed = Reparse("calc", "f;\n", out string text, (1, 1, "(1);"));

        using var full = Calc.Parse(text, CalcModule.Program);
        TreeAssert.Same(full, reparsed);
        Assert.Equal(0, reparsed.Stats.ReusedNodes);
    }

    [Fact]
    public void Bad_changes_throw()
    {
        using var previous = Calc.ParseIncremental("a;b;c;", CalcModule.Program);

        Assert.Throws<ArgumentException>(() => Calc.Reparse(previous, "a;b;c;", [new(4, 0, 0), new(2, 0, 0)]));   // unsorted
        Assert.Throws<ArgumentException>(() => Calc.Reparse(previous, "a;b;", [new(0, 2, 0), new(1, 1, 0)]));     // overlapping
        Assert.Throws<ArgumentException>(() => Calc.Reparse(previous, "a;b;c;", [new(7, 0, 0)]));                // out of range
        Assert.Throws<ArgumentException>(() => Calc.Reparse(previous, "a;b;c;x", [new(0, 1, 1)]));               // wrong new length
        Assert.Throws<ArgumentException>(() => Calc.Reparse(previous, "a;b;c;", [new(0, -1, 0)]));              // negative length
    }

    [Fact]
    public void Previous_from_another_language_throws()
    {
        var other = new LanguageBuilder().Add(CalcModule.Instance).Build();
        using var previous = other.ParseIncremental("a;", CalcModule.Program);

        Assert.Throws<ArgumentException>(() => Calc.Reparse(previous, "b;", [new(0, 1, 1)]));
    }

    [Fact]
    public void Plain_previous_throws()
    {
        using var previous = Calc.Parse("a;", CalcModule.Program);

        Assert.Throws<ArgumentException>(() => Calc.Reparse(previous, "b;", [new(0, 1, 1)]));
    }

    [Fact]
    public void Disposed_previous_throws()
    {
        var previous = Calc.ParseIncremental("a;", CalcModule.Program);
        previous.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Calc.Reparse(previous, "b;", [new(0, 1, 1)]));
    }

    [Fact]
    public void Results_are_independent()
    {
        string old = CalcProgram(10);
        var previous = Calc.ParseIncremental(old, CalcModule.Program);
        var (text, changes) = Edit(old, (5, 1, "9"));
        var reparsed = Calc.Reparse(previous, text, changes);
        string expected = SyntaxDumper.Dump(reparsed.Tree);

        previous.Dispose();
        Assert.Equal(expected, SyntaxDumper.Dump(reparsed.Tree));
        var again = Calc.Reparse(reparsed, text + "z;", [new(text.Length, 0, 2)]);
        reparsed.Dispose();
        using var full = Calc.Parse(text + "z;", CalcModule.Program);
        TreeAssert.Same(full, again);
        again.Dispose();
    }

    [Fact]
    public void Reparse_of_a_reparse_reuses_inside_grafts()
    {
        string old = CalcProgram(100);
        using var first = Calc.ParseIncremental(old, CalcModule.Program);
        var (second, c1) = Edit(old, (old.IndexOf("x10 ", StringComparison.Ordinal), 3, "y10"));
        using var middle = Calc.Reparse(first, second, c1);
        var (third, c2) = Edit(second, (second.IndexOf("x90 ", StringComparison.Ordinal), 3, "y90"));

        using var last = Calc.Reparse(middle, third, c2);

        using var full = Calc.Parse(third, CalcModule.Program);
        TreeAssert.Same(full, last);
        Assert.True(last.Stats.ReusedNodes >= 0.9 * last.Tree.NodeCount, $"{last.Stats.ReusedNodes} of {last.Tree.NodeCount}");
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void Typing_through_an_error_still_reuses(int first, int second, int third)
    {
        string good = CalcProgram(50);
        int semicolon = good.IndexOf("f(c, 2);\nx26", StringComparison.Ordinal) + "f(c, 2)".Length;
        var results = new ParseResult[3];
        results[0] = Calc.ParseIncremental(good, CalcModule.Program);
        var (broken, c1) = Edit(good, (semicolon, 1, ""));
        results[1] = Calc.Reparse(results[0], broken, c1);
        var (fixedText, c2) = Edit(broken, (semicolon, 0, ";"));
        results[2] = Calc.Reparse(results[1], fixedText, c2);

        Assert.False(results[1].Success);
        Assert.True(results[1].IsIncremental);
        using (var full = Calc.Parse(broken, CalcModule.Program)) TreeAssert.Same(full, results[1]);
        using (var full = Calc.Parse(fixedText, CalcModule.Program)) TreeAssert.Same(full, results[2]);
        Assert.True(results[2].Stats.ReusedNodes > 0);

        foreach (int i in new[] { first, second, third }) results[i].Dispose();
    }

    [Fact]
    public void A_reparse_after_an_error_still_works_when_the_good_result_is_gone()
    {
        string good = CalcProgram(20);
        var start = Calc.ParseIncremental(good, CalcModule.Program);
        var (broken, c1) = Edit(good, (5, 1, ")"));
        var bad = Calc.Reparse(start, broken, c1);
        start.Dispose();
        var (fixedText, c2) = Edit(broken, (5, 1, good[5].ToString()));

        using var again = Calc.Reparse(bad, fixedText, c2);
        bad.Dispose();

        using var full = Calc.Parse(fixedText, CalcModule.Program);
        TreeAssert.Same(full, again);
        Assert.True(again.Stats.ReusedNodes > 0);
    }

    [Theory]
    [InlineData("abcdef", "abXdef", 2, 1, 1)]
    [InlineData("abc", "abc", 3, 0, 0)]
    [InlineData("aaa", "aaaa", 3, 0, 1)]
    [InlineData("abc", "", 0, 3, 0)]
    [InlineData("", "xy", 0, 0, 2)]
    public void Between_is_the_change_between_common_prefix_and_suffix(string before, string after, int start, int oldLength, int newLength)
    {
        Assert.Equal(new TextChange(start, oldLength, newLength), TextChange.Between(before, after));
    }

    [Fact]
    public void A_rolled_back_graft_carries_nothing_over()
    {
        // A graft whose arena slot backtracking reclaimed and gave to another node must not carry its block's entries.
        using var previous = Calc.ParseIncremental(CalcProgram(5), CalcModule.Program);
        var a = new BuildArena { GraftSource = previous.Tree };
        var root = previous.Reuse![0];
        int graft = a.NewGraft(root.Node, 0, root.Start, root.End, root.Key);
        a.NodeCount = graft; // rolled back
        int other = a.NewNode(SyntaxKinds.Literal, 0, 1, NodeFlags.None, 0, 0);
        Assert.Equal(graft, other);

        var table = ReuseTable.From(a, [0], previous.Reuse);

        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void A_peek_at_a_slash_examines_the_character_after_it()
    {
        // "1" ends before "/3"; its postfix loop peeks past trivia at "/", and the trivia function reads the "3" to
        // see that "/" starts no comment. Turning "/3" into "//" makes the rest of the line a comment, so "!" binds
        // to "1": reusing the old "1" would be wrong.
        var divide = new LanguageBuilder().Add(DivideModule.Instance).Build();
        const string old = "2*1/3\n!;";
        using var previous = divide.ParseIncremental(old, DivideModule.Program);
        var (text, changes) = Edit(old, (4, 1, "/"));

        using var reparsed = divide.Reparse(previous, text, changes);

        using var full = divide.Parse(text, DivideModule.Program);
        Assert.True(full.Success);
        TreeAssert.Same(full, reparsed);
    }

    [Fact]
    public void The_reuse_table_stays_flat_over_a_long_editing_session()
    {
        string text = CalcProgram(100);
        var result = Calc.ParseIncremental(text, CalcModule.Program);
        int initial = result.Reuse!.Count;
        try
        {
            for (int i = 0; i < 50; i++)
            {
                int at = text.IndexOf("x99 = a*(b+", StringComparison.Ordinal) + "x99 = a*(b+".Length;
                var (next, changes) = Edit(text, (at, 0, "1"));
                var reparsed = Calc.Reparse(result, next, changes);
                result.Dispose();
                result = reparsed;
                text = next;
            }
            using var full = Calc.Parse(text, CalcModule.Program);
            TreeAssert.Same(full, result);
            Assert.True(result.Reuse!.Count <= initial + 20, $"{initial} entries grew to {result.Reuse.Count}");
        }
        finally
        {
            result.Dispose();
        }
    }
}
