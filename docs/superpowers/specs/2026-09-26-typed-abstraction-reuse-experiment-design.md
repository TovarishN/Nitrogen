# Typed abstraction reuse experiment — design

**Date:** 2026-09-26. **Status:** proposed experiment, not an implemented Nitrogen feature.

## Purpose

Test whether an agent can externalize a useful concept into an admitted, executable, typed Nitrogen module and later benefit from that module on a new problem. The primary observation is success on problem B in a fresh conversation using the artifact created during problem A. Grammar generation or a correct answer to A alone does not establish the hypothesis.

This is a narrow research experiment. Its first domain is one-dimensional support and balance, using a small `Core`, `Units`, `Geometry`, and `Optimization` environment. The environment gives the agent primitive pure operations, but no support-region, stability-margin, or stable-pose operation. Those concept names are evaluator vocabulary and must not appear in agent-visible task instructions or the initial module catalog.

## Current boundary and prerequisite

The present `ModulePackageLoader` admits declarative `.ngr` grammars, examples, and requested capability IDs. A trusted `HostModuleProfile` supplies the `ModuleDescriptor`, semantics, lowerers, bindings, and probe. `SemanticModule` can represent types and signatures, while the general `HirEvaluator` has a finite numeric value model. Thus current packages cannot themselves define the new typed semantics required by this experiment, and a new nominal aggregate value would also need an execution path. A passing grammar package must not be reported as passing this experiment.

Before running agents, add a **declarative derived-module artifact** that can state:

- module ID, imports, nominal types with fields and invariants, and exported operation signatures;
- pure expression bodies built only from literals, parameters, constructors, field access, conditionals, and imported or granted pure operations;
- examples with expected outputs and negative diagnostics;
- requested host capability IDs, source origins, and a content hash.

No authored C#, delegate, assembly reference, or arbitrary host binding belongs in this artifact. Type checking must establish exact argument and result types, import resolution, operation identity, and constructor invariants before admission. Lowering must retain both definition and call origins. Execution must evaluate the admitted expression body or its typed HIR expansion with a bounded, host-controlled value model. Resource limits and isolated execution need a separate design before automatic admission of untrusted agent output; the first run can use the existing reviewed-local trust boundary.

## Environment contract

The pilot uses `Units.Length` and `Units.Mass` as distinct types, `Geometry.Contact1D` and `Geometry.Load1D` as data values, and a finite list of each as inputs. The host provides pure primitives for extracting contact positions, calculating the mass-weighted load projection, minimum and maximum of lengths, length subtraction and comparison, and optimization over a finite set of candidates. Each primitive has an exact signature and deterministic behavior. The host does **not** provide an interval constructor, signed support margin, stability predicate, or placement selector that directly answers the tasks.

These are proposed contracts, not verified built-in Nitrogen exports. Freeze the complete capability catalog, type definitions, input bounds, numeric tolerance, and reference evaluator before revealing A. Reject NaN, infinities, zero or negative total mass, fewer than two distinct contacts, and malformed candidates using explicit diagnostics. The agent may create a different sound abstraction than the evaluator's example; acceptance is behavioral and typed, never based on a required name.

## Agent and artifact flow

1. Give agent A the frozen catalog, the language-definition interface, its capability grants, admission diagnostics, and task A. Permit a fixed number of edit/validate/repair attempts. Do not suggest a module design.
2. Record every candidate and diagnostic. Admit the final package only after composition, examples, type checking, HIR lowering, domain preflight, and pure execution pass. Retain its hash and an immutable snapshot. An invalid candidate cannot replace the last accepted one.
3. Start agent B in a fresh conversation with no A transcript or prose summary. Show the standard catalog and, in the artifact condition, the admitted module's exported interface and examples through ordinary Nitrogen discovery. Give B task B without an instruction to use the module.
4. Validate and execute B. Record whether its program imports and calls the A-created module, whether the actual call resolves to that package hash, and whether the hidden evaluator accepts the result. Merely mentioning the module in prose does not count as reuse.
5. Fork the same successful A run into matched B conditions with identical model, base environment, task B, tool access, context budget, attempt limit, and evaluation budget. In the **no-carry** condition, omit A's artifact. In the **text-only** condition, provide a prose description of A's abstraction of comparable size but no executable module. Randomize condition order and use several generated A/B pairs and model seeds; do not infer a general effect from one pair. If A fails to admit an artifact, record that failure and do not silently substitute a hand-authored module.

The agent's task prompts and an illustrative pilot pair are in [task-suite.md](../../experiments/typed-abstraction-reuse/task-suite.md). The machine-readable record contract is [run-record.schema.json](../../experiments/typed-abstraction-reuse/run-record.schema.json). Actual held-out variants and oracle outputs should be stored outside the agent-visible workspace during a run, then published with hashes after evaluation.

## Outcomes and scoring

The **primary measure** is paired B success: a correct, validated program within the same attempt and time budget, compared across artifact, no-carry, and text-only conditions. Report A admission rate separately, and report B comparisons both conditional on successful A admission and over all assigned A runs, counting failed admission as no artifact benefit. A successful artifact-condition run must also prove a resolved call into A's admitted module hash. If B succeeds by reimplementing the concept without importing the module, count solution success but not artifact reuse. A module that merely renames one primitive without composing behavior or enforcing a new type contract fails the abstraction audit even if it is admitted.

Secondary measures are B attempts, wall time, tokens, diagnostic count and repair count, A artifact admission rate, and the fraction of invalid attempts rejected before host execution. Record failures by stage: missing abstraction recognition, module definition, composition, program authoring, diagnostics repair, or B reuse. For each diagnostic repair, compare the before and after artifacts to confirm a targeted fix rather than a weakened type or invariant. Include seeded wrong-type and wrong-unit candidates to check that the compiler rejects them with useful origins; these are gate tests, not agent successes.

Run two pilot pairs to debug the harness, then exclude them from the confirmatory analysis. The proposed confirmatory minimum is twelve held-out A/B pairs across three model seeds each, with all three B conditions forked from each A run. Freeze the model version, prompts, task generator, success oracle, matching, budgets, stopping rule, and analysis before that phase. A starting budget is eight A attempts, six B attempts, twenty wall-clock minutes and 12,000 generated tokens for A, and fifteen minutes and 8,000 generated tokens for B; adjust these only during the pilot and record the final limits before confirmatory runs. Report paired counts and uncertainty intervals, plus raw traces. The claim is supported only if admitted artifacts are called on B and improve B outcomes against both controls across multiple held-out pairs. A single compelling trace is a demonstration, not evidence of a reliable advantage.

## Delivery boundary

This document and its linked artifacts specify the experiment. They do not implement the declarative semantic artifact, productize agent admission, or run the evaluation. Implementation should follow a separately reviewed plan. Existing `.ngr` admission, host capability grants, and Geometry execution remain the baseline to extend.
