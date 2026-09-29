# Nitrogen Semantic Catalog and Task Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Nitrogen-oriented concept reuse and evidence gathering available across projects through a private, independently validated semantic catalog.

**Architecture:** Keep the maintained skill in Nitrogen and install it for cross-project use. Store concept, capability, realization, and evidence records in a separate `TovarishN/Nitrogen.Concepts` repository with local validation and an index generated from source records. Solve and verify each task before proposing catalog updates in a pull request; keep ontology promotion a separate review action.

**Tech Stack:** Markdown skill, JSON Schema 2020-12, Python 3 standard library plus `jsonschema` 4.x for catalog validation, Git, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-09-29-nitrogen-semantic-catalog-skill-design.md`

## Global Constraints

- The catalog repository is private under `TovarishN`; proposed name: `Nitrogen.Concepts`.
- Preserve the current Nitrogen parser, typer, runtime, admission APIs, and host bindings.
- Keep the existing structured-lowering working tree untouched; implement from the isolated design branch or another clean worktree.
- Concept matches do not authorize execution. Revalidate optional Nitrogen artifacts with the receiving host and runtime before use.
- Record observations, candidates, and established concepts distinctly; routine tasks cannot promote or merge concepts.
- Publish task-gathered records through reviewable pull requests. If GitHub authentication is unavailable, retain a local draft and report publication pending.
- Do not copy secrets, private task transcripts, or customer data into catalog evidence.

## Review Focus

- Duplicate concept IDs across directories must fail validation; Task 1 tests this.
- A capability relation to a missing record must fail; Task 1 tests this.
- A candidate labeled established without independent evidence must fail; Task 1 tests this.
- A failed reuse attempt must remain visible in capability-first discovery; Task 2 tests this.
- An unavailable private catalog must not stop the user's main task or be described as successful publication; Task 3 exercises this.

## File map

`Nitrogen.Concepts`:

- `schemas/record.schema.json`: shape and allowed fields for all four record kinds.
- `tools/validate_catalog.py`: schema, unique-ID, graph, maturity, and index checks; `--write-index` regenerates the index.
- `tests/test_validate_catalog.py`: malformed and valid catalog cases.
- `requirements-dev.txt`: `jsonschema>=4,<5` for local validation and CI.
- `index.json`: generated capability-first lookup, checked into Git.
- `concepts/Concurrency.SingleFlight.json`, `capabilities/Concurrency.CoalesceInFlight.json`: small candidate examples, explicitly lacking execution claims.
- `README.md`, `.github/workflows/validate.yml`: record semantics, PR workflow, setup, and validation gate.

`Nitrogen`:

- `.agents/skills/nitrogen/SKILL.md`: cross-project apply/gather workflow and routing to reference detail.
- `.agents/skills/nitrogen/references/repository-development.md`: existing Nitrogen repository-specific maintenance guidance and concrete Geometry path.
- `.agents/skills/nitrogen/references/catalog.md`: catalog location, record meanings, reuse evidence, PR procedure, and unavailable-catalog behavior.
- `README.md`: install the maintained skill at `~/.codex/skills/nitrogen` and configure access to the private catalog.

### Task 1: Local catalog format and validator

**Files:** Create `Nitrogen.Concepts/schemas/record.schema.json`, `tools/validate_catalog.py`, `tests/test_validate_catalog.py`, `requirements-dev.txt`.

**Interfaces:** `validate(root: Path, base_root: Path | None = None) -> list[str]` returns deterministic errors; `build_index(root: Path) -> dict` derives sorted capability references and relevant evidence IDs. CLI `python3 tools/validate_catalog.py [--write-index] [--base PATH]` exits nonzero on invalid records or stale index; CI supplies a checkout of the PR base to check maturity transitions.

- [ ] **Step 1: Write failing validator tests.** Use `unittest` temporary directories to assert: a valid candidate concept and capability pass; duplicate IDs, dangling relation targets, unsupported `status` transitions, and an `established` concept with no independent accepted evidence fail. Add a test where stale `index.json` fails until regenerated. Use IDs `Concurrency.SingleFlight` and `Concurrency.CoalesceInFlight` in fixtures.

  ```python
  # Core assertion pattern in tests/test_validate_catalog.py.
  errors = validate(catalog_root)
  self.assertTrue(any("duplicate ID Concurrency.SingleFlight" in e for e in errors))
  self.assertTrue(any("missing relation target" in e for e in errors))
  ```
- [ ] **Step 2: Run the red test.** Run `python3 -m unittest discover -s tests -v` in `Nitrogen.Concepts`; expect import or assertion failures because the validator does not exist.
- [ ] **Step 3: Implement the format and validator.** Define schema-required `schemaVersion`, `kind`, `id`, `name`, and `definition`; concept `status` is `observation|candidate|established` with typed inputs/outputs, constraints, invariants, applicability limits, and examples where known. Capability contracts contain named inputs, result type, and `pure|host` effect. Relations have `kind`, `target`, and `rationale`. Realizations include source revision/hash, required host/runtime, provided capability IDs, and validation state. Evidence has stable event ID, problem class/domain, requested capability, match kind, outcome, verification, and reason. Use `Draft202012Validator` for shapes, then graph checks for duplicate IDs, relation targets, realization capability references, and evidence references. An established concept needs a reviewed contract, positive and negative examples, and independent accepted reuse or validation evidence; no fixed reuse-count threshold grants promotion. With `base_root`, reject maturity downgrades and skips from observation directly to established. Generate sorted `index.json` from capability requirements/provisions and evidence references.

  ```python
  from jsonschema import Draft202012Validator

  def schema_errors(record: dict, schema: dict) -> list[str]:
      validator = Draft202012Validator(schema)
      return sorted(error.message for error in validator.iter_errors(record))

  def maturity_rank(status: str) -> int:
      return {"observation": 0, "candidate": 1, "established": 2}[status]
  ```
- [ ] **Step 4: Run green tests and commit.** Run `python3 -m pip install -r requirements-dev.txt`, then `python3 -m unittest discover -s tests -v`. Commit only the catalog files for this task. If dependency installation is unavailable, use an existing compatible `jsonschema` installation or report the missing dependency; do not claim validation passed.

### Task 2: Catalog seed, discovery, and review workflow

**Files:** Create `Nitrogen.Concepts/concepts/Concurrency.SingleFlight.json`, `capabilities/Concurrency.CoalesceInFlight.json`, `README.md`, `.github/workflows/validate.yml`; generate `index.json`; extend `tests/test_validate_catalog.py`.

**Interfaces:** `index.json` maps capability IDs to concept IDs, realization IDs, and evidence IDs. The seed is a candidate semantic contract; it has no runnable realization or fabricated reuse evidence.

- [ ] **Step 1: Write failing discovery tests.** Assert `build_index` returns `Concurrency.SingleFlight` for `Concurrency.CoalesceInFlight`; add a failed reuse evidence fixture and assert its ID remains in that capability's evidence list. Assert a seed without a realization does not appear as executable.
- [ ] **Step 2: Run the red test.** Run `python3 -m unittest discover -s tests -v`; expect the discovery assertions to fail.
- [ ] **Step 3: Add the seed and workflow.** The candidate describes coalescing one in-flight computation per key, its input/output types, cancellation and failure-policy questions as applicability limits, and its relation to the capability. The README documents clone/setup, capability-first search in `index.json`, record maturity, evidence fields, local validation, PR submission, and the distinction between record reuse and executable module reuse. CI installs `requirements-dev.txt`, runs unit tests, runs `python3 tools/validate_catalog.py`, and validates changed maturity against a checkout of the pull request's base commit using `--base`.

  ```json
  {
    "schemaVersion": 1,
    "kind": "concept",
    "id": "Concurrency.SingleFlight",
    "name": "SingleFlight",
    "status": "candidate",
    "definition": "Concurrent requests for one key share one in-flight computation.",
    "provides": ["Concurrency.CoalesceInFlight"],
    "applicabilityLimits": ["Cancellation and failure propagation need an explicit policy."],
    "relations": []
  }
  ```
- [ ] **Step 4: Verify and commit.** Run `python3 tools/validate_catalog.py --write-index`, `python3 -m unittest discover -s tests -v`, and `python3 tools/validate_catalog.py`; inspect the generated index and commit the catalog changes. A fresh temporary clone of the local repo must find the candidate by capability without using the original task context.

### Task 3: Cross-project Nitrogen skill

**Files:** Modify `Nitrogen/.agents/skills/nitrogen/SKILL.md`; create `references/repository-development.md` and `references/catalog.md`; update `Nitrogen/README.md`.

**Interfaces:** The skill loads for Nitrogen-oriented task solving and ontology gathering. It uses `index.json` and the catalog record fields from Tasks 1–2; it never treats an index hit as a validated executable binding.

- [ ] **Step 1: Capture red skill scenarios.** Save a short scenario checklist in the catalog reference draft: exact capability match, failed match with a counterexample, and private catalog unavailable. For each, record what the current skill omits: cross-project discovery, evidence capture, or offline fallback. Keep the original skill text available for regression comparison.
- [ ] **Step 2: Rewrite the skill and references.** In `SKILL.md`, give a short apply → solve/validate → gather loop; load the catalog reference only when catalog access or evidence recording matters. Move the existing Nitrogen repository-specific API and Geometry guidance into `repository-development.md` without losing its diagnostics and execution boundaries. `catalog.md` gives the local checkout search, authenticated fetch, schema fields, duplicate check, evidence event preparation, reviewable PR, and local-draft fallback. Explicitly say ordinary task work completes before the catalog PR and cannot silently promote concepts.

  ```markdown
  1. Express the task as required capabilities and typed constraints.
  2. Search the catalog; inspect contracts and failure evidence.
  3. Solve and deterministically validate the smallest suitable composition.
  4. Record actual reuse outcomes; propose reusable observations in a catalog PR.
  ```
- [ ] **Step 3: Validate and exercise.** Run `python3 /Users/dmitrikamenetski/.codex/skills/.system/skill-creator/scripts/quick_validate.py .agents/skills/nitrogen`. Walk the three scenarios with the actual catalog files and check the skill's instructions lead to a typed match or explained rejection, correct evidence status, and completion of the main task when offline. Run `git diff --check`; commit only the skill and README files.

### Task 4: Install, publish, and fresh-session acceptance

**Files:** No new source files; install the maintained skill at `~/.codex/skills/nitrogen`. Create the separate private GitHub repository and its initial commit from the tested local catalog.

**Interfaces:** A fresh agent in another project can load the installed skill, clone/read `TovarishN/Nitrogen.Concepts`, discover a capability, and propose evidence in a separate PR.

- [ ] **Step 1: Check current state.** Verify the Nitrogen implementation worktree is clean and catalog tests/CI files are committed. Run `gh auth status`; if invalid, finish local acceptance and report remote rollout blocked until the user refreshes GitHub authentication. Do not retry with copied tokens.
- [ ] **Step 2: Install and verify the skill.** If `~/.codex/skills/nitrogen` is absent, symlink it to the maintained `.agents/skills/nitrogen` directory; if occupied, inspect and preserve its contents before changing it. Run `quick_validate.py` on the installed path and confirm its frontmatter appears from an unrelated project directory.
- [ ] **Step 3: Publish when authenticated.** Confirm `TovarishN/Nitrogen.Concepts` does not already exist, then create it private from the tested catalog, push the initial commit, and verify GitHub reports it private. Push the Nitrogen skill branch separately; do not merge the skill branch as part of publishing the catalog.
- [ ] **Step 4: Prove fresh-session reuse.** In a clean temporary directory, clone the private catalog using normal GitHub credentials, search `index.json` for `Concurrency.CoalesceInFlight`, inspect the candidate and its absence of executable realization, and produce a local sample reuse attempt with a verified outcome. Run catalog validation on the proposed evidence branch and open a reviewable PR if authentication works. Confirm the PR remains unmerged and the source task's work is unchanged. If publication is blocked, preserve the local draft and report exactly what remains pending.

## Final verification

Run the catalog unit tests and validator, `quick_validate.py` on both source and installed skill, `git diff --check` in both repositories, and the fresh-checkout discovery exercise. Report local validation, GitHub publication, and any executable Nitrogen validation separately. Do not claim cross-machine executable reuse from a catalog lookup alone.
