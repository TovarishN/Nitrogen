# Nitrogen generated-module admission — design

**Issue:** 248. **Date:** 2026-09-26. **Status:** written design for review.
**Base:** `codex/248-nitrogen-generated-module-admission` starts at issue 247 commit `ca711f72`. Issues 244–247 are not merged into `master`.

## 1. Purpose and acceptance proof

Milestone 5 admits a reviewed, locally authored generated syntax module through explicit gates instead of registering a grammar as soon as it compiles. A candidate package contains `.ngr` grammar text, its start rule, positive and negative example documents, and the expected diagnostic codes for each example. A trusted host profile supplies the semantic descriptor, lowerer, required operation signatures, exact host bindings, and an executable acceptance probe. A passing candidate becomes the active module; a failed candidate leaves the previous active module usable.

The first proof uses a small generated box grammar and a host-owned profile that lowers its positive sample to the existing `Geometry.BoxMesh` operation and executes through the existing box-mesh host adapter. It compares the resulting mesh with `MeshGenerator.GenerateBox`. A negative sample proves the declared diagnostic expectation. This exercises compile, parse, bind, semantic composition, HIR lowering, and host execution without changing the editor's production path. An edited grammar with a broken sample must be rejected while the last accepted sample still parses and executes.

## 2. Candidate and host-owned profile

The candidate is an immutable in-memory package loaded from a small directory format. Its manifest names a package ID, a registered host-profile ID, one or more relative `.ngr` files, a module-qualified start rule, and a bounded list of example documents. Each example includes its source, a start rule (or the package default), and an exact expected set of diagnostic codes; successful examples may also name a profile-defined expected result. Reject absolute or escaping paths, duplicate files, duplicate example IDs, unknown manifest fields, missing files, and oversized inputs before compilation. The package cannot supply C# files, assembly references, namespace imports, semantic descriptors, operation signatures, or host bindings.

The host profile is registered in C# by the application. It pins the allowed syntax module name, constructs a `ModuleDescriptor` from the compiled `SyntaxModule`, supplies trusted semantic dependencies and `HostOperationBinding`s, and checks a successful example's typed HIR/execution result. The manifest selects a known profile by ID; it cannot alter the profile's permissions or signatures. `ModuleComposer` remains the authority for syntax/semantic conflicts, start rules, and exact required host bindings.

## 3. Validation and activation flow

Admission proceeds in this order:

1. Parse and validate the manifest, paths, sizes, profile ID, and example count. Use fixed, documented bounds for grammar bytes, example bytes, and number of files/examples. Return source-linked package diagnostics when possible.
2. Parse every `.ngr` file with `GrammarParser` and inspect the model before generated C# is compiled. Permit syntax, tokens, imports, extensions, and binding metadata. Reject every authored C# entry point: rule/alternative semantics blocks, extensible-rule properties, and symbol properties. The admission API never populates `GrammarWorkspace.Sources`, `References`, or `Usings`.
3. Compile the declarative grammar in a fresh `GrammarWorkspace` and hold the resulting `WorkspaceSnapshot` as a candidate only. Reject grammar or generated-C# diagnostics, wrong/missing syntax module, missing start rule, and any `ModuleComposer` diagnostic.
4. Run the bounded examples with the candidate composition. Compare complete diagnostic-code multisets, not just success flags. For a successful example, the host-owned profile checks the expected HIR and execution result. Any mismatch rejects the candidate with the example path and a stable admission code. Dispose failed candidate snapshots.
5. Publish a single immutable accepted handle containing the package identity/hash, `WorkspaceSnapshot`, `ModuleComposition`, and validated start rules. Replace the active handle only after every gate succeeds. Readers already holding an older handle may finish with it; disposal/unloading follows ownership of those handles. Keep the previous active handle on any failure.

No candidate enters `LanguageRegistry` or another product registry before acceptance. The package hash covers canonical manifest and grammar/example bytes, and is reported with admission results for reproducibility. Re-admitting the same bytes is deterministic apart from snapshot IDs.

## 4. Diagnostics and trust boundary

Admission returns structured diagnostics with a stable code, stage, package-relative path, and location when available. Grammar errors retain their `.ngr` location; composition errors retain `NM` codes and involved module IDs; example mismatches name the example and expected/actual diagnostic codes. A failed candidate has no active handle and no host execution beyond the trusted positive-sample probe. Expected negative samples cannot invoke host operations. The probe rejects unexpected roots, nonfinite values, and wrong signatures before calling the host.

This is a **trusted local gate**, not a sandbox for arbitrary untrusted code. `GrammarWorkspace` compiles generated C# and loads a candidate assembly in-process, and collectible assembly loading does not restrict CPU, memory, or platform APIs. Therefore the admission API accepts only packages reviewed by a human or another trusted local workflow. Agent-produced text is not admitted automatically. The syntax-only check removes authored helper C# from this path, but does not by itself establish a security boundary. Process isolation, operating-system resource limits, and automatic untrusted-agent admission are deferred to a separate design.

## 5. Tests and delivery

Test manifest/path/size limits; each forbidden C# surface; successful generated-box admission and exact mesh output; grammar errors; missing profile, module, start rule, and host binding; diagnostic-code mismatch; wrong typed HIR or execution result; negative examples; failed re-admission preserving the prior active handle; and candidate snapshot disposal. Verify an accepted handle can be used after a later failed attempt, and that explicitly released handles permit collectible unloading. Run the full Nitrogen suite and Gravity fast gate, comparing the known seven RagdollEditor failures. The extended gate applies only if its documented trigger files change.

Track implementation in `issues/248-nitrogen-generated-module-admission.md` on the issue-numbered branch. Preserve the existing `GrammarWorkspace`, `ModuleComposer`, Motion/Policy paths, Geometry editor adapter, and unrelated untracked files. Do not merge or push without a separate user request. Milestone 6 capability contracts remain separate.
