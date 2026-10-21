# Diagnostic capture message validation

Branch: codex/diagnostic-capture-messages. Scope approved by the user's request to implement typed captures in diagnostic messages.

- Initial configured-language tests: RED (7 failures from unresolved message capture names), then GREEN with runtime capture views and generated typed parameters.
- Exact scalar XML capture tests pass, including direct password elements with surrounding siblings.
- Scalar interpolation, imported typed semantic properties, read-only list captures, repeated capture backtracking, message-local scopes and escaped C# names pass.
- Ordinary check evaluation-order regression: RED, then GREEN preserving condition → location → message order.
- Public legacy SemanticsCheck constructor and Holds/Message APIs remain available. FileSemantics uses one match result for message evaluation.
- CLI and language-service configured XML end-to-end tests pass; source grammar has no compilation diagnostics and input produces XML1001 containing the captured text.
- Focused runtime/semantic gate: 57 passed. Integrated XML/CLI/language-service gate: 46 passed.
- Existing XML example warning behavior is preserved; README documents the scalar capture variant and list alternative.
- Full Debug suite: 2,146/2,146 passed (33 seconds). Full Release solution build passed with zero warnings/errors. Independent review found no actionable defects.
- Actual VS Code/Rider visual verification: not performed. No installation, release, commit or push included.

- Final diff whitespace and documentation-link checks passed.
- Review suggested optional additional alias/reserved-name test cases; no implementation defect was identified. Existing coverage includes the XML Text category, imported typed views and child-name collision diagnostics.

## Release update

User authorized merge, push, version and release. Target: 0.18.1. Preserve the user's saved scalar XML pattern and direct `{pass}` interpolation. Align example tests/docs with its exactly-one-Text-node behavior; make test fixtures replace the whole diagnostic block instead of assuming the old capture-free example. Recheck exposed five fixture/example expectation failures before these changes. Prior no-commit/release scope is superseded.

Final 0.18.1 local release gate: 2,146 .NET tests passed after aligning fixtures with the user's saved scalar XML pattern; full Release solution build passed with zero warnings/errors; VS Code compilation and 41 extension tests passed; documentation link and diff whitespace checks passed. Independent runtime/generator review from implementation remains applicable (no production changes since that review).
