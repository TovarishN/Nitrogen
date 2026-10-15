---
name: nitrogen
description: Use when solving tasks through typed domain capabilities, designing Nitrogen languages or authoring DSL programs and modules, or gathering reusable concepts and ontology evidence across projects.
---

# Nitrogen-oriented task work

Treat a task as a request for capabilities under typed constraints. Search existing project contracts before inventing one, solve the user's task with the smallest suitable composition, and record what the outcome taught about reuse. A catalog entry is semantic knowledge; it is not automatically executable code.

## Apply → solve → gather

1. **Frame the need.** Name the required capabilities, inputs, outputs, effects, invariants, units, reference frames, and domain identities that matter. Keep syntax secondary to meaning. Use the task's actual host and tools; this skill does not require every project to use a Nitrogen DSL.
2. **Discover and compose.** Inspect existing project modules and abstractions first. When the task needs cross-project reuse, concept discovery, or catalog evidence, find the private semantic catalog using [the catalog reference](references/catalog.md). Search capability-first, then inspect candidate concepts, relations, realizations, examples, and both success and failure evidence. A local grammar or editor fix can proceed using its existing contracts without catalog discovery. Record whether the match is exact, a specialization, adaptation, or composition, and identify any missing capability. A name match alone is insufficient: compare complete contracts and host availability.
3. **Construct the smallest solution.** When designing or extending a Nitrogen language, read [language-design.md](references/language-design.md). When Nitrogen is applicable, inspect the real grammar, imports, exported types and operation signatures, and required host bindings. Compose the smallest language and typed program for the task. Preserve units, frames, nominal domain types, and entity identities; convert only through declared operations. When the project uses another implementation language, retain those semantic distinctions in its native types and checks.
4. **Validate, repair, then execute if authorized.** Compose → parse → bind → type/value check → lower to typed HIR where supported → bind exact host operations → run applicable preflight or domain checks. Diagnose the earliest underlying error from its code and source span; make one targeted repair and rerun all applicable stages. Never weaken a contract just to silence a diagnostic. Stop after bounded attempts and report unresolved diagnostics. Invoke a host handler only after deterministic checks pass and the task authorizes its effects; revalidate after source, module, input, or binding changes.
5. **Finish the task and gather evidence.** Verify the user's requested result first. Record a reuse attempt only if a catalog concept or realization was actually tried; capture match, adaptation, outcome, verification, and why it worked or failed. Harvest a reusable observation, candidate concept, missing capability, counterexample, or relation only when supported by the task. Check duplicates and applicability limits. Prepare a catalog pull request when authorized and access works; otherwise keep a local draft and report that publication is pending. Routine tasks do not promote candidates or redesign the ontology.

For an abstraction that recurs, propose a typed Nitrogen module only if current APIs and a host profile can express and validate it. Define imports, exported contracts, examples and counterexamples, requested host capabilities, and a content hash. A receiving host must revalidate or re-admit it before execution. Never treat catalog publication as automatic admission of agent-authored code.

## Small example

A task needs concurrent requests for one key to share pending work. Search for `Concurrency.CoalesceInFlight`; the catalog's `Concurrency.SingleFlight` candidate may explain the capability, but its cancellation policy is unresolved and it has no executable realization. Solve and test the task's actual cancellation behavior. Record exact reuse, adaptation, or rejection based on that result. A failed match is useful evidence; it does not make the candidate established.

## References

- Read [catalog.md](references/catalog.md) when locating the catalog, interpreting records, or proposing evidence and a pull request.
- Read [language-design.md](references/language-design.md) when choosing language constructs, binding rules, composition boundaries, or validation examples.
- Read [repository-development.md](references/repository-development.md) when authoring Nitrogen programs or changing Nitrogen itself. It documents current APIs, diagnostics, the Geometry execution path, and repository checks.
