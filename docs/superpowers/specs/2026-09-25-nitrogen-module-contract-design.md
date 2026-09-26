# Nitrogen module contract — design

**Issue:** 244. **Date:** 2026-09-25. **Status:** draft for written review.

## Purpose and acceptance

The first milestone in [the Nitrogen roadmap](../../roadmap.md) is a C# registered module that can be composed and validated as one unit. A module author should be able to supply syntax, semantic exports and lowerers, named start rules, and required host operations together. Composition should fail with actionable diagnostics before parsing when a dependency or binding is absent or incompatible. Existing Motion and Policy callers must keep working through their current `LanguageBuilder` APIs.

Acceptance is a separately authored fixture module whose descriptor composes with a semantic-only dependency, exposes a working start rule, parses and lowers one sample, and is rejected predictably for each contract error below. This milestone validates host binding but does not execute HIR. Execution and value/error propagation are milestone 2.

## Existing boundaries

`LanguageBuilder` already combines `SyntaxModule` instances and `SemanticModule` instances. `SemanticCatalog.Compose` validates semantic imports, type and operation identity, and lowerer collisions (`NC0001`–`NC0005`). `ExtensionRegistry.Freeze` validates missing extension owners and duplicate alternative names; input-dependent ambiguity remains a parser diagnostic. `SyntaxModule.GetRule` resolves a start rule by name. The language service separately maps file extensions to `Rule` instances. These facilities remain the implementation behind the new contract.

The new API is opt-in. It does not change `.ngr` syntax, the generator, Motion/Policy compiler paths, or existing `LanguageBuilder.Build` and `TryBuild` behavior.

## Descriptor and composition API

An immutable `ModuleDescriptor` has a stable, nonempty module ID, an optional `SyntaxModule`, an optional `SemanticModule`, named start-rule references, and required `OperationSignature`s. At least one of syntax or semantics is required. Its constructor copies supplied collections. A semantic-only descriptor such as Units has no start rules. A descriptor's named start rules resolve through its own syntax module's `GetRule` during composition; names are kept alongside resolved `Rule`s in the result. File extensions remain a language-service choice, because the same rule may serve different extensions in different hosts.

`ModuleComposer.TryCompose(descriptors, hostBindings, out result, out diagnostics)` is the opt-in entry point. It validates descriptor IDs and start rules, adds their syntax and semantic parts to a `LanguageBuilder`, then exposes the built `Language`, resolved start rules, and the validated host bindings in an immutable result. A throwing `Compose` counterpart returns the same structured diagnostics in an exception. The legacy builder is not made dependent on host registrations.

Each `HostOperationBinding` pairs one exact `OperationSignature` with a non-null delegate. The delegate is stored and never invoked by milestone 1; milestone 2 defines argument conversion, invocation, result validation, and runtime error diagnostics. Required signatures must be exported by the composed semantic catalog and match both that export and a supplied host binding structurally, including ordered inputs and result. Duplicate binding IDs with incompatible signatures, a missing binding, and a required ID absent from semantic exports fail composition. Equivalent requirements from multiple descriptors coalesce. Unused host bindings are allowed.

## Diagnostics and failure behavior

The composer returns all contract diagnostics it can safely collect in deterministic ID order. Diagnostics carry a stable code, involved module IDs, and a clear message. They cover duplicate descriptor IDs, start rules absent from the descriptor's syntax module, absent semantic exports for requirements, missing host bindings, and signature mismatches. Existing semantic diagnostics retain their `NC` codes and module provenance. Existing static extension collisions are caught during composition and converted to structured contract diagnostics that name the contributors when available. Input-dependent grammar ambiguity stays a parse diagnostic rather than being claimed as a build-time check.

On any diagnostic the result is null, no partially composed language is published, and no host delegate runs. Composition does not load external assemblies, helper C#, or agent-generated artifacts. Those trust decisions belong to later milestones.

## Tests and delivery

Focused tests exercise a valid syntax plus semantics module and a semantic-only dependency; missing import; duplicate descriptor ID; absent and valid start rules; incompatible semantic operation exports; duplicate extension alternatives; missing host binding; host signature mismatch; and equivalent shared requirements in either descriptor order. A successful fixture parses and lowers a sample through the existing binding and HIR path. Existing Nitrogen tests and the repository fast gate check compatibility. The extended gate is required only if the change reaches predictive-control training, `artifacts/`, or the velocity profile.

Work is recorded in `issues/244-nitrogen-module-contract.md` on `codex/244-nitrogen-module-contract`. The existing untracked roadmap is preserved and receives a status link for milestone 1. No production parser or runtime caller switches to the new descriptor in this milestone.
