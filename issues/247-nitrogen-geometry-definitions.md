# 247 — Nitrogen reusable geometry definitions

Status: Implemented; merged into Gravity master and present in standalone Nitrogen

Implement milestone 4 of [the Nitrogen roadmap](../docs/roadmap.md): three typed scalar parameters on a named box definition, cross-file calls through `Project`, HIR expansion with definition and call origins, and rejection of arity/type errors and cycles. Preserve the direct box sample and existing geometry executor/adapter.

Branch: `codex/247-nitrogen-geometry-definitions` from issue 246 at `7a23fa58`. Issues 244–246 are not merged into `master`.

Spec: [2026-09-26-nitrogen-geometry-definitions-design.md](../docs/superpowers/specs/2026-09-26-nitrogen-geometry-definitions-design.md)

Plan: [2026-09-26-nitrogen-geometry-definitions.md](../docs/superpowers/plans/2026-09-26-nitrogen-geometry-definitions.md)

## Log

- 2026-09-26 — User requested the next milestone and approved a three-scalar box definition called from another `.geom` file. Chose typed HIR expansion at lowering time after considering runtime calls and text substitution. Written design prepared for review; implementation has not started.
- 2026-09-26 — User approved the written spec. A five-task test-first implementation plan is ready for review; implementation has not started.
- 2026-09-26 — User approved the plan and native execution. Added cross-file `def`/`make` binding, scalar parameter checks, typed HIR expansion, forwarding by bound symbol identity, source origins, and cycle diagnostics. Direct `box` lowering and the existing mesh host remain supported. The integration tests prove two renderable calls, navigation, inspection, and invalidation after changing or closing a definition. A regression test also prevents an invalid sibling definition from blocking a valid one.
- 2026-09-26 — Whole-branch review found no actionable issues. A suspected nested missing-definition diagnostic gap was checked with a regression test; the existing expander correctly reports `GD0002` at the nested name. Final full Nitrogen suite passed 1,912/1,912. The Gravity fast gate passed every project except RagdollEditor.Tests (769 passed, the same seven named failures recorded in issues 245–246). No predictive-control training, `artifacts/`, or velocity-profile files changed, so the extended gate was not triggered.

Issue 247 depends on issue 246 at `7a23fa58`; it has not been merged or pushed.

- 2026-09-26 — Merged with the milestone stack into Gravity `master`; the implementation is present in the extracted standalone Nitrogen repository. Gravity-only integration remains in Gravity.
