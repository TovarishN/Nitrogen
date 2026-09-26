using System.Buffers;

namespace Nitrogen;

/// <summary>Compacts the reachable part of a <see cref="BuildArena"/> into a preorder <see cref="SyntaxTree"/>.</summary>
internal static class TreeFreezer
{
    public static SyntaxTree Freeze(BuildArena a, int root, string text, Language? language)
    {
        // Without ambiguity or recovery every reachable node occurs once and is kept, so the arena's
        // counts bound the tree and the counting pass is skipped. Candidates under an Ambiguous node
        // can share subtrees (copied once per occurrence), and recovery's Skipped markers are dropped
        // from the tree; both need the exact count.
        bool recovery = a.HasRecovery;
        a.RepairEntryCount = 0;
        int nodes, slots, sp = 0;
        if (a.HasAmbiguity || recovery)
        {
            nodes = slots = 0;
            Push(a, ref sp, root, -1);
            while (sp > 0)
            {
                int n = a.ScratchNode[--sp];
                nodes++;
                int count = a.ChildCount[n], first = a.ChildStart[n];
                for (int k = 0; k < count; k++)
                {
                    int child = a.ChildLog[first + k];
                    if (a.Kind[child] == SyntaxKinds.Skipped) continue;
                    slots++;
                    Push(a, ref sp, child, -1);
                }
            }
        }
        else
        {
            nodes = a.NodeCount;
            slots = a.ChildLogCount;
        }

        var kind = Rent<int>(nodes);
        var start = Rent<int>(nodes);
        var length = Rent<int>(nodes);
        var flags = Rent<NodeFlags>(nodes);
        var childStart = Rent<int>(nodes);
        var childCount = Rent<int>(nodes);
        var children = Rent<int>(slots);

        // Preorder copy. Each node reserves one contiguous child slice; its children fill their
        // slots when they are visited. A Missing node keeps its diagnostic text index in the arena's
        // ChildStart; a Skipped marker child becomes a repair and flags its parent.
        int next = 0, cursor = 0;
        Push(a, ref sp, root, -1);
        while (sp > 0)
        {
            sp--;
            int old = a.ScratchNode[sp], slot = a.ScratchSlot[sp];
            int n = next++;
            kind[n] = a.Kind[old];
            start[n] = a.Start[old];
            length[n] = a.Length[old];
            flags[n] = a.Flags[old];
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
                    if (a.Kind[child] != SyntaxKinds.Skipped) continue;
                    kept--;
                    flags[n] |= NodeFlags.Skipped;
                    a.AddRepairEntry(new TextSpan(a.Start[child], a.Length[child]), DiagnosticCode.Skipped, a.ChildStart[child]);
                }
            }
            childStart[n] = cursor;
            childCount[n] = kept;
            int slice = cursor;
            cursor += kept;
            for (int k = count - 1, position = kept - 1; k >= 0; k--)
            {
                int child = a.ChildLog[first + k];
                if (recovery && a.Kind[child] == SyntaxKinds.Skipped) continue;
                Push(a, ref sp, child, slice + position--);
            }
        }
        if (recovery && a.TrailingStart >= 0)
            a.AddRepairEntry(new TextSpan(a.TrailingStart, text.Length - a.TrailingStart), DiagnosticCode.ExpectedEndOfInput, -1);

        // Trivia: gaps between consecutive non-empty leaves (preorder is text order). Skipped text
        // is a gap too.
        var trivia = Rent<TextSpan>(next + 1);
        int triviaCount = 0, previousEnd = 0;
        for (int n = 0; n < next; n++)
        {
            if (childCount[n] != 0 || length[n] == 0) continue;
            if (start[n] > previousEnd) trivia[triviaCount++] = new TextSpan(previousEnd, start[n] - previousEnd);
            previousEnd = Math.Max(previousEnd, start[n] + length[n]);
        }
        if (text.Length > previousEnd) trivia[triviaCount++] = new TextSpan(previousEnd, text.Length - previousEnd);

        SortRepairs(a);
        var skipped = Array.Empty<TextSpan>();
        int skippedCount = 0;
        if (recovery)
        {
            skipped = Rent<TextSpan>(a.RepairEntryCount);
            for (int i = 0; i < a.RepairEntryCount; i++)
                if (a.RepairCodes[i] != DiagnosticCode.Missing) skipped[skippedCount++] = a.RepairSpans[i];
        }

        return new SyntaxTree(text, language, next, kind, start, length, flags,
            childStart, childCount, children, trivia, triviaCount, skipped, skippedCount);
    }

    public static SyntaxTree ErrorTree(string text, Language? language)
    {
        var kind = Rent<int>(1);
        var start = Rent<int>(1);
        var length = Rent<int>(1);
        var flags = Rent<NodeFlags>(1);
        var childStart = Rent<int>(1);
        var childCount = Rent<int>(1);
        kind[0] = SyntaxKinds.Error;
        start[0] = 0;
        length[0] = text.Length;
        flags[0] = NodeFlags.Error;
        childStart[0] = 0;
        childCount[0] = 0;
        return new SyntaxTree(text, language, 1, kind, start, length, flags,
            childStart, childCount, Rent<int>(1), Rent<TextSpan>(1), 0, Array.Empty<TextSpan>(), 0);
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
}
