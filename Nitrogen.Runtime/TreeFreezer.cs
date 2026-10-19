using System.Buffers;

namespace Nitrogen;

/// <summary>Compacts the reachable part of a <see cref="BuildArena"/> into a preorder <see cref="SyntaxTree"/>.</summary>
internal static class TreeFreezer
{
    /// <summary>
    /// Trees up to this many nodes live in pooled arrays, so warm parses stay allocation-free.
    /// Larger trees get exact-size arrays: the pool would round them up to the next power of two
    /// and keep them after disposal.
    /// </summary>
    public const int PooledTreeNodes = 1 << 17;

    /// <param name="arenaToTree">When given (an incremental parse), filled with each arena node's tree index, -1 for nodes the tree dropped.</param>
    public static SyntaxTree Freeze(BuildArena a, int root, string text, Language? language, int pooledTreeNodes = PooledTreeNodes,
        int[]? arenaToTree = null)
    {
        if (arenaToTree is not null) Array.Fill(arenaToTree, -1);
        // Without ambiguity or recovery every reachable node occurs once and is kept, so the arena's
        // counts bound the tree and the counting pass is skipped. Candidates under an Ambiguous node
        // can share subtrees (copied once per occurrence), and recovery's Skipped markers are dropped
        // from the tree; both need the exact count. So does an exact-size (unpooled) tree, since the
        // arena also holds nodes discarded by backtracking.
        bool recovery = a.HasRecovery;
        a.RepairEntryCount = 0;
        int nodes, slots, sp = 0;
        if (a.HasAmbiguity || recovery || a.NodeCount + a.GraftExtraNodes > pooledTreeNodes)
        {
            nodes = slots = 0;
            Push(a, ref sp, root, -1);
            while (sp > 0)
            {
                int n = a.ScratchNode[--sp];
                nodes++;
                if (a.Kind[n] == SyntaxKinds.Graft)
                {
                    var graft = a.Grafts[n];
                    nodes += graft.Size - 1;
                    slots += graft.SlotEnd - graft.SlotStart;
                    continue;
                }
                int count = a.ChildCount[n], first = a.ChildStart[n];
                for (int k = 0; k < count; k++)
                {
                    int child = a.ChildLog[first + k];
                    if (child == SyntaxTree.NoNode)
                    {
                        slots++;
                        continue;
                    }
                    if (a.Kind[child] == SyntaxKinds.Skipped) continue;
                    slots++;
                    Push(a, ref sp, child, -1);
                }
            }
        }
        else
        {
            nodes = a.NodeCount + a.GraftExtraNodes;
            slots = a.ChildLogCount + a.GraftExtraSlots;
        }

        bool pooled = nodes <= pooledTreeNodes;
        var kinds = language?.Kinds ?? DenseKindTable.Shared;
        var kind = Allocate<ushort>(nodes, pooled);
        var start = Allocate<int>(nodes, pooled);
        var length = Allocate<int>(nodes, pooled);
        // childStart has a sentinel: node n's children are slots childStart[n]..childStart[n + 1).
        var childStart = Allocate<int>(nodes + 1, pooled);
        var children = Allocate<int>(slots, pooled);

        // Preorder copy. Each node reserves one contiguous child slice; its children fill their
        // slots when they are visited, and a null slot is written as NoNode without a visit. A
        // Missing node keeps its diagnostic text index in the arena's ChildStart; a Skipped marker
        // child becomes a repair and flags its parent.
        var listShapes = new Dictionary<int, TemplateListShape>();
        int next = 0, cursor = 0;
        Push(a, ref sp, root, -1);
        while (sp > 0)
        {
            sp--;
            int old = a.ScratchNode[sp], slot = a.ScratchSlot[sp];
            int n = next++;
            if (arenaToTree is not null && arenaToTree[old] < 0) arenaToTree[old] = n;
            if (a.Kind[old] == SyntaxKinds.Graft)
            {
                // A reused subtree: copy its block from the previous tree; its rows and slots are already complete.
                var graft = a.Grafts[old];
                if (slot >= 0) children[slot] = n;
                a.GraftSource!.CopyBlock(graft, n, cursor, kind, start, length, childStart, children);
                next = n + graft.Size;
                cursor += graft.SlotEnd - graft.SlotStart;
                a.ReusedNodes += graft.Size;
                continue;
            }
            if (a.TemplateLists?.TryGetValue(old, out var shape) == true) listShapes[n] = shape;
            kind[n] = DenseKindTable.Pack(kinds.DenseKind(a.Kind[old]), a.Flags[old]);
            start[n] = a.Start[old];
            length[n] = a.Length[old];
            if (slot >= 0) children[slot] = n;
            int count = a.ChildCount[old], first = a.ChildStart[old];
            int kept = count;
            if (recovery)
            {
                if ((a.Flags[old] & NodeFlags.Missing) != 0 && a.ChildStart[old] >= 0)
                    a.AddRepairEntry(new TextSpan(a.Start[old], 0), DiagnosticCode.Missing, a.ChildStart[old]);
                for (int k = 0; k < count; k++)
                {
                    int child = a.ChildLog[first + k];
                    if (child == SyntaxTree.NoNode || a.Kind[child] != SyntaxKinds.Skipped) continue;
                    kept--;
                    kind[n] |= DenseKindTable.Pack(0, NodeFlags.Skipped);
                    a.AddRepairEntry(new TextSpan(a.Start[child], a.Length[child]), DiagnosticCode.Skipped, a.ChildStart[child]);
                }
            }
            childStart[n] = cursor;
            int slice = cursor;
            cursor += kept;
            for (int k = count - 1, position = kept - 1; k >= 0; k--)
            {
                int child = a.ChildLog[first + k];
                if (child == SyntaxTree.NoNode)
                {
                    children[slice + position--] = SyntaxTree.NoNode;
                    continue;
                }
                if (recovery && a.Kind[child] == SyntaxKinds.Skipped) continue;
                Push(a, ref sp, child, slice + position--);
            }
        }
        if (recovery && a.TrailingStart >= 0)
            a.AddRepairEntry(new TextSpan(a.TrailingStart, text.Length - a.TrailingStart), DiagnosticCode.ExpectedEndOfInput, -1);

        // Trivia is built from the tree on first access (SyntaxTree.Trivia).
        childStart[next] = cursor;

        SortRepairs(a);
        var skipped = Array.Empty<TextSpan>();
        int skippedCount = 0;
        if (recovery)
        {
            int bound = a.RepairEntryCount;
            if (!pooled)
            {
                bound = 0;
                for (int i = 0; i < a.RepairEntryCount; i++)
                    if (a.RepairCodes[i] != DiagnosticCode.Missing) bound++;
            }
            skipped = Allocate<TextSpan>(bound, pooled);
            for (int i = 0; i < a.RepairEntryCount; i++)
                if (a.RepairCodes[i] != DiagnosticCode.Missing) skipped[skippedCount++] = a.RepairSpans[i];
        }

        // The snapshot is read after every DenseKind call above, so it maps all of this tree's ids.
        return new SyntaxTree(text, language, next, kind, kinds.Snapshot, start, length,
            childStart, children, skipped, skippedCount, pooled) { TemplateLists = listShapes };
    }

