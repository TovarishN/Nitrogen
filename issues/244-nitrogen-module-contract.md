# 244 — Nitrogen module contract

Status: Implemented; merged into Gravity master and present in standalone Nitrogen

Implement milestone 1 of [the Nitrogen roadmap](../docs/roadmap.md): a C# registered descriptor that composes syntax, semantics, start rules, and required host operation bindings, with deterministic composition diagnostics. HIR execution belongs to milestone 2.

Branch: `codex/244-nitrogen-module-contract` from `master`.

Spec: [2026-09-25-nitrogen-module-contract-design.md](../docs/superpowers/specs/2026-09-25-nitrogen-module-contract-design.md)

Plan: [2026-09-25-nitrogen-module-contract.md](../docs/superpowers/plans/2026-09-25-nitrogen-module-contract.md)

## Log

- 2026-09-25 — User selected roadmap milestone 1 and approved the descriptor design in chat. Written design prepared for review; implementation has not started.
- 2026-09-25 — User approved the written spec. A four-task test-first implementation plan is ready for review; implementation has not started.
- 2026-09-25 — User chose native execution. Added immutable module descriptors, composition diagnostics, exact host binding validation, and a Base/Uses fixture that parses, binds, and lowers with a source origin. Focused tests were observed failing before each implementation step and passing afterward.
- 2026-09-25 — Full Nitrogen suite: 1,840 passed, 0 failed. Required `dotnet test Gravity.slnx --filter "Gate!=Extended"` passed in all projects except RagdollEditor.Tests (769 passed, 7 failed). The seven failing test names match the baseline failures documented in issue 243; the new code is confined to Nitrogen. No predictive-control training, `artifacts/`, or velocity-profile files changed, so the extended gate was not triggered. `git diff --check` reported no whitespace errors. Whole-branch review remains.
- 2026-09-25 — Whole-branch review found that an invalid start rule hid an independent missing-host diagnostic and that substring matching could falsely attribute a syntax conflict to an unrelated module. Both cases failed as new regression tests before the fixes and passed afterward. Final Nitrogen suite: 1,842 passed, 0 failed. The repeated fast gate passed every project except RagdollEditor.Tests (769 passed, the same seven named failures as the earlier run). No merge or push has been performed.

- 2026-09-26 — Merged with the milestone stack into Gravity `master`; the implementation is present in the extracted standalone Nitrogen repository. Gravity-only integration remains in Gravity.
