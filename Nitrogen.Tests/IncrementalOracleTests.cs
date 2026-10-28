using System.Text;
using Nitrogen.Tests.Calc;
using Nitrogen.Tests.Divide;
using Nitrogen.Tests.Lexical;
using Nitrogen.Tests.Lists;
using Nitrogen.Tests.Recovery;
using Nitrogen.Tests.Shapes;
using Xunit;
using Xunit.Sdk;

namespace Nitrogen.Tests;

/// <summary>
/// The incremental reparse oracle: random edit sequences over each test grammar, every reparse compared node for
/// node with a full parse of the same text. Seeds 1–200 run by default; set NITROGEN_ORACLE_SEEDS for a longer run.
/// A failure names the grammar, seed and step, and both texts, so it can be replayed.
/// </summary>
public class IncrementalOracleTests
{
    sealed record Case(string Name, Language Language, Rule Start, string Input, string[] Fragments);

    static readonly Language Calc = new LanguageBuilder().Add(CalcModule.Instance).Build();
    static readonly Language Recovery = new LanguageBuilder().Add(RecoveryModule.Instance).Build();
    static readonly Language Shapes = new LanguageBuilder().Add(ShapesModule.Instance).Build();
    static readonly Language Lists = new LanguageBuilder().Add(ListsModule.Instance).Build();
    static readonly Language Lexical = new LanguageBuilder().Add(LexicalModule.Instance).Build();
    static readonly Language Divide = new LanguageBuilder().Add(DivideModule.Instance).Build();

    static string Repeat(int count, Func<int, string> item) => string.Concat(Enumerable.Range(0, count).Select(item));

    static readonly Case[] Cases =
    [
        new("calc", Calc, CalcModule.Program,
            Repeat(30, i => i % 3 == 0 ? $"x{i} = -(a{i} + f(b, {i}.5)) * c;\n" : i % 3 == 1 ? $"g({i}, h(x), -y);\n" : $"v = w{i};\n"),
            ["x", "1", "2.5", "+", "-", "*", "(", ")", ";", "f(", ",", " ", "\n", "=", "ab", "/* c */", "//c\n"]),
        new("recovery", Recovery, RecoveryModule.File,
            Repeat(20, i => $"item n{(char)('a' + i % 26)} {{ x = {i}; y = 1; }}\n"),
            ["item", "a", "x", "=", "1", ";", "{", "}", " ", "\n", "??"]),
        new("shapes", Shapes, ShapesModule.File,
            Repeat(20, i => i % 2 == 0 ? $"public static decl a : int {{ b, c }}\n" : "decl d : e { }\n"),
            ["public", "static", "decl", "a", ":", "int", "{", "}", ",", " ", "\n"]),
        new("shapes-block", Shapes, ShapesModule.Block,
            "{" + Repeat(30, i => i % 3 == 0 ? " ;" : $" var v{(char)('a' + i % 26)}") + " }",
            ["var", "a", ";", " ", "{", "}", "\n"]),
        new("lists", Lists, ListsModule.File,
            Repeat(25, i => $"set n{(char)('a' + i % 26)} ({i}, {i + 1}, 3);\n"),
            ["set", "a", "(", ")", ",", "1", ";", " ", "\n"]),
        new("divide", Divide, DivideModule.Program,
            string.Join("/", Enumerable.Range(1, 40).Select(i => i % 4 == 0 ? $"{i}*{i + 1}\n!" : i % 4 == 1 ? $"{i}!*{i}" : $"{i}\n!")) + ";",
            ["/", "//", "/*", "*/", "!", "*", "1", "\n", " ", "7"]),
        new("lexical", Lexical, LexicalModule.Document,
            Repeat(15, i => $"(* c{i} *), \"s{i}\\\"q\", 0x1F, 12_34, word, pick a!, pick b c, tag w, -5, {{ a = 1 b = 2 }}, ") + "end;",
            ["(*", "*)", "\"", "\\", "0x", "_", "1", "a", "pick", "tag", "x", "!", ",", ";", "{", "}", "=", " ", "-"]),
    ];

    static int Seeds => int.TryParse(Environment.GetEnvironmentVariable("NITROGEN_ORACLE_SEEDS"), out int n) && n > 0 ? n : 200;

