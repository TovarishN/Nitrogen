namespace Nitrogen;

/// <summary>A rollback point captured by <see cref="ParserState.Mark"/>.</summary>
public readonly record struct ParseMark(
    int Position, int NodeCount, int ChildLogCount, int ChildStackCount, int FrameCount, int MemoWrites);

/// <summary>
/// The state a generated rule works on. Generated rules have the shape
/// <c>static bool ParseX(ref ParserState s)</c>. A plain rule marks, opens its node, matches its
/// elements (calling <see cref="SkipTrivia"/> between elements of a <c>syntax</c> rule), and
/// either closes and returns true, or resets to its mark and returns false.
/// </summary>
public unsafe ref struct ParserState
{
    readonly ReadOnlySpan<char> _text;
    readonly delegate*<ReadOnlySpan<char>, int, int> _trivia;
    readonly AsciiSet _triviaStart;
    internal readonly BuildArena Arena;
    internal readonly MemoTable Memo;

    /// <summary>The language being parsed; needed by extensible rules. Null only in unit tests of the primitives.</summary>
    internal Language? Language { get; init; }

    /// <summary>Current input position. Generated code may read it; it should move it only through the Match/Push methods.</summary>
    public int Position;

    internal ParserState(ReadOnlySpan<char> text, BuildArena arena, MemoTable memo,
        delegate*<ReadOnlySpan<char>, int, int> trivia, AsciiSet triviaStart = default)
    {
        _text = text;
        _trivia = trivia;
        _triviaStart = triviaStart;
        Arena = arena;
        Memo = memo;
        Position = 0;
    }

    public readonly ReadOnlySpan<char> Text => _text;

    public readonly bool AtEnd => Position >= _text.Length;

    public readonly char Current => Position < _text.Length ? _text[Position] : '\0';

    public readonly ParseMark Mark() => new(
        Position, Arena.NodeCount, Arena.ChildLogCount, Arena.ChildStackCount, Arena.FrameCount, Arena.MemoWrites);

    public void Reset(in ParseMark mark)
    {
        Position = mark.Position;
        Arena.ChildStackCount = mark.ChildStackCount;
        Arena.FrameCount = mark.FrameCount;
        if (Arena.MemoWrites == mark.MemoWrites)
        {
            Arena.NodeCount = mark.NodeCount;
            Arena.ChildLogCount = mark.ChildLogCount;
        }
    }

    /// <summary>Opens a node of <paramref name="kind"/>; everything pushed until <see cref="Close"/> becomes its children.</summary>
    public readonly void Open(int kind) => Arena.PushFrame(kind, Position, Arena.ChildStackCount);

    /// <summary>Closes the innermost open node, pushes it as a child of the enclosing one, and returns its arena index.</summary>
    public readonly int Close(NodeFlags flags = NodeFlags.None)
    {
        var a = Arena;
        int frame = --a.FrameCount;
        int childBase = a.FrameChildBase[frame];
        int count = a.ChildStackCount - childBase;
        int start, end;
        if (count > 0)
        {
            // Zero-length children (absent optionals, empty lists) sit after skipped trivia; they
            // must not stretch the node over whitespace, so the span runs between children with width.
            int first = childBase, last = a.ChildStackCount - 1;
            while (first < last && a.Length[a.ChildStack[first]] == 0) first++;
            while (last > first && a.Length[a.ChildStack[last]] == 0) last--;
            int firstNode = a.ChildStack[first], lastNode = a.ChildStack[last];
            start = a.Start[firstNode];
            end = a.Start[lastNode] + a.Length[lastNode];
        }
        else
        {
            start = end = a.FramePosition[frame];
        }
        int logStart = a.AppendChildLog(childBase, count);
        a.ChildStackCount = childBase;
        int node = a.NewNode(a.FrameKind[frame], start, end - start, flags, logStart, count);
        a.PushChild(node);
        return node;
    }

    /// <summary>Pushes a leaf <c>[Position, end)</c> and moves to <paramref name="end"/>.</summary>
    public void PushLeaf(int kind, int end)
    {
        Arena.PushChild(Arena.NewNode(kind, Position, end - Position, NodeFlags.None, 0, 0));
        Position = end;
    }

    /// <summary>Pushes an <see cref="SyntaxKinds.Empty"/> leaf for an absent optional element.</summary>
    public readonly void PushEmpty() =>
        Arena.PushChild(Arena.NewNode(SyntaxKinds.Empty, Position, 0, NodeFlags.None, 0, 0));

    public bool MatchLiteral(string literal)
    {
        if (_text.Slice(Position).StartsWith(literal))
        {
            PushLeaf(SyntaxKinds.Literal, Position + literal.Length);
            return true;
        }
        Arena.Expect(Position, literal, isLiteral: true);
        return false;
    }

    /// <summary>Like <see cref="MatchLiteral"/>, but fails when an identifier character follows.</summary>
    public bool MatchKeyword(string keyword)
    {
        int end = Position + keyword.Length;
        if (_text.Slice(Position).StartsWith(keyword) && (end >= _text.Length || !IsIdentifierPart(_text[end])))
        {
            PushLeaf(SyntaxKinds.Literal, end);
            return true;
        }
        Arena.Expect(Position, keyword, isLiteral: true);
        return false;
    }

    /// <summary>
    /// Pushes a token leaf if a generated matcher found one. Matchers return the end position or a
    /// negative value; a zero-length match counts as failure.
    /// </summary>
    public bool MatchToken(int kind, int end, string name)
    {
        if (end > Position)
        {
            PushLeaf(kind, end);
            return true;
        }
        Arena.Expect(Position, name, isLiteral: false);
        return false;
    }

    /// <summary>
    /// Records <paramref name="literal"/> as expected at the current position, exactly as a failed
    /// <see cref="MatchLiteral"/> or <see cref="MatchKeyword"/> would, and returns false.
    /// </summary>
    public readonly bool ExpectLiteral(string literal)
    {
        Arena.Expect(Position, literal, isLiteral: true);
        return false;
    }

    public void SkipTrivia()
    {
        if (Position < _text.Length && _triviaStart.Matches(_text[Position])) Position = _trivia(_text, Position);
    }

    /// <summary>Records that <paramref name="name"/> was expected at the current position.</summary>
    public readonly void Expect(string name) => Arena.Expect(Position, name, isLiteral: false);

    // ---- the recovery pass (issue 235) ----

    /// <summary>What <see cref="Recover"/> returns when every remaining element is missing.</summary>
    public const int ResumeAtEnd = int.MaxValue;

    /// <summary>The recovery pass: a committed element's failure repairs instead of failing.</summary>
    public readonly bool Recovering => Arena.Recovering;

    /// <summary>
    /// The extension point being parsed and its call's recovery bits (Plan 2b): 1 = the point failing
    /// here fails the parse, 2 = so does stopping here before a postfix operator.
    /// </summary>
    internal ExtensionPoint? CallPoint;
    internal int CallFlags;

    /// <summary>Whether a commit point in the alternative of <paramref name="kind"/> may repair in this call.</summary>
    public readonly bool AlternativeCommitted(int kind, bool postfix) =>
        CallPoint is { } point && (CallFlags & (postfix ? 2 : 1)) != 0 && point.IsCommitEnabled(kind);

    /// <summary>
    /// Repairs the failure of committed element <c>site.Element</c> at the current position (after
    /// trivia). Returns the element to resume at: <c>site.Element</c> to retry it after skipping, a
    /// later element (the caller pushes Missing nodes up to it), or <see cref="ResumeAtEnd"/> (the
    /// caller pushes Missing nodes for the rest and closes).
    /// </summary>
    public int Recover(RepairSite site)
    {
        var a = Arena;
        a.HasRecovery = true;
        int from = Position;
        int text = a.AddRepairText(site.Expected);
        if (++a.RepairCount > BuildArena.MaxRepairs) return GiveUp(from, text);
        if (site.Insert.Matches(_text, from))
        {
            a.PendingRepairText = text;
            return site.Element + 1;
        }
        int depth = 0, skippedEnd = from;
        while (true)
        {
            SkipTrivia();
            int p = Position;
            if (p >= _text.Length)
            {
                Skipped(from, skippedEnd, text);
                return ResumeAtEnd;
            }
            if (depth == 0)
            {
                foreach (var point in site.Sync)
                {
                    if (point.Element < site.Element || (point.Element == site.Element && p == from)) continue;
                    if (!point.First.Matches(_text, p)) continue;
                    Skipped(from, skippedEnd, text);
                    return point.Element;
                }
                if (EndsScope(p))
                {
                    Skipped(from, skippedEnd, text);
                    return ResumeAtEnd;
                }
            }
            depth = Math.Max(0, depth + SkipUnit());
            skippedEnd = Position;
        }
    }

    /// <summary>
    /// An item of the list just before committed element <c>site.Element</c> failed. Returns true
    /// after skipping to where another item can start (the loop goes on), false when the list ends
    /// here: at the end of input, at a closer of an enclosing block, or where the element after the
    /// list (or a later one) can start.
    /// </summary>
    public bool RecoverInList(RepairSite site)
    {
        SkipTrivia();
        int from = Position;
        if (from >= _text.Length || EndsScope(from) || Continues(site, from)) return false;
        var a = Arena;
        a.HasRecovery = true;
        int text = a.AddRepairText(site.Expected);
        if (++a.RepairCount > BuildArena.MaxRepairs)
        {
            GiveUp(from, text);
            return false;
        }
        int depth = 0;
        while (true)
        {
            depth = Math.Max(0, depth + SkipUnit());
            int skippedEnd = Position;
            SkipTrivia();
            int p = Position;
            if (p >= _text.Length)
            {
                Skipped(from, skippedEnd, text);
                return false;
            }
            if (depth > 0) continue;
            foreach (var point in site.Sync)
            {
                if (point.Element != site.Element - 1 || !point.First.Matches(_text, p)) continue;
                Skipped(from, skippedEnd, text);
                return true;
            }
            if (EndsScope(p) || Continues(site, p))
            {
                Skipped(from, skippedEnd, text);
                return false;
            }
        }
    }

    /// <summary>
    /// A separated list's separator failed (recovery pass). True, with the diagnostic pending for
    /// the next Missing node, when another item starts here and the element after the list does not:
    /// the separator alone is missing.
    /// </summary>
    public bool MissingSeparator(RepairSite site, string separator)
    {
        SkipTrivia();
        int at = Position;
        if (at >= _text.Length || Continues(site, at)) return false;
        foreach (var point in site.Sync)
        {
            if (point.Element != site.Element - 1 || !point.First.Matches(_text, at)) continue;
            var a = Arena;
            a.HasRecovery = true;
            if (++a.RepairCount > BuildArena.MaxRepairs) return false;
            a.PendingRepairText = a.AddRepairText(separator);
            return true;
        }
        return false;
    }

    /// <summary>Pushes a zero-width node of <paramref name="kind"/> flagged Missing; the first after a repair carries its diagnostic.</summary>
    public void PushMissing(int kind)
    {
        var a = Arena;
        int text = a.PendingRepairText;
        a.PendingRepairText = -1;
        a.PushChild(a.NewNode(kind, Position, 0, NodeFlags.Missing, text, 0));
    }

    /// <summary>Pushes an empty list: the shape of a repetition that recovery passed over.</summary>
    public readonly void PushEmptyList() =>
        Arena.PushChild(Arena.NewNode(SyntaxKinds.List, Position, 0, NodeFlags.None, 0, 0));

    readonly bool Continues(RepairSite site, int position)
    {
        foreach (var point in site.Sync)
            if (point.Element >= site.Element && point.First.Matches(_text, position)) return true;
        return false;
    }

    /// <summary>More than <see cref="BuildArena.MaxRepairs"/> repairs: skip the rest of the input.</summary>
    int GiveUp(int from, int text)
    {
        Position = _text.Length;
        Skipped(from, _text.Length, text);
        return ResumeAtEnd;
    }

    /// <summary>Records <c>[start, end)</c> as skipped (a marker node carrying the diagnostic), or, when nothing was skipped, hands the diagnostic to the next Missing node.</summary>
    readonly void Skipped(int start, int end, int text)
    {
        var a = Arena;
        if (end > start) a.PushChild(a.NewNode(SyntaxKinds.Skipped, start, end - start, NodeFlags.Skipped, text, 0));
        else a.PendingRepairText = text;
    }

    /// <summary>Skips one unit: a bracket (returns +1 / -1 for depth), a quoted string, an identifier run, or one character.</summary>
    int SkipUnit()
    {
        char c = _text[Position];
        if (c is '(' or '[' or '{')
        {
            Position++;
            return 1;
        }
        if (IsCloser(c))
        {
            Position++;
            return -1;
        }
        if (c is '"' or '\'')
        {
            int close = _text.Slice(Position + 1).IndexOf(c);
            Position = close < 0 ? _text.Length : Position + close + 2;
            return 0;
        }
        if (IsIdentifierPart(c))
        {
            do Position++;
            while (Position < _text.Length && IsIdentifierPart(_text[Position]));
            return 0;
        }
        Position++;
        return 0;
    }

    static bool IsCloser(char c) => c is ')' or ']' or '}';

    /// <summary>
    /// A closer at <paramref name="position"/> ends the current scope only when it closes the
    /// innermost bracket open there; any other closer is stray input (Plan 3b).
    /// </summary>
    bool EndsScope(int position) => _text[position] switch
    {
        ')' => OpenBracketAt(position) == '(',
        ']' => OpenBracketAt(position) == '[',
        '}' => OpenBracketAt(position) == '{',
        _ => false,
    };

    /// <summary>
    /// The innermost bracket still open at <paramref name="position"/>, or '\0'. It scans the text
    /// with the language's trivia, skips quoted strings, and ignores closers that match nothing. The
    /// scan resumes from the previous call when positions only grow within a parse.
    /// </summary>
    char OpenBracketAt(int position)
    {
        var a = Arena;
        if (position < a.BracketScanPosition) a.BracketScanPosition = a.BracketDepth = 0;
        int i = a.BracketScanPosition;
        while (i < position)
        {
            char c = _text[i];
            if (_triviaStart.Matches(c))
            {
                int after = _trivia(_text, i);
                if (after > i)
                {
                    i = after;
                    continue;
                }
            }
            if (c is '(' or '[' or '{')
            {
                if (a.BracketDepth == a.BracketStack.Length) Array.Resize(ref a.BracketStack, a.BracketDepth * 2);
                a.BracketStack[a.BracketDepth++] = c;
                i++;
            }
            else if (IsCloser(c))
            {
                char opener = c == ')' ? '(' : c == ']' ? '[' : '{';
                if (a.BracketDepth > 0 && a.BracketStack[a.BracketDepth - 1] == opener) a.BracketDepth--;
                i++;
            }
            else if (c is '"' or '\'')
            {
                int close = _text.Slice(i + 1).IndexOf(c);
                i = close < 0 ? _text.Length : i + close + 2;
            }
            else
            {
                i++;
            }
        }
        a.BracketScanPosition = i;
        return a.BracketDepth > 0 ? a.BracketStack[a.BracketDepth - 1] : '\0';
    }

    internal readonly int PeekPastTrivia() =>
        Position < _text.Length && _triviaStart.Matches(_text[Position]) ? _trivia(_text, Position) : Position;

    internal static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
