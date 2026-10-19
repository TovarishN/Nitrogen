namespace Nitrogen;

/// <summary>
/// Parse-time scratch storage, reused across parses on one thread; it grows and never shrinks.
/// Nodes are struct-of-arrays. A node's children are a contiguous slice of <see cref="ChildLog"/>,
/// written when the node closes. <see cref="ChildStack"/> holds the finished children of the
/// frames that are still open. A child entry may be <see cref="SyntaxTree.NoNode"/>, a null slot
/// (absent optional or empty list); readers of child entries skip it.
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

    /// <summary>
    /// A tracked parse (incremental reparse): text reads are recorded in <see cref="ParserState.Reads"/> and
    /// each rule result in <see cref="Records"/>. Off for plain, recovery and template parses.
    /// </summary>
    public bool Tracking;

    public ReuseRecord[] Records = [];
    public int RecordCount;

    /// <summary>A reparse's previous tree, which <see cref="SyntaxKinds.Graft"/> nodes copy from.</summary>
    public SyntaxTree? GraftSource;

    /// <summary>Each graft's block in <see cref="GraftSource"/>, by arena node.</summary>
    public readonly Dictionary<int, Graft> Grafts = new();

    /// <summary>Nodes and child slots the grafts add beyond their own arena node: with the arena's counts, a bound on the tree.</summary>
    public int GraftExtraNodes, GraftExtraSlots;

    /// <summary>Tree nodes the last freeze copied from <see cref="GraftSource"/>.</summary>
    public int ReusedNodes;

    /// <summary>Nodes the arena can hold before it grows.</summary>
    public int NodeCapacity => Kind.Length;

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

    internal Dictionary<int, TemplateListShape>? TemplateLists;

    public BuildArena() => Clear();

    public void Clear()
    {
        TemplateLists?.Clear();
        NodeCount = ChildLogCount = ChildStackCount = FrameCount = RecordCount = 0;
        GraftSource = null;
        if (Grafts.Count > 0) Grafts.Clear();
        GraftExtraNodes = GraftExtraSlots = ReusedNodes = 0;
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
        TemplateLists?.Remove(n);
        Kind[n] = kind;
        Start[n] = start;
        Length[n] = length;
        Flags[n] = flags;
        ChildStart[n] = childStart;
        ChildCount[n] = childCount;
        return n;
    }

    /// <summary>
    /// Pushes a graft for the previous tree's node <paramref name="oldNode"/>, moved by <paramref name="posDelta"/>,
    /// and returns its arena node. <paramref name="entryStart"/>..<paramref name="entryEnd"/> is the old call's range.
    /// </summary>
    public int NewGraft(int oldNode, int posDelta, int entryStart, int entryEnd)
    {
        var tree = GraftSource!;
        var span = tree.Span(oldNode);
        int end = tree.SubtreeEnd(oldNode);
        int slotStart = tree.ChildStart(oldNode), slotEnd = tree.ChildStart(end);
        int node = NewNode(SyntaxKinds.Graft, span.Start + posDelta, span.Length, NodeFlags.None, oldNode, 0);
        Grafts[node] = new Graft(oldNode, end - oldNode, slotStart, slotEnd, posDelta, entryStart, entryEnd);
        GraftExtraNodes += end - oldNode - 1;
        GraftExtraSlots += slotEnd - slotStart;
        return node;
    }

    public void AddRecord(in ReuseRecord record)
    {
        if (RecordCount == Records.Length) Array.Resize(ref Records, Math.Max(64, RecordCount * 2));
        Records[RecordCount++] = record;
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

/// <summary>
/// A grafted block of the previous tree: <c>[Root, Root + Size)</c> in preorder, its child slots
/// <c>[SlotStart, SlotEnd)</c>, how far positions move, and the old call's range (for carrying its inner
/// reuse entries over).
/// </summary>
internal readonly record struct Graft(int Root, int Size, int SlotStart, int SlotEnd, int PosDelta, int EntryStart, int EntryEnd);
