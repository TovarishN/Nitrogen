# Nitrogen semantic catalog and cross-project task skill — design

**Date:** 2026-09-29. **Status:** approved conversational design, pending review of this written spec.

## Purpose and boundary

Give an agent a Nitrogen-oriented way to complete tasks across projects: identify required capabilities, discover existing semantic contracts, compose the smallest suitable solution, validate it deterministically, and collect reusable knowledge from the outcome. Store that knowledge in a separate private GitHub repository, proposed as `TovarishN/Nitrogen.Concepts`, so a fresh task can discover it without the original conversation.

The semantic catalog is an empirical knowledge base, not a claim that every entry is a runnable Nitrogen module. The first release updates the existing `.agents/skills/nitrogen/SKILL.md` as the maintained skill source and makes it available to the agent across projects. It does not change Nitrogen's parser, typer, runtime, admission APIs, or existing host bindings. The current skill's Nitrogen repository maintenance guidance moves to a supporting reference so ordinary tasks load only the task workflow.

## Architecture

The catalog repository owns versioned records, evidence, schema, and validation tooling. The Nitrogen repository owns the skill instructions. A task workspace owns its code and results. Install the maintained skill in Codex's personal skills directory for cross-project discovery while retaining its source in Nitrogen. The skill locates a local checkout of the private catalog or retrieves it through the user's normal GitHub access; it reads the catalog before designing an abstraction and proposes catalog changes through a separate pull request after solving the task. Catalog access failure must not prevent the task from being completed with the capabilities available locally.

The catalog distinguishes:

- **Concept:** what a thing means, its inputs and outputs, constraints, invariants, effects, and applicability limits.
- **Capability:** what a task needs or a realization can provide, with typed input/output and effect contracts where expressible. Capability is the primary task-to-catalog search key.
- **Relation:** typed edges such as `specializes`, `requires`, `provides`, `composes-with`, `conflicts-with`, and `derived-from`. Every edge names an existing ID and has a short rationale.
- **Realization:** a concrete Nitrogen module/package, implementation, API, or composition satisfying a declared capability. A realization has a repository/revision or content hash, required host/runtime versions and capabilities, and a validation status. A prose concept is not automatically executable.
- **Evidence:** append-only observations and reuse attempts. Each attempt records the problem class/domain, requested capability, matched concept or realization, match kind (`exact`, `specialization`, `adaptation`, `composition`), outcome (`accepted`, `rejected`, `modified`, `failed`), verification performed, execution result when authorized, adaptation made, and a concise reason. Evidence links to task artifacts without copying private transcripts, secrets, or customer data.

Records have stable qualified IDs, a schema version, a human-readable definition, and lineage to superseded IDs. They use a machine-readable format with a documented JSON Schema and a short README. The repository contains `concepts/`, `capabilities/`, `realizations/`, and `evidence/` directories; a checked catalog index supports capability-first discovery and is regenerated and checked from the records. Schema and graph validation reject duplicate IDs, dangling relations, invalid state transitions, and unsupported references. Initial records can be sparse where a task has only an observation; they must not invent signatures or validation evidence.

## Maturity and review

An **observation** records what happened in one task. A **candidate concept** states a reusable hypothesis and its limits. An **established concept** has a reviewed contract, relevant positive and negative examples, and evidence from independent use or validation. Promotion is a deliberate catalog review, not an automatic count threshold. Failed reuse can narrow a concept's applicability or motivate a new capability; it does not disappear from the record. Keep evidence dimensions separate rather than computing one popularity score.

The routine task skill may add observations, candidate concepts, missing capabilities, and reuse evidence to a pull request. It does not promote entries, merge concepts, or redesign the ontology during an ordinary task. A later explicit refinement operation can examine accumulated evidence, propose generalizations or splits, and request review. Only validated, reviewed changes enter the default catalog branch. The pull request links the task evidence, explains duplicates and relations considered, and shows catalog validation results. Publishing a PR is a separate action from executing task code; it follows the user's repository and authorization boundaries.

## Agent workflow

1. Translate the task into required capabilities, types, units, reference frames, domain entities, constraints, and authorized effects. Search the catalog by capabilities and relations before inventing a new abstraction; inspect provenance and evidence, including failures.
2. Select the smallest matching concept or composition. Distinguish exact reuse from adaptation. Inspect the concrete realization's contracts and the current host's available operations. A catalog match is a proposal, not proof of compatibility.
3. Solve the task using existing modules where possible. If Nitrogen is applicable, construct the smallest typed program or reusable module; otherwise use the project's native implementation while retaining the semantic contract. Syntax is secondary to meaning. Preserve units, frames, and nominal domain types; use explicit conversions.
4. Run the task's deterministic checks before any authorized execution: parsing, binding, type/value checks, HIR lowering when supported, host binding, and applicable preflight/domain checks. Use source-located diagnostics to repair the earliest cause, then revalidate. Do not weaken a type or invariant to silence a diagnostic. Host effects still require the task's normal authorization.
5. Complete and verify the user's task first. Record whether catalog reuse was actually attempted, accepted, adapted, rejected, or failed, and what evidence supports that outcome. Do not claim reuse merely because a concept was mentioned.
6. Harvest only potentially reusable knowledge: observations, candidate concepts, capabilities that were missing, useful relations, counterexamples, and realizations with verified contracts. Check for duplicates. Propose catalog changes in a reviewable PR. If GitHub is unavailable, keep a local draft and report that publication remains pending; never label it published.

For a recurring missing abstraction, propose a typed Nitrogen module only when the current Nitrogen APIs and a host profile can represent and validate it. Include imports, exported signatures, examples and counterexamples, required host capabilities, and a content hash. Revalidate it in the receiving environment before execution. Current local admission is a reviewed trust boundary; the skill must not turn agent-authored text into automatically admitted executable code.

## Example

A task needs deduplicated concurrent loading. The catalog search finds a general cache concept but its evidence says it does not coalesce in-flight work. The agent solves the task using a typed `SingleFlight<K,V>` candidate contract, validates concurrent callers and failure behavior, and records an observation plus a failed cache reuse attempt. A later independent task may reuse or adapt the candidate. Only after review of its contract and independent evidence could catalog maintainers establish it. The name is illustrative; it is not a built-in Nitrogen type or operation.

## Verification and acceptance

- Validate the skill frontmatter and instructions with the skill validator. Exercise a task with an exact match, a failed match, and no catalog access; check that it solves the task and records only supported evidence.
- Validate all catalog records against the schema and graph rules. A malformed ID, dangling edge, invented realization, or unsupported promotion fails CI. A PR shows the validation result.
- In a fresh workspace with no earlier task transcript, obtain the private catalog, discover a capability, inspect its concept and evidence, and either use a compatible realization after local validation or reject it with a recorded reason. This proves cross-session discovery; executable cross-machine reuse is claimed only when the relevant artifact is re-admitted with compatible runtime and host capabilities.
- Keep the Nitrogen repository's existing build and tests as regression checks only if implementation changes affect them. The first skill/catalog release does not require a runtime change.

## Delivery

Implement the skill update in Nitrogen, a private `TovarishN/Nitrogen.Concepts` repository with schema, index, validator, documented review workflow, and a small illustrative seed set, plus a documented installation of the maintained skill at `~/.codex/skills/nitrogen`. Keep this design work isolated from the current structured-lowering checkout. GitHub repository creation and PR publication depend on working `TovarishN` authentication; current `gh auth status` reports an invalid token.
