# 245 — Nitrogen typed HIR execution

Status: Implemented; merged into Gravity master and present in standalone Nitrogen

Implement milestone 2 of [the Nitrogen roadmap](../docs/roadmap.md): bind pure host operations to exact semantic signatures and evaluate a finite numeric HIR slice with explicit symbol inputs and source-linked diagnostics. A dynamic Motion angle track is the primary proof; Policy clamp is a secondary constant-path check.

Branch: `codex/245-nitrogen-hir-execution` from `codex/244-nitrogen-module-contract` at `5daa8b16`. Issue 244 is not merged into `master`.

Spec: [2026-09-25-nitrogen-hir-execution-design.md](../docs/superpowers/specs/2026-09-25-nitrogen-hir-execution-design.md)

Plan: [2026-09-25-nitrogen-hir-execution.md](../docs/superpowers/plans/2026-09-25-nitrogen-hir-execution.md)

## Log

- 2026-09-25 — User chose the dynamic Motion track as the main proof and approved the typed registry/evaluator design in chat. Written spec prepared for review; implementation has not started.
- 2026-09-25 — User approved the written spec. A five-task test-first implementation plan is ready for review; implementation has not started.
- 2026-09-25 — User approved the plan and native execution. Added finite scalar/angle values, exact typed host registry, whole-tree HIR preflight, source-linked evaluator, and dynamic Motion plus Policy execution proofs. Each product step had a failing focused test before implementation; the two integration tests passed on the first run against the completed evaluator.
- 2026-09-25 — Full Nitrogen suite: 1,861 passed, 0 failed. Required fast gate passed every project except RagdollEditor.Tests (769 passed, 7 failed), matching the failure count documented for issues 243 and 244. The seven current failures are `NonFirstPhasePoseEditingTests.LaterPhaseSelectedThroughItsTransitionCanBeGizmoDraggedAndPinned` for phase indices 1 and 2; `Ragdoll10SquatSkillTests.SelectingDeepSquatPosesTheRealRigImmediately` and `PhaseAssetKeepsWholeBodyOwnershipAndMatchesDeepSquatPose`; `Ragdoll10FullBodyIkTests.StandingPelvisDescentBreaksStraightLegSingularityAndFlexesBothLegs`, `FloatingRootInspectorPreparationEnablesRootDrag`, and `MissingForearmMotorConfirmationAddsEveryPhaseAndUndoRemovesIt`. Earlier issue logs did not record the seven names, so the count is the documented comparison. This branch changes no RagdollEditor files. `git diff --check 5daa8b16` is clean. No predictive-control training, `artifacts/`, or velocity-profile files changed, so the extended gate was not triggered.

- 2026-09-25 — Whole-branch review found that a caller-supplied dictionary with a name-based symbol comparer could make a value from another parse satisfy a HIR reference. A regression test failed first; preflight now matches symbol keys by object identity, and evaluation snapshots inputs with reference identity before executing. The focused preflight/evaluator/integration tests passed (13/13), and the full Nitrogen suite passed (1,862/1,862). The dedicated review agent was unavailable due to its usage limit, so this was a direct self-review; no independent reviewer verdict is claimed.
- 2026-09-25 — Final fast gate after the review fix: all projects passed except RagdollEditor.Tests (769 passed, the same seven named failures observed before the fix). Nitrogen passed 1,862/1,862 within that gate.

Issue 245 depends on issue 244 at `5daa8b16`; it has not been merged or pushed.

- 2026-09-26 — Merged with the milestone stack into Gravity `master`; the implementation is present in the extracted standalone Nitrogen repository. Gravity-only integration remains in Gravity.