    public static SyntaxTree ErrorTree(string text, Language? language)
    {
        var kind = Rent<ushort>(1);
        var start = Rent<int>(1);
        var length = Rent<int>(1);
        var childStart = Rent<int>(2);
        kind[0] = DenseKindTable.Pack(SyntaxKinds.Error, NodeFlags.Error); // a built-in: its dense id is the kind itself
        start[0] = 0;
        length[0] = text.Length;
        childStart[0] = childStart[1] = 0;
        return new SyntaxTree(text, language, 1, kind, (language?.Kinds ?? DenseKindTable.Shared).Snapshot, start, length,
            childStart, Rent<int>(1), Array.Empty<TextSpan>(), 0, pooled: true);
    }

    /// <summary>Insertion sort of the repair entries by start; there are at most a few hundred.</summary>
    static void SortRepairs(BuildArena a)
    {
        for (int i = 1; i < a.RepairEntryCount; i++)
        {
            var span = a.RepairSpans[i];
            var code = a.RepairCodes[i];
            int text = a.RepairTextIndex[i];
            int j = i - 1;
            while (j >= 0 && a.RepairSpans[j].Start > span.Start)
            {
                a.RepairSpans[j + 1] = a.RepairSpans[j];
                a.RepairCodes[j + 1] = a.RepairCodes[j];
                a.RepairTextIndex[j + 1] = a.RepairTextIndex[j];
                j--;
            }
            a.RepairSpans[j + 1] = span;
            a.RepairCodes[j + 1] = code;
            a.RepairTextIndex[j + 1] = text;
        }
    }

    static void Push(BuildArena a, ref int sp, int node, int slot)
    {
        if (sp == a.ScratchNode.Length)
        {
            Array.Resize(ref a.ScratchNode, sp * 2);
            Array.Resize(ref a.ScratchSlot, sp * 2);
        }
        a.ScratchNode[sp] = node;
        a.ScratchSlot[sp] = slot;
        sp++;
    }

    static T[] Rent<T>(int count) => ArrayPool<T>.Shared.Rent(Math.Max(count, 1));

    static T[] Allocate<T>(int count, bool pooled) =>
        pooled ? Rent<T>(count) : count == 0 ? [] : GC.AllocateUninitializedArray<T>(count);
}
