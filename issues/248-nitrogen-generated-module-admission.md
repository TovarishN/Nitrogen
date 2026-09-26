# 248 — Nitrogen generated-module admission

Status: Implemented; merged into Gravity master and present in standalone Nitrogen

Implement milestone 5 of [the Nitrogen roadmap](../docs/roadmap.md): package a generated syntax module with examples and expected diagnostics, validate it against a host-owned semantic descriptor and operation bindings, then activate it only when every gate passes. The first slice accepts reviewed local declarative `.ngr` only.

Branch: `codex/248-nitrogen-generated-module-admission` from issue 247 at `ca711f72`. Issues 244–247 remain unmerged into `master`.

Spec: [2026-09-26-nitrogen-generated-module-admission-design.md](../docs/superpowers/specs/2026-09-26-nitrogen-generated-module-admission-design.md)

Plan: [2026-09-26-nitrogen-generated-module-admission.md](../docs/superpowers/plans/2026-09-26-nitrogen-generated-module-admission.md)

## Log

- 2026-09-26 — User asked to continue the roadmap and chose declarative `.ngr` with a host-owned descriptor for milestone 5. Approved a trusted local admission gate with human review before agent output is admitted automatically. Written design prepared for review; no product implementation has started.
- 2026-09-26 — User said to continue after the committed design was presented for review. A five-task test-first implementation plan is written for review; no product implementation has started.
- 2026-09-26 — User approved the implementation plan. The reviewed-local package loader enforces a bounded manifest and `.ngr` files, records a content hash, and rejects malformed paths and symbolic links. The declarative policy rejects grammar C# before compilation. Candidate composition uses only a registered host profile, including exact semantic and operation binding checks.
- 2026-09-26 — Example diagnostics are compared as code multisets. Positive examples lower to HIR and pass through the trusted profile's execution probe; negative examples do not call the probe. Publication is atomic, and retired snapshots unload after the last lease. The generated box proof matches the editor's 24-vertex, 36-index mesh exactly; an invalid replacement leaves the previous module usable.
- 2026-09-26 — Verification: `Nitrogen.Tests` 1,941 passed; `dotnet test Gravity.slnx --filter "Gate!=Extended"` passed in every reported project except the same seven RagdollEditor tests documented in issue 247 (769 passed, seven failed there). No predictive-control training, `artifacts/`, or velocity-profile files changed, so the extended gate was not triggered. This is a trusted local gate for human-reviewed packages; it is not an untrusted agent code sandbox or automatic admission service.
- 2026-09-26 — Whole-branch review found that constructed packages, negative-only examples, and dependency start rules could bypass the intended proof. The package constructor and setters are now internal, package collections are copied, admission requires a successful candidate-module example, and examples cannot select dependency starts. Failed results retain the package hash. Focused admission tests passed 32/32; the full Nitrogen suite passed 1,944/1,944. The repository fast gate was rerun after these fixes.

- 2026-09-26 — Merged with the milestone stack into Gravity `master`; the implementation is present in the extracted standalone Nitrogen repository. Gravity-only integration remains in Gravity.