    public static TheoryData<string> CaseNames => new(Cases.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Reparse_equals_full_parse_under_random_edits(string name)
    {
        var c = Cases.Single(x => x.Name == name);
        int steps = 0, reusing = 0;
        for (int seed = 1; seed <= Seeds; seed++) Run(c, seed, ref steps, ref reusing);

        // The oracle must exercise reuse, not only reparses of broken text parsed whole.
        Assert.True(reusing * 20 >= steps, $"{name}: only {reusing} of {steps} steps reused anything");
    }

    /// <summary>
    /// Seed 17: a reparse whose text has errors must report the same first failure (what was expected at the
    /// furthest position) as a full parse, including expectations from inside subtrees it would otherwise reuse.
    /// </summary>
    [Fact]
    public void Calc_seed_17_a_failed_reparse_expects_what_a_full_parse_expects()
    {
        int steps = 0, reusing = 0;
        Run(Cases.Single(x => x.Name == "calc"), 17, ref steps, ref reusing);
    }

    static void Run(Case c, int seed, ref int steps, ref int reusing)
    {
        var random = new Random(seed);
        string text = c.Input;
        var previous = c.Language.ParseIncremental(text, c.Start);
        try
        {
            Assert.True(previous.Success, $"{c.Name}: the input must parse: {(previous.Diagnostics.Length > 0 ? previous.FormatMessage(previous.Diagnostics[0]) + " at " + previous.Diagnostics[0].Span : "")}");
            int count = random.Next(1, 6);
            for (int step = 1; step <= count; step++)
            {
                var edits = RandomEdits(random, text, c.Fragments);
                var (next, changes) = IncrementalTests.Edit(text, edits);
                ParseResult reparsed;
                try
                {
                    reparsed = c.Language.Reparse(previous, next, changes);
                }
                catch (Exception e)
                {
                    throw Fail(c, seed, step, text, next, edits, e);
                }
                previous.Dispose();
                previous = reparsed;
                using var full = c.Language.Parse(next, c.Start);
                using var unseeded = c.Language.ParseUnseeded(next, c.Start);
                try
                {
                    TreeAssert.Same(unseeded, full);
                    TreeAssert.Same(full, reparsed);
                }
                catch (Exception e)
                {
                    throw Fail(c, seed, step, text, next, edits, e);
                }
                text = next;
                steps++;
                if (reparsed.Stats.ReusedNodes > 0) reusing++;
            }
        }
        finally
        {
            previous.Dispose();
        }
    }

    static XunitException Fail(Case c, int seed, int step, string before, string after, (int, int, string)[] edits, Exception inner) =>
        new($"{c.Name} seed {seed} step {step}: edits {string.Join(", ", edits.Select(e => $"({e.Item1}, {e.Item2}, \"{e.Item3}\")"))}\n"
            + $"before: {before}\nafter: {after}\n{inner.Message}", inner);

    /// <summary>
    /// 1–5 sorted, non-overlapping edits. About two thirds tend to keep the text valid, so the reparse reuses (whitespace
    /// at a token boundary, an identifier or number swapped for another, a whole line duplicated or deleted); the rest
    /// are random, biased toward token boundaries and the end of the text.
    /// </summary>
    static (int Start, int OldLength, string New)[] RandomEdits(Random random, string text, string[] fragments)
    {
        int count = random.Next(3) == 0 ? random.Next(2, 6) : 1;
        var candidates = new List<(int Start, int OldLength, string New)>();
        for (int i = 0; i < count; i++) candidates.Add(RandomEdit(random, text, fragments));
        candidates.Sort((x, y) => x.Start.CompareTo(y.Start));
        var edits = new List<(int, int, string)>();
        int floor = 0;
        foreach (var e in candidates)
        {
            if (e.Start < floor) continue;
            edits.Add(e);
            floor = e.Start + e.OldLength + 1;
        }
        return edits.ToArray();
    }

    static (int Start, int OldLength, string New) RandomEdit(Random random, string text, string[] fragments)
    {
        switch (random.Next(12))
        {
            case 0 or 1: // whitespace between tokens
                return (Boundary(random, text), 0, random.Next(2) == 0 ? " " : "\n");
            case 2 or 3: // an identifier for another
                if (Run(random, text, char.IsLetter) is { } word) return (word.Start, word.Length, Letters(random));
                break;
            case 4 or 5: // a number for another
                if (Run(random, text, char.IsDigit) is { } number) return (number.Start, number.Length, random.Next(1000).ToString());
                break;
            case 6 or 7: // a whole line duplicated or deleted
            {
                var lines = LineStarts(text);
                if (lines.Count > 1)
                {
                    int i = random.Next(lines.Count - 1);
                    int start = lines[i], end = lines[i + 1];
                    return random.Next(2) == 0 ? (start, 0, text[start..end]) : (start, end - start, "");
                }
                break;
            }
        }
        int p = RandomPosition(random, text);
        int length = Math.Min(random.Next(0, 9) * (random.Next(3) == 0 ? 0 : 1), text.Length - p);
        var replacement = new StringBuilder();
        for (int k = random.Next(0, 4); k > 0; k--) replacement.Append(fragments[random.Next(fragments.Length)]);
        if (length == 0 && replacement.Length == 0) replacement.Append(fragments[random.Next(fragments.Length)]);
        return (p, length, replacement.ToString());
    }

    static string Letters(Random random) =>
        new(Enumerable.Range(0, random.Next(1, 4)).Select(_ => (char)('a' + random.Next(26))).ToArray());

    static int Boundary(Random random, string text)
    {
        for (int tries = 0; tries < 20; tries++)
        {
            int p = random.Next(text.Length + 1);
            bool before = p > 0 && char.IsLetterOrDigit(text[p - 1]);
            bool after = p < text.Length && char.IsLetterOrDigit(text[p]);
            if (before != after) return p;
        }
        return random.Next(text.Length + 1);
    }

    /// <summary>A random maximal run of characters matching <paramref name="part"/>, or null.</summary>
    static (int Start, int Length)? Run(Random random, string text, Func<char, bool> part)
    {
        for (int tries = 0; tries < 20 && text.Length > 0; tries++)
        {
            int p = random.Next(text.Length);
            if (!part(text[p])) continue;
            int start = p, end = p;
            while (start > 0 && part(text[start - 1])) start--;
            while (end < text.Length && part(text[end])) end++;
            return (start, end - start);
        }
        return null;
    }

    static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        if (starts[^1] != text.Length) starts.Add(text.Length);
        return starts;
    }

    static int RandomPosition(Random random, string text)
    {
        switch (random.Next(10))
        {
            case < 5: // a token boundary: where a letter/digit run starts or ends
                return Boundary(random, text);
            case < 8:
                return random.Next(text.Length + 1);
            default: // near the end
                return Math.Max(0, text.Length - random.Next(0, 6));
        }
    }
}
