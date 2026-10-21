# Captures in diagnostic messages

Status: implemented locally; 2,146 tests and zero-warning Release build pass.

Approved scope: the user requested implementation of the previously described typed capture access on 2026-10-21.

A declaration such as `(pass: Text)` introduces a scalar syntax view local to that diagnostic's message expression. `pass.Text` exposes source text; scalar interpolation uses the existing view string conversion. `(items: Node*)` introduces an immutable IReadOnlyList<TView> with Count, indexing and enumeration. Captures do not become semantic fields or leak into other checks. Anonymous discards remain unbound.

Use generated semantic view types when the category has one, including imported categories; otherwise use SemanticNode, as child fields do. Captured properties evaluate in the original FileSemantics context. Reject names colliding with containing children/properties/reserved view members using NGR0149; escaped C# keywords remain supported.

Evaluate each pattern once per diagnostic invocation and retain its result through synchronous message evaluation. Mismatches do not evaluate messages. Preserve existing match-error diagnostics, ordinary checks, severity and locations. Keep existing SemanticsCheck constructor and Holds/Message APIs; add a capture-aware constructor and a shared internal evaluation path.

Sequence: failing configured-language tests; runtime capture views and single-result evaluation; typed generated message parameters and collision validation; exact XML example, CLI/service checks and docs; full suite/build and independent review. No release or installation is included in this request. Live editor interaction is separate from automated checks.
