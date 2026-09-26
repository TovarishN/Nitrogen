# 246 — Nitrogen renderable box mesh proof

Status: Implemented; merged into Gravity master and present in standalone Nitrogen

Implement milestone 3 of [the Nitrogen roadmap](../docs/roadmap.md): a small independent `.geom` module that parses, checks, lowers, and executes `box 1 2 3;` to a renderable mesh through a bound adapter to the existing RagdollEditor box generator. Inspect the same typed root in the language service.

Branch: `codex/246-nitrogen-geometry-mesh` from issue 245 at `6fad7ef6`. Issues 244 and 245 are not merged into `master`.

Spec: [2026-09-25-nitrogen-geometry-mesh-design.md](../docs/superpowers/specs/2026-09-25-nitrogen-geometry-mesh-design.md)

Plan: [2026-09-25-nitrogen-geometry-mesh.md](../docs/superpowers/plans/2026-09-25-nitrogen-geometry-mesh.md)

## Log

- 2026-09-25 — User selected a renderable box mesh as the first geometry result and approved the narrow `Nitrogen.Geometry` module plus editor-side adapter design. Written spec prepared for review; implementation has not started.
- 2026-09-25 — User approved the written spec. A five-task test-first implementation plan is ready for review; implementation has not started.
- 2026-09-25 — User approved the plan and native execution. Implemented the generated `.geom` grammar and semantic checks, source-linked box HIR, immutable mesh value, narrow geometry executor, and RagdollEditor host adapter. `box 1 2 3;` exactly matches the existing generator's 24 vertices and 36 indices; the language service inspects the same root and invalidates it after an edit. Invalid dimensions do not invoke a host.
- 2026-09-25 — Each implementation task began with a failing focused test and ended with passing tests. Direct review found that C# delegate variance admitted `Func<object, GeometryMesh>` despite the exact handler contract; a regression test failed first and the executor now checks the concrete delegate type. Focused executor/integration tests passed 9/9 after that fix. Full Nitrogen suite passed 1,886/1,886.
- 2026-09-25 — Final Gravity fast gate passed Engine, Game3D, JpcSharp, MotionDSL, Nitrogen, Physics2D, and Physics3D tests. RagdollEditor.Tests passed 769 and failed the same seven named cases documented in issue 245: two `NonFirstPhasePoseEditingTests.LaterPhaseSelectedThroughItsTransitionCanBeGizmoDraggedAndPinned` cases (phase 1 and 2), two `Ragdoll10SquatSkillTests` cases, and three `Ragdoll10FullBodyIkTests` cases. No predictive-control training, `artifacts/`, or velocity-profile files changed, so the extended gate was not triggered.
- 2026-09-25 — A fresh whole-branch reviewer found no concrete findings against the approved spec in `6fad7ef6..e31c7390`. The reviewer did not rerun tests; the test evidence above comes from the implementation run. `git diff --check 6fad7ef6` is clean.
- 2026-09-26 — Added an exact malformed-token parser diagnostic assertion for `box 1 nope 3;`; the focused module filter passed 6/6. The generator allows one hover property per language, so numeric literals expose `Core.Scalar` as hover metadata while the box has a regular `Geometry.Mesh` property and is typed through HIR inspection.

The branch includes unmerged issues 244 and 245. It has not been merged or pushed.

- 2026-09-26 — Merged with the milestone stack into Gravity `master`; the implementation is present in the extracted standalone Nitrogen repository. Gravity-only integration remains in Gravity.
