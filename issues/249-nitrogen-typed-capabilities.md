# 249 — Nitrogen typed capability requests

Status: Implemented; merged into Gravity master and present in standalone Nitrogen

Implement [roadmap milestone 6](../docs/roadmap.md): a generated module requests operation IDs from a host-owned typed capability set. The first proof requests the pure `Geometry.BoxMesh` operation and retains the milestone 5 admission gates.

Branch: `codex/249-nitrogen-typed-capabilities` from issue 248 at `92fc5c77`. Issues 244–248 remain unmerged into `master`.

Design: [2026-09-26-nitrogen-typed-capabilities-design.md](../docs/superpowers/specs/2026-09-26-nitrogen-typed-capabilities-design.md)

Plan: [2026-09-26-nitrogen-typed-capabilities.md](../docs/superpowers/plans/2026-09-26-nitrogen-typed-capabilities.md)

## Log

- 2026-09-26 — User asked to continue the roadmap, selected `Geometry.BoxMesh` as the first pure capability proof, and approved the in-chat design. The written design is prepared for review; implementation has not started.
- 2026-09-26 — User approved the written design. A four-task test-first implementation plan is committed for review; implementation has not started.
- 2026-09-26 — User approved the plan. Packages now declare zero to sixteen unique capability IDs in the bounded manifest; the sorted IDs enter the content hash. A host-owned immutable grant set owns exact signatures, bindings, and pure effect classification. Candidate admission requires requested IDs to match descriptor and dependency requirements, selects only those bindings, and returns `NA0006` before examples for unknown, missing, unused, or mismatched requests.
- 2026-09-26 — The generated box proof exposes exactly `Geometry.BoxMesh` and still matches the editor's 24-vertex, 36-index mesh. An unknown replacement request makes no additional probe or host call and leaves the accepted lease usable. Verification: Nitrogen 1,953/1,953; `dotnet test Gravity.slnx --filter "Gate!=Extended"` passed in every reported project except the same seven RagdollEditor tests recorded in issue 248 (769 passed, seven failed there). No extended-gate trigger paths changed. This is a trusted local grant check for human-reviewed packages; automatic admission and non-pure permissions remain outside this milestone.
- 2026-09-26 — Fresh whole-branch review found no critical or important production defect. The box recovery test now lowers and executes through the retained lease, and the granted-but-unused request test reaches its distinct rejection path; both passed on first run because the implementation already supported them. The final Nitrogen suite again passed 1,953/1,953 and the fast gate retained exactly the same seven RagdollEditor failures. A minor review finding remains: the `NM0005` conflict diagnostic for duplicate requirements names the operation ID instead of the contributing module IDs.

- 2026-09-26 — Merged with the milestone stack into Gravity `master`; the implementation is present in the extracted standalone Nitrogen repository. Gravity-only integration remains in Gravity.
