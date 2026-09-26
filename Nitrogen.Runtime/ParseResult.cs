using System.Buffers;

namespace Nitrogen;

/// <param name="ArenaNodes">Nodes created during the parse, including discarded ones.</param>
/// <param name="TreeNodes">Nodes in the final tree.</param>
public readonly record struct ParseStats(int ArenaNodes, int TreeNodes, int MemoHits, int MemoMisses);

/// <summary>Where the fast pass failed and what it expected there: diagnostic 0 of a failed parse.</summary>
internal readonly record struct FirstFailure(int Position, string[]? Expected, bool[]? IsLiteral, int Count)
{
    /// <summary>No fast pass ran (<see cref="Language.ParseRecovering"/>).</summary>
    public static FirstFailure None => new(-1, null, null, 0);
}

/// <summary>Owns the tree and the diagnostics; dispose it to return their arrays to the pool.</summary>
public sealed class ParseResult : IDisposable
{
    Diagnostic[]? _diagnostics;
    int _diagnosticCount;
    string[]? _expected;
    bool[]? _expectedIsLiteral;
    int _expectedCount;
    string[]? _repairTexts;
    int _repairTextCount;

    ParseResult(SyntaxTree tree, bool success, ParseStats stats, Diagnostic[]? diagnostics, int diagnosticCount,
        FirstFailure failure, string[]? repairTexts, int repairTextCount)
    {
        Tree = tree;
        Success = success;
        Stats = stats;
        _diagnostics = diagnostics;
        _diagnosticCount = diagnosticCount;
        _expected = failure.Expected;
        _expectedIsLiteral = failure.IsLiteral;
        _expectedCount = failure.Count;
        _repairTexts = repairTexts;
        _repairTextCount = repairTextCount;
    }

    public SyntaxTree Tree { get; }

    /// <summary>True when the input parsed without repairs; the tree may still contain ambiguities.</summary>
    public bool Success { get; }

    public ParseStats Stats { get; }

    public ReadOnlySpan<Diagnostic> Diagnostics => _diagnostics.AsSpan(0, _diagnosticCount);

    public bool HasErrors
    {
        get
        {
            foreach (ref readonly var d in Diagnostics)
                if (d.Severity == DiagnosticSeverity.Error) return true;
            return false;
        }
    }

    internal static ParseResult Succeeded(SyntaxTree tree, BuildArena arena)
    {
        int ambiguous = 0;
        for (int n = 0; n < tree.NodeCount; n++)
            if ((tree.Flags(n) & NodeFlags.Ambiguous) != 0) ambiguous++;

        Diagnostic[]? diagnostics = null;
        if (ambiguous > 0)
        {
            diagnostics = ArrayPool<Diagnostic>.Shared.Rent(ambiguous);
            int i = 0;
            for (int n = 0; n < tree.NodeCount; n++)
                if ((tree.Flags(n) & NodeFlags.Ambiguous) != 0)
                    diagnostics[i++] = new Diagnostic(DiagnosticCode.Ambiguous, DiagnosticSeverity.Error, tree.Span(n), n);
        }
        return new ParseResult(tree, true, StatsOf(arena, tree), diagnostics, ambiguous, FirstFailure.None, null, 0);
    }

    internal static FirstFailure CaptureFailure(BuildArena arena)
    {
        int count = arena.ExpectedCount;
        if (count == 0) return new FirstFailure(arena.FurthestPosition, null, null, 0);
        var expected = ArrayPool<string>.Shared.Rent(count);
        var isLiteral = ArrayPool<bool>.Shared.Rent(count);
        Array.Copy(arena.ExpectedNames, expected, count);
        Array.Copy(arena.ExpectedIsLiteral, isLiteral, count);
        return new FirstFailure(arena.FurthestPosition, expected, isLiteral, count);
    }

    /// <summary>Even the recovery pass could not start: a single Error root and the fast pass's diagnostic.</summary>
    internal static ParseResult Failed(SyntaxTree errorTree, BuildArena arena, FirstFailure failure)
    {
        var diagnostics = ArrayPool<Diagnostic>.Shared.Rent(1);
        diagnostics[0] = FirstDiagnostic(failure);
        return new ParseResult(errorTree, false, StatsOf(arena, errorTree), diagnostics, 1, failure, null, 0);
    }

