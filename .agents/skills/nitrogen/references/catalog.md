# Semantic catalog use

The private repository is `TovarishN/Nitrogen.Concepts`. Its `README.md` and `schemas/record.schema.json` define the current record contract; inspect those files instead of guessing fields. The catalog separates concepts (meaning), capabilities (what can be achieved), realizations (concrete implementations), and evidence (observed reuse or failure). Only a realization with compatible host/runtime requirements and successful local validation can support execution.

## Locate and search

1. If `NITROGEN_CONCEPT_CATALOG` is set, use that absolute path after checking it contains `index.json` and the catalog directories. Otherwise look for an existing `Nitrogen.Concepts` sibling checkout, including `/Users/dmitrikamenetski/work/Nitrogen.Concepts` on this machine.
2. If no checkout exists and authenticated GitHub access is available, clone `git@github.com:TovarishN/Nitrogen.Concepts.git` to a suitable local workspace. Do not place the clone inside the task's product repository. If access fails, continue the main task using locally available knowledge and state that catalog discovery was unavailable. Do not call an inaccessible catalog empty.
3. Inspect `index.json` by required capability ID. The `concepts` and `realizations` lists provide it; `requiredBy` names dependents, not providers. Open linked concept and evidence records, including failed attempts and applicability limits. Inspect any realization's source revision, provided capability, host requirements, runtime requirement, and validation state. Verify its exact contract against the current task and host. A candidate without a realization can inform a design, but it cannot be imported as runnable code.
4. If the index is stale or a record is malformed, run the catalog validator and report its diagnostics. Do not silently repair, promote, or execute an invalid catalog entry while solving an unrelated task.

Search for capabilities, not only object names. Compose multiple concepts only when their typed inputs, outputs, units, frames, effects, and invariants align. Note missing conversions or host bindings explicitly. An apparent semantic match is a hypothesis until the task's deterministic checks and outcome support it.

## Record an outcome

Separate a problem observation from a reusable concept hypothesis. An observation can describe one verified task without claiming broad generality. A candidate concept needs a definition, typed inputs/outputs where known, constraints, applicability limits, examples, and relations to existing IDs. An established concept requires a separate review, positive and negative examples, and independent evidence; routine tasks do not promote it.

For an actual reuse attempt, add a new append-only evidence event with `EV-YYYYMMDD-slug` ID, problem class and domain, requested capability, optional matched subject ID, match kind (`exact`, `specialization`, `adaptation`, `composition`, or `none`), outcome (`accepted`, `rejected`, `modified`, `failed`, or `observed`), verification status and method, execution result if applicable, adaptation and reason, and a minimal source reference. A failed or rejected attempt remains visible even after later success. Do not fabricate test results, execution, independent reuse, or numerical scores.

Review confidentiality before writing records. Generalize private task details; omit secrets, customer data, internal transcripts, and source links that reveal more than the catalog needs. Keep the link to a task artifact only when that artifact is safe for this repository's readers.

## Propose changes

Complete and verify the user's task, search for duplicate IDs and relations, then add the smallest records or evidence event on a catalog branch. Run the catalog's unit tests, `tools/validate_catalog.py --write-index`, and `tools/validate_catalog.py --base PATH` against the base checkout before proposing a pull request. Describe the observed outcome, applicability limits, duplicate analysis, and validation in the PR. Leave it for review; do not merge as part of routine gathering. If GitHub or permission is unavailable, retain a local draft and report its path and unpublished status.

Nitrogen packages need separate local admission. A catalog record or pull request cannot turn untrusted agent-authored text into executable code. Recheck the artifact, runtime, host capability grants, and examples in the receiving environment before execution.
