namespace Nitrogen;

/// <summary>
/// Nitra-style extensible rules: memoized, longest match among prefix alternatives, then a
/// Pratt loop over postfix alternatives. Ties (same end, same precedence) produce an Ambiguous
/// node instead of being resolved silently, so the result does not depend on module order.
/// </summary>
public static unsafe class ExtensibleRules
{
    struct Candidates
    {
        public int Base;       // child-stack index where the winners start
        public int Count;      // number of tied winners on the child stack
        public int End;
        public int Precedence;
        public bool Repaired;   // a winner repaired in the recovery pass
    }

    /// <param name="callFlags">
    /// This call's recovery bits (issue 235, Plan 2b): 1 = the point failing here fails the parse,
    /// 2 = so does stopping here before a postfix operator. Generated per call site; 0 when unknown.
    /// </param>
    public static bool ParseExtensible(this ref ParserState s, ExtensionPointDecl decl, int minPrecedence, int callFlags = 0)
    {
        if ((uint)minPrecedence > 255) throw new ArgumentOutOfRangeException(nameof(minPrecedence));
        var language = s.Language ?? throw new InvalidOperationException("Extensible rules need a Language.");
        var point = language.GetExtensionPoint(decl);
        var arena = s.Arena;
        // In the recovery pass the call bits join the key: a repair made for one call site must not
        // be reused at another that may not repair.
        int ruleKey = (decl.GlobalId << 10) | ((callFlags & arena.CallFlagMask) << 8) | minPrecedence;
        int start = s.Position;

        if (s.Memo.TryGet(ruleKey, start, out int memoNode, out int memoEnd))
        {
            arena.MemoHits++;
            if (memoNode < 0) return false;
            arena.PushChild(memoNode);
            s.Position = memoEnd;
            return true;
        }
        arena.MemoMisses++;

        var callerPoint = s.CallPoint;
        int callerFlags = s.CallFlags;
        s.CallPoint = point;
        s.CallFlags = callFlags;
        bool parsed = ParsePrefix(ref s, point);
        if (parsed) ParsePostfix(ref s, point, minPrecedence);
        s.CallPoint = callerPoint;
        s.CallFlags = callerFlags;

        if (!parsed)
        {
            s.Memo.Set(ruleKey, start, -1, -1);
            arena.MemoWrites++;
            return false;
        }
        s.Memo.Set(ruleKey, start, arena.ChildStack[arena.ChildStackCount - 1], s.Position);
        arena.MemoWrites++;
        return true;
    }

    static bool ParsePrefix(ref ParserState s, ExtensionPoint point)
    {
        int start = s.Position;
        int bucket = ExtensionPoint.Bucket(s.Text, start);
        var candidates = new Candidates { Base = s.Arena.ChildStackCount };

        foreach (int index in point.PrefixCandidates(bucket))
        {
            ref readonly var ext = ref point.Prefix[index];
            var mark = s.Mark();
            int repairs = s.Arena.RepairCount;
            s.Open(ext.Kind);
            var parse = ext.Parse;
            if (parse(ref s) && s.Position > start)
            {
                s.Close();
                Consider(ref s, ref candidates, ext.Precedence, s.Arena.RepairCount != repairs, mark, start);
            }
            else
            {
                s.Reset(mark);
            }
        }

        if (candidates.Count == 0)
        {
            s.Arena.Expect(start, point.Decl.Name, isLiteral: false);
            return false;
        }
        Finish(ref s, ref candidates, start);
        return true;
    }

    static void ParsePostfix(ref ParserState s, ExtensionPoint point, int minPrecedence)
    {
        var arena = s.Arena;
        while (true)
        {
            int left = arena.ChildStack[arena.ChildStackCount - 1];
            int start = s.Position;
            int peek = s.PeekPastTrivia();
            int bucket = ExtensionPoint.Bucket(s.Text, peek);

            arena.ChildStackCount--; // the left operand becomes each candidate's first child
            var candidates = new Candidates { Base = arena.ChildStackCount };

            foreach (int index in point.PostfixCandidates(bucket))
            {
                ref readonly var ext = ref point.Postfix[index];
                if (ext.Precedence <= minPrecedence) continue;
                var mark = s.Mark();
                int repairs = arena.RepairCount;
                s.Open(ext.Kind);
                arena.PushChild(left);
                var parseRest = ext.ParseRest;
                if (parseRest(ref s) && s.Position > start)
                {
                    s.Close();
                    Consider(ref s, ref candidates, ext.Precedence, arena.RepairCount != repairs, mark, start);
                }
                else
                {
                    s.Reset(mark);
                }
            }

            if (candidates.Count == 0)
            {
                arena.PushChild(left);
                s.Position = start;
                return;
            }
            Finish(ref s, ref candidates, start);
        }
    }

    /// <summary>
    /// The candidate just closed is on top of the child stack at <c>s.Position</c>. Longest wins, then
    /// an unrepaired candidate over a repaired one (recovery pass), then the higher precedence.
    /// </summary>
    static void Consider(ref ParserState s, scoped ref Candidates candidates, int precedence, bool repaired,
        scoped in ParseMark beforeCandidate, int start)
    {
        var arena = s.Arena;
        int end = s.Position;
        bool wins = candidates.Count == 0 || end > candidates.End
            || (end == candidates.End && ((candidates.Repaired && !repaired)
                || (repaired == candidates.Repaired && precedence > candidates.Precedence)));
        if (wins)
        {
            int node = arena.ChildStack[arena.ChildStackCount - 1];
            arena.ChildStackCount = candidates.Base;
            arena.PushChild(node);
            candidates.Count = 1;
            candidates.End = end;
            candidates.Precedence = precedence;
            candidates.Repaired = repaired;
        }
        else if (end == candidates.End && precedence == candidates.Precedence && repaired == candidates.Repaired)
        {
            candidates.Count++;
        }
        else
        {
            s.Reset(beforeCandidate);
        }
        s.Position = start;
    }

    static void Finish(ref ParserState s, scoped ref Candidates candidates, int start)
    {
        if (candidates.Count > 1)
        {
            s.Arena.HasAmbiguity = true;
            s.Arena.PushFrame(SyntaxKinds.Ambiguous, start, candidates.Base);
            s.Close(NodeFlags.Ambiguous);
        }
        s.Position = candidates.End;
    }
}