    /// <summary>
    /// The recovery pass produced a tree (issue 235). Diagnostic 0 is the fast pass's; the repairs
    /// follow in text order. A repair with no token consumed since the previous one extends it
    /// (never diagnostic 0) instead of adding a diagnostic, and a repair at the fast pass's failure
    /// position is diagnostic 0 already.
    /// </summary>
    internal static ParseResult Recovered(SyntaxTree tree, BuildArena arena, FirstFailure failure)
    {
        int entries = arena.RepairEntryCount;
        var diagnostics = ArrayPool<Diagnostic>.Shared.Rent(entries + 1);
        int count = 0;
        bool hasFirst = failure.Position >= 0;
        if (hasFirst) diagnostics[count++] = FirstDiagnostic(failure);
        var leaves = LeafStarts(tree, out int leafCount);
        int previous = -1, previousEnd = 0;
        for (int i = 0; i < entries; i++)
        {
            var span = arena.RepairSpans[i];
            if (previous >= 0 && !TokenBetween(leaves, leafCount, previousEnd, span.Start))
            {
                if (!(hasFirst && previous == 0) && span.End > previousEnd)
                {
                    var extended = diagnostics[previous];
                    diagnostics[previous] = extended with { Span = new TextSpan(extended.Span.Start, span.End - extended.Span.Start) };
                }
                previousEnd = Math.Max(previousEnd, span.End);
                continue;
            }
            previousEnd = span.End;
            if (hasFirst && span.Start == failure.Position)
            {
                previous = 0;
                continue;
            }
            diagnostics[count] = new Diagnostic(arena.RepairCodes[i], DiagnosticSeverity.Error, span, arena.RepairTextIndex[i]);
            previous = count++;
        }
        ArrayPool<int>.Shared.Return(leaves);

        string[]? texts = null;
        int textCount = arena.RepairTextCount;
        if (textCount > 0)
        {
            texts = ArrayPool<string>.Shared.Rent(textCount);
            Array.Copy(arena.RepairTexts, texts, textCount);
        }
        return new ParseResult(tree, false, StatsOf(arena, tree), diagnostics, count, failure, texts, textCount);
    }

    public string FormatMessage(in Diagnostic diagnostic) => diagnostic.Code switch
    {
        DiagnosticCode.Expected => "expected " + JoinOr(ExpectedDescriptions()),
        DiagnosticCode.Unexpected => "unexpected input",
        DiagnosticCode.Ambiguous => "ambiguous parse: " + JoinOr(AlternativeNames(diagnostic.Arg0)),
        DiagnosticCode.Missing => "expected " + RepairText(diagnostic.Arg0),
        DiagnosticCode.Skipped => "unexpected input; expected " + RepairText(diagnostic.Arg0),
        DiagnosticCode.ExpectedEndOfInput => "unexpected input; expected end of input",
        _ => diagnostic.Code.ToString(),
    };

    public void Dispose()
    {
        Tree.Dispose();
        if (_diagnostics is not null) ArrayPool<Diagnostic>.Shared.Return(_diagnostics);
        if (_expected is not null) ArrayPool<string>.Shared.Return(_expected, clearArray: true);
        if (_expectedIsLiteral is not null) ArrayPool<bool>.Shared.Return(_expectedIsLiteral);
        if (_repairTexts is not null) ArrayPool<string>.Shared.Return(_repairTexts, clearArray: true);
        _diagnostics = null;
        _expected = null;
        _expectedIsLiteral = null;
        _repairTexts = null;
        _diagnosticCount = 0;
        _expectedCount = 0;
        _repairTextCount = 0;
    }

    static Diagnostic FirstDiagnostic(FirstFailure failure) =>
        failure.Count > 0
            ? new Diagnostic(DiagnosticCode.Expected, DiagnosticSeverity.Error, new TextSpan(failure.Position, 0))
            : new Diagnostic(DiagnosticCode.Unexpected, DiagnosticSeverity.Error, new TextSpan(0, 0));

    string RepairText(int index) =>
        _repairTexts is not null && (uint)index < (uint)_repairTextCount ? _repairTexts[index] : "?";

    /// <summary>The starts of the tree's non-empty leaves (its real tokens), sorted.</summary>
    static int[] LeafStarts(SyntaxTree tree, out int count)
    {
        var leaves = ArrayPool<int>.Shared.Rent(Math.Max(tree.NodeCount, 1));
        count = 0;
        for (int n = 0; n < tree.NodeCount; n++)
        {
            if (tree.ChildCount(n) != 0) continue;
            var span = tree.Span(n);
            if (span.Length > 0) leaves[count++] = span.Start;
        }
        Array.Sort(leaves, 0, count);
        return leaves;
    }

    static bool TokenBetween(int[] leaves, int count, int from, int to)
    {
        int low = 0, high = count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (leaves[middle] < from) low = middle + 1;
            else high = middle;
        }
        return low < count && leaves[low] < to;
    }

    internal List<(string Text, bool IsLiteral)> ExpectedItems()
    {
        var items = new List<(string, bool)>(_expectedCount);
        for (int i = 0; i < _expectedCount; i++) items.Add((_expected![i], _expectedIsLiteral![i]));
        return items;
    }

    List<string> ExpectedDescriptions()
    {
        var items = new List<string>(_expectedCount);
        for (int i = 0; i < _expectedCount; i++)
            items.Add(_expectedIsLiteral![i] ? $"'{_expected![i]}'" : _expected![i]);
        return items;
    }

    List<string> AlternativeNames(int node)
    {
        var items = new List<string>();
        for (int k = 0; k < Tree.ChildCount(node); k++)
        {
            int kind = Tree.Kind(Tree.Child(node, k));
            string name = SyntaxDumper.KindName(kind, Tree.Language);
            string? owner = Tree.Language?.ModuleById(SyntaxKinds.ModuleOf(kind))?.Name;
            items.Add(owner is null || name.StartsWith(owner + ".", StringComparison.Ordinal) ? name : owner + "." + name);
        }
        return items;
    }

    static string JoinOr(List<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1],
    };

    static ParseStats StatsOf(BuildArena arena, SyntaxTree tree) =>
        new(arena.NodeCount, tree.NodeCount, arena.MemoHits, arena.MemoMisses);
}
