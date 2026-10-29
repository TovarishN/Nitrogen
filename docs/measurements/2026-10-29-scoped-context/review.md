# Production core review

A fresh gpt-6-astra/high reviewer examined the whole3be215da..0889e757 branch read-only and independently reproduced four Important issues in an external harness. No Critical or Minor findings were returned. The reviewer did not rerun the complete suite. One author fix pass follows the executing-plans contract; no second review seat is used.

- Nullable context-separated lists: four direct/indirect * and + compiler cases failed before the fix. Validation now rejects a nullable item/nullable separator pair when context-dependent, before source emission.
- Shared-choice operand precedence: both false and true context cases parsed -x+x with Neg as the outer owner. The flattened emitter now propagates the alternative operand precedence through scoped sequences/branches, restoring Add(Neg(x),x).
- Unlabeled scoped fields: the compiler regression failed for the missing Child/Child1/Child2 properties. Scope unwrapping now participates in field discovery and duplicate numbering; actual generated typed fields and direct parent slots are exercised too.
- Context rename: two capture/ambiguity regressions accepted an unsafe rename before the fix. The proposed edits now reparse and rebind all context occurrences, preserving every unrelated identity. A qualified unrelated binding remains renameable.

The nine focused reproducer cases all failed first. All69 context tests passed after the fixes; the final full gate, including two additional generated-field/safe-rename guards, passes2321/2321 with zero failures/skips. Logs are retained by the implementing session. These tests establish the fixes; they do not constitute a second independent review.

The review explicitly leaves dependency repinning/CSharp1-4 integration to the dependent repository task, full CSharp5 source admission/legality and semantic behavior to their source plan, live editor acceptance unclaimed, and stable timing/retained-memory bounds unclaimed. These boundaries are recorded as rulings in the execution ledger rather than silently treating them as completed work.
