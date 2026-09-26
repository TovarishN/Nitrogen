# Nitrogen typed capability requests — design

**Issue:** 249. **Date:** 2026-09-26. **Status:** written design for review.
**Base:** issue 248 branch at `92fc5c77`; issues 244–248 remain unmerged into `master`.

## 1. Purpose and first proof

Milestone 6 makes a generated module's requested host operations explicit and checks each request against a grant controlled by the application. The first proof uses the pure `Geometry.BoxMesh` operation and the reviewed local box package from milestone 5. A passing package must still compile, parse, lower, and produce the exact editor mesh. An unknown, missing, or incompatible request must fail before an example probe or host handler runs, leaving the last accepted module usable.

The request list does not give the package authority. Human review remains necessary before locally admitting agent-produced text. This milestone does not create an untrusted-code sandbox or automatic agent admission path.

## 2. Package request and host grant

Add a required `capabilities` array of zero to sixteen unique operation IDs to `module.json`. An empty array supports syntax-only modules. IDs are nonblank, use ordinal comparison, and are ordered canonically when hashed, so reordering the same requests preserves package identity. The bounded manifest and package hash include this field; missing, duplicate, malformed, or excessive entries receive package diagnostic `NA0001`. The package never supplies operation signatures, handlers, implementation code, or effects.

The application registers an immutable `HostCapabilitySet` for a `HostModuleProfile`. Each `HostCapability` contains an operation ID, the exact `OperationSignature`, a `HostOperationBinding` with that same signature, and an effect classification. The first classification is `Pure`; there are no effectful grants in this slice. Constructing the set rejects duplicate IDs, mismatched IDs or signatures, and null entries. The profile's `Describe` callback remains host-owned and builds the module descriptor and lowerer. The capability set is the only source of bindings passed to composition for an admitted package.

## 3. Admission and execution boundary

After declarative grammar validation and host profile selection, resolve package requests against that profile's capability set. Unknown IDs fail with `NA0006`, naming the requested ID. Require the resolved request IDs to match the operations required by the candidate descriptor and its declared dependencies. A missing required request or a request unused by those descriptors also fails with `NA0006`. Compare signatures exactly, including result and ordered inputs; a mismatch fails before examples. `ModuleComposer` continues to validate semantic exports, imports, and exact host bindings, retaining its `NM` diagnostics for those failures.

Pass only the selected host bindings into `ModuleComposer`. The accepted `ModuleComposition.HostBindings` therefore contains no unrequested operation. The existing box executor still checks root signature, argument values, and handler type before invocation. Failed capability checks dispose the candidate snapshot and do not replace `Active`. The result retains the package hash and returns a structured diagnostic. The accepted handle and lease ownership rules from issue 248 remain unchanged.

No capability request can select a dependency start rule or avoid the successful candidate-module example required by milestone 5. An operation exported by semantics but absent from the selected bindings cannot run.

## 4. Compatibility, tests, and delivery

The milestone 5 package format is not yet integrated into `master`, so make `capabilities` required and update its loader fixtures, direct test packages, and generated box proof together. The prior `HostModuleProfile.Bindings` list is replaced by a capability set; ordinary C# `ModuleComposer` and `HostOperationRegistry` callers retain their existing APIs. The first test profile grants only `Geometry.BoxMesh` and requires it in the descriptor. A positive package requests that ID and produces the same 24-vertex, 36-index mesh.

Test canonical request hashing; malformed, duplicate, and oversized request arrays; an empty request for a syntax-only profile; unknown and unused requests; missing required requests; signature and handler mismatches; dependency requirements; a grant with extra unrequested operations; no probe or host invocation after rejection; and previous-active preservation. Confirm the accepted composition exposes exactly the requested binding. Run the full Nitrogen suite and the Gravity fast gate; compare the known seven RagdollEditor failures. Run the extended gate only if its documented trigger paths change.

Track work in `issues/249-nitrogen-typed-capabilities.md` on `codex/249-nitrogen-typed-capabilities`. Preserve unrelated working-tree files. Do not merge or push as part of this milestone. Non-pure permissions, resource limits, and automatic admission of agent output require a concrete use case and separate design.
