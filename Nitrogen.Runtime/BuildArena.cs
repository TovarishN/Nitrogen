namespace Nitrogen;

/// <summary>
/// Parse-time scratch storage, reused across parses on one thread; it grows and never shrinks.
/// Nodes are struct-of-arrays. A node's children are a contiguous slice of <see cref="ChildLog"/>,
/// written when the node closes. <see cref="ChildStack"/> holds the finished children of the
/// frames that are still open.
/// </summary>
internal sealed class BuildArena
{
    public const int MaxExpected = 32;

    public int[] Kind = new int[256];
    public int[] Start = new int[256];
    public int[] Length = new int[256];
    public NodeFlags[] Flags = new NodeFlags[256];
    public int[] ChildStart = new int[256];
    public int[] ChildCount = new int[256];
    public int NodeCount;

    public int[] ChildLog = new int[256];
    public int ChildLogCount;

    public int[] ChildStack = new int[64];
    public int ChildStackCount;

    public int[] FrameKind = new int[32];
    public int[] FramePosition = new int[32];
    public int[] FrameChildBase = new int[32];
    public int FrameCount;

    public int MemoWrites;
    public int MemoHits;
    public int MemoMisses;

    /// <summary>An Ambiguous node was created; the freeze must count shared nodes.</summary>
    public bool HasAmbiguity;

    /// <summary>The recovery pass (issue 235): committed elements repair instead of failing.</summary>
    public bool Recovering;

    /// <summary>3 in the recovery pass, 0 otherwise: call bits join the memo key only in pass 2 (Plan 2b).</summary>
    public int CallFlagMask;

    /// <summary>A repair happened: the freeze drops Skipped markers and collects the repairs.</summary>
    public bool HasRecovery;

    /// <summary>Repairs attempted, backtracked ones included; past <see cref="MaxRepairs"/> the rest of the input is skipped.</summary>
    public int RepairCount;

    public const int MaxRepairs = 100;

    /// <summary>Diagnostic texts. Missing and Skipped arena nodes keep an index here in <see cref="ChildStart"/>.</summary>
    public string[] RepairTexts = new string[16];
    public int RepairTextCount;

    /// <summary>The text of the next Missing node; -1 once taken.</summary>
    public int PendingRepairText = -1;

    /// <summary>Where input left after the start rule begins in the recovery pass; -1 when none is left.</summary>
    public int TrailingStart = -1;

    /// <summary>Recovery's bracket scan (Plan 3b): the brackets open at <see cref="BracketScanPosition"/>, innermost last.</summary>
    public char[] BracketStack = new char[16];
    public int BracketDepth;
    public int BracketScanPosition;

    /// <summary>The repairs reachable from the frozen root, in text order; filled by the freeze.</summary>
    public TextSpan[] RepairSpans = new TextSpan[16];
    public DiagnosticCode[] RepairCodes = new DiagnosticCode[16];
    public int[] RepairTextIndex = new int[16];
    public int RepairEntryCount;

    public int FurthestPosition;
    public string[] ExpectedNames = new string[MaxExpected];
    public bool[] ExpectedIsLiteral = new bool[MaxExpected];
    public int ExpectedCount;

    /// <summary>Freeze traversal scratch: (arena node, destination child slot) pairs.</summary>
    public int[] ScratchNode = new int[64];
    public int[] ScratchSlot = new int[64];

    public BuildArena() => Clear();

    public void Clear()
    {
        NodeCount = ChildLogCount = ChildStackCount = FrameCount = 0;
        MemoWrites = MemoHits = MemoMisses = 0;
        FurthestPosition = -1;
        ExpectedCount = 0;
        HasAmbiguity = false;
        Recovering = false;
        CallFlagMask = 0;
        HasRecovery = false;
        RepairCount = RepairTextCount = RepairEntryCount = 0;
        PendingRepairText = -1;
        TrailingStart = -1;
        BracketDepth = BracketScanPosition = 0;
    }

    public int NewNode(int kind, int start, int length, NodeFlags flags, int childStart, int childCount)
    {
        if (NodeCount == Kind.Length) GrowNodes();
        int n = NodeCount++;
        Kind[n] = kind;
        Start[n] = start;
        Length[n] = length;
        Flags[n] = flags;
        ChildStart[n] = childStart;
        ChildCount[n] = childCount;
        return n;
    }

    public void PushChild(int node)
    {
        if (ChildStackCount == ChildStack.Length) Array.Resize(ref ChildStack, ChildStack.Length * 2);
        ChildStack[ChildStackCount++] = node;
    }

    /// <summary>Copies <c>ChildStack[from..from+count)</c> into the child log; returns the slice start.</summary>
    public int AppendChildLog(int from, int count)
    {
        while (ChildLogCount + count > ChildLog.Length) Array.Resize(ref ChildLog, ChildLog.Length * 2);
        Array.Copy(ChildStack, from, ChildLog, ChildLogCount, count);
        int start = ChildLogCount;
        ChildLogCount += count;
        return start;
    }

    public void PushFrame(int kind, int position, int childBase)
    {
        if (FrameCount == FrameKind.Length)
        {
            int capacity = FrameCount * 2;
            Array.Resize(ref FrameKind, capacity);
            Array.Resize(ref FramePosition, capacity);
            Array.Resize(ref FrameChildBase, capacity);
        }
        FrameKind[FrameCount] = kind;
        FramePosition[FrameCount] = position;
        FrameChildBase[FrameCount] = childBase;
        FrameCount++;
    }

    /// <summary>Records what was expected at <paramref name="position"/>. Only the furthest position is kept.</summary>
    public void Expect(int position, string name, bool isLiteral)
    {
        if (position < FurthestPosition) return;
        if (position > FurthestPosition)
        {
            FurthestPosition = position;
            ExpectedCount = 0;
        }
        for (int i = 0; i < ExpectedCount; i++)
            if (ExpectedIsLiteral[i] == isLiteral && string.Equals(ExpectedNames[i], name, StringComparison.Ordinal))
                return;
        if (ExpectedCount == MaxExpected) return;
        ExpectedNames[ExpectedCount] = name;
        ExpectedIsLiteral[ExpectedCount] = isLiteral;
        ExpectedCount++;
    }

    public int AddRepairText(string text)
    {
        if (RepairTextCount == RepairTexts.Length) Array.Resize(ref RepairTexts, RepairTextCount * 2);
        RepairTexts[RepairTextCount] = text;
        return RepairTextCount++;
    }

    public void AddRepairEntry(TextSpan span, DiagnosticCode code, int text)
    {
        if (RepairEntryCount == RepairSpans.Length)
        {
            int capacity = RepairEntryCount * 2;
            Array.Resize(ref RepairSpans, capacity);
            Array.Resize(ref RepairCodes, capacity);
            Array.Resize(ref RepairTextIndex, capacity);
        }
        RepairSpans[RepairEntryCount] = span;
        RepairCodes[RepairEntryCount] = code;
        RepairTextIndex[RepairEntryCount] = text;
        RepairEntryCount++;
    }

    void GrowNodes()
    {
        int capacity = Kind.Length * 2;
        Array.Resize(ref Kind, capacity);
        Array.Resize(ref Start, capacity);
        Array.Resize(ref Length, capacity);
        Array.Resize(ref Flags, capacity);
        Array.Resize(ref ChildStart, capacity);
        Array.Resize(ref ChildCount, capacity);
    }
}
