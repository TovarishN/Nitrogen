# Sequence-pattern implementation ledger

Plan: `2026-10-21-sequence-patterns-and-wildcards.md`; approved design dated 2026-10-21.

- Preflight: Tasks 1–5 share pattern metadata, compiled plans and helper/editor parsing. Interfaces agree; implement in sequence.
- Ruling: work in this checkout on `codex/sequence-patterns-wildcards`, preserving the untracked approved documents. No commits or pushes. This keeps the requested direct execution visible in the user's workspace; isolation is a local branch rather than a second checkout.
- Baseline: sandboxed MSBuild cannot open its local pipes. Retrying focused tests with authorized local execution and a single build worker.
- Tasks 1–5: implemented and validated by the focused gate below. Task 6: automated acceptance complete; live editor visual acceptance pending.

- Task 1: wildcard tests RED (12 failures), then quotation regression gate GREEN (105 tests).
- Task 2: missing options API RED, then budget/pattern/helper gate GREEN (95 tests).
- Task 3: sequence cases RED (13 failures), then pattern/sequence gate GREEN (98 tests).
- Task 4: workspace validation RED on unknown `$_` and reserved-name handling; integration in progress.
- Ruling: migrate the existing XmlPatterns diagnostic consumer to anonymous wildcard syntax, including sibling spans before/after password. The committed example declared `_` and an unused `_1`; reserved-name validation exposed it. Tests now describe the arbitrary-position behavior. Cost if wrong: example warning scope changes to all direct password children.
- Ruling: helper lowering retains existing `NH0009` mapping for runtime quotation errors, including `NQP0006` work exhaustion; direct matching reports `NQP0006`. No fallback occurs. Cost if wrong: consumers expecting raw NQP codes on lowering see the established NH wrapper instead.
- Task 5: fresh editor tests RED on missing discard hover; integration in progress.

- Tasks 4–5: integrated focused gate GREEN: 260/260 tests, including helper lowering, XML diagnostic consumer, parser ambiguity/rollback, C# and grammar hover/navigation/rename/completion, opaque comments, single-segment work bounds and deep nesting.
- Single-segment pruning regression: RED at 5,000 work steps on 300 middle items, GREEN after recognizing the uniquely determined length when no later variable segment exists. This preserves deterministic single-segment behavior and avoids quadratic retries.
- Incomplete-pattern completion: C# and grammar cases RED, then GREEN using pattern-mode expectation probes with lexical masking and full parsed-site checks when a complete quote is available.
- Actual VS Code/Rider visual interaction: pending; no extension installation or live native editor control was performed.

- Full Debug suite: 2,127/2,127 tests passed (35 seconds).
- Local performance probe (Debug, concurrent release build; indicative timings only): 10-item patterns consumed 70–72 steps, 100-item patterns 610–612, and 300-item patterns 1,810–1,812. Representative matches used about 8–157 KB per match and 0.008–0.706 ms. A 45-item adversarial failure stopped at the default 1,000,000-step limit in about 562 ms. No timing assertions or general complexity claim are made.
- Independent final reviewer dispatched against the uncommitted diff and approved spec.

- Independent review: no critical or important core matcher defects. One editor finding: incomplete opaque tokens containing `$_` could receive discard completion from an expectation-only fallback.
- Final: Ruling: treat this editor finding as a required spec correction because actual parsed-hole identity is an explicit contract. Reproduced both C# and grammar cases RED; fix requires an actual typed-hole parser probe as well as prefix progress. Cost if wrong: an extra internal completion-only callback, with no ordinary parsing behavior change.
- Initial `./build.sh`: Release build with zero warnings/errors, 2,127 Release tests, VS Code package, shared Rider tests, Rider plugin and carried-server smoke checks all passed. Final post-review build pending.

- Final review correction: both incomplete opaque-token completion regressions RED → GREEN; focused editor/parser gate 56/56 passed. No reviewer findings remain deferred.
- Final Release suite after the correction: 2,129/2,129 passed. Release build: zero warnings/errors. VS Code package and shared Rider tests passed; final Rider packaging in progress.

- Final `./build.sh` completed successfully: zero-warning Release build, 2,129 passing tests, VS Code VSIX, shared Rider tests, Rider ZIP and carried-server startup/shutdown smoke checks.
- Actual Release CLI `check examples/XmlPatterns/sample.xml`: exactly one `XML1001` warning at line 6, column 5, exit 0.
- Final diff whitespace check passed. All changes remain uncommitted on `codex/sequence-patterns-wildcards`; no push or installation performed.
- Remaining acceptance: actual VS Code/Rider visual interaction only. No deferred code findings.

## Release authorization

The user requested PR and release on 2026-10-21. This authorizes scoped commits, push, PR checks and merge, version synchronization, tag, and publication. Target version: 0.18.0 (new sequence-pattern capability and reserved pattern capture name `_`). Earlier no-commit planning constraints are superseded for this release. Live editor visual verification remains pending.
