# Nitrogen Typed Capabilities Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admit reviewed local Nitrogen packages only when their declared operation IDs match exact host-owned pure capabilities, with `Geometry.BoxMesh` as the first execution proof.

**Architecture:** Extend the bounded package manifest with a canonical list of requested IDs. A host-owned immutable capability set supplies exact signatures and bindings. Candidate admission compares requests with descriptor requirements, selects only granted bindings, then reuses the milestone 5 example and atomic-publication pipeline.

**Tech Stack:** .NET 10/C#, xUnit, Nitrogen.Workspace, Nitrogen.Runtime, Nitrogen.Geometry, Gravity.RagdollEditor.

**Spec:** `docs/superpowers/specs/2026-09-26-nitrogen-typed-capabilities-design.md`

## Global Constraints

- This is a trusted local gate for human-reviewed declarative `.ngr`; do not add automatic untrusted-agent admission.
- `module.json` contains zero to sixteen distinct operation IDs in required `capabilities`; the package hash covers them in ordinal order.
- The package cannot supply signatures, handlers, C#, or effect grants. Only `Pure` effects are supported in this slice.
- Preserve the milestone 5 successful candidate-module example, exact diagnostic multisets, accepted leases, and atomic replacement.
- Work on `codex/249-nitrogen-typed-capabilities`, based on issue 248 commit `92fc5c77`. Preserve the unrelated untracked JpcSharp plan.
- Use `[codex/249-nitrogen-typed-capabilities]` at the start of each commit message. Do not merge or push.

## Review Focus

- Reordering the same requested IDs must preserve the hash; changing one ID must change it (Task 1).
- A package requesting an ID that is present in another profile must still be rejected by this profile (Task 3).
- A profile with an extra grant must not expose that binding in the accepted composition (Task 3).
- A dependency requiring an unrequested operation must reject admission before example probing (Task 3).
- A failed capability check must leave the previous active lease and snapshot usable (Task 4).

---

### Task 1: Parse and hash requested capability IDs

**Files:**
- Modify: `Nitrogen/Nitrogen.Workspace/Admission/ModulePackage.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/ModulePackageTests.cs`

**Interfaces:** `ModulePackage.RequestedCapabilities : IReadOnlyList<string>` is a copied, sorted list. Its internal constructor adds `IReadOnlyList<string> requestedCapabilities` before `sha256`; all direct test constructors are updated in this task. `ModulePackageLoader.Load` requires `capabilities` in the manifest.

- [ ] **Step 1: Write failing loader tests.** Add `capabilities = new[] { "Geometry.BoxMesh" }` to the valid fixture. Assert the loaded request; assert that missing, duplicate, blank, nonstring, and seventeen-entry arrays return `NA0001`. Write two manifests with the same two IDs in opposite order and assert equal hashes; change one ID and assert a different hash. Update direct package constructors in `ModuleAdmissionCompositionTests`, `ModuleAdmissionServiceTests`, and `GeneratedBoxAdmissionTests` to pass `[]` or `["Geometry.BoxMesh"]` as appropriate, keeping the project compiling once the constructor changes.
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ModulePackageTests -m:1 -p:UseSharedCompilation=false`. Expected: new capability property/validation assertions fail.
- [ ] **Step 3: Implement the manifest and hash.** Add `"capabilities"` to `RootFields`; use `RequiredArray(root, "capabilities")`, limit length to 16, parse each via `String`, reject whitespace and ordinal duplicates, sort and copy with `Array.AsReadOnly`. Pass the list into `ModulePackage` and `Hash`; write its count and sorted ID fields into the canonical hash stream before grammar fields. Keep the manifest's existing 16 KiB bound and `NA0001` mapping. Example invariant:

```csharp
var requested = capabilityJson.EnumerateArray().Select(String).ToArray();
if (requested.Length > 16 || requested.Any(string.IsNullOrWhiteSpace) ||
    requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
    throw Invalid("invalid capability request list");
Array.Sort(requested, StringComparer.Ordinal);
```

- [ ] **Step 4: Rerun** loader tests and the full `Nitrogen.Tests` project. Expected: package/hash cases pass and existing admission tests still compile and pass with updated direct constructors.
- [ ] **Step 5: Commit** with `[codex/249-nitrogen-typed-capabilities] Parse requested capability IDs`.

### Task 2: Define immutable host grants

**Files:**
- Create: `Nitrogen/Nitrogen.Workspace/Admission/HostCapabilitySet.cs`
- Modify: `Nitrogen/Nitrogen.Workspace/Admission/HostModuleProfile.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/HostCapabilitySetTests.cs`
- Test fixtures: `Nitrogen/Nitrogen.Tests/Workspace/Admission/ModuleAdmissionCompositionTests.cs`, `ModuleAdmissionServiceTests.cs`, `GeneratedBoxAdmissionTests.cs`

**Interfaces:** `HostCapability(OperationSignature Signature, HostOperationBinding Binding, CapabilityEffect Effect)` exposes `Id => Signature.Id`; `CapabilityEffect` initially contains only `Pure`. `HostCapabilitySet(IEnumerable<HostCapability>)` copies entries into a read-only ordinal dictionary, with `TryGet(string, out HostCapability)` and internal `AllBindings`. Replace `HostModuleProfile.Bindings` with `HostModuleProfile.Capabilities : HostCapabilitySet`; profile test fixtures pass a set containing their existing bindings, or an empty set.

- [ ] **Step 1: Write failing grant tests.** A pure grant with `Geometry.BoxMesh` exposes the exact signature and binding. Null entries, duplicate IDs, mismatched binding signatures, and a binding with an ID different from the declared signature throw `ArgumentException`; mutating the input list afterward cannot change the set. For the profile fixture, use:

```csharp
new HostCapabilitySet([new HostCapability(BoxMeshModule.BoxSignature,
    GeometryBoxMeshHost.Binding, CapabilityEffect.Pure)])
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~HostCapabilitySetTests -m:1 -p:UseSharedCompilation=false`. Expected: missing host-capability types.
- [ ] **Step 3: Implement the set and migrate profile fixtures.** Copy into `ReadOnlyDictionary<string,HostCapability>` with `StringComparer.Ordinal`; validate exact `OperationSignature.Equals`, reject duplicate IDs, and reject every effect other than `Pure`. Change the profile record field to `HostCapabilitySet Capabilities`. Update its three admission test fixtures, including the tests that deliberately supply missing or mismatched grants. In `TryCandidate`, temporarily pass `profile.Capabilities.AllBindings` to `ModuleComposer`; Task 3 replaces that call with selected bindings. `ModuleComposer` itself stays unchanged.
- [ ] **Step 4: Rerun** host-set tests and `FullyQualifiedName~Admission`. Expected: all focused tests pass with the migrated fixture, confirming that the host grant representation preserves existing behavior before selection is added.
- [ ] **Step 5: Commit** with `[codex/249-nitrogen-typed-capabilities] Define host capability grants`.

### Task 3: Select exact grants during candidate composition

**Files:**
- Modify: `Nitrogen/Nitrogen.Workspace/Admission/ModuleAdmissionService.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/ModuleAdmissionCompositionTests.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/ModuleAdmissionServiceTests.cs`

**Interfaces:** `TryCandidate` resolves `package.RequestedCapabilities` against `profile.Capabilities` after `profile.Describe`. A local helper returns either selected `HostOperationBinding[]` or `AdmissionDiagnostic[]` with `NA0006`, stage `capability`, path `module.json`. Required operations are the distinct signatures from `[descriptor, ..profile.Dependencies]`; request IDs must equal required IDs, and each selected signature must equal the required signature.

- [ ] **Step 1: Write failing candidate tests.** Unknown ID, an ID granted only by a different profile, a missing required ID, an unused requested ID, mismatched required signature, and an unrequested dependency operation each reject with `NA0006` before `Probe` runs. A profile with two grants and a descriptor requiring one admits with exactly one `Composition.HostBindings` entry. Rejection returns the package hash through `Admit` and leaves `Active` and an old lease usable. Construct examples with a successful candidate-module sample so failure is attributable to capability selection.
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter 'FullyQualifiedName~ModuleAdmissionCompositionTests|FullyQualifiedName~ModuleAdmissionServiceTests' -m:1 -p:UseSharedCompilation=false`. Expected: requested grants are not enforced.
- [ ] **Step 3: Implement selection.** Compare `HashSet<string>` of requests to descriptor/dependency requirements using ordinal equality, then compare each requirement's full signature to the host capability. Preserve duplicate incompatible requirements as a composition failure where `ModuleComposer` already emits `NM0005`. Pass only selected bindings to `ModuleComposer.TryCompose`; dispose the candidate snapshot on any `NA0006`. Keep `TryCandidate` profile exception mapping to `NA0003` and failure hash behavior in `Admit`. The decisive call becomes:

```csharp
if (!TrySelectCapabilities(package.RequestedCapabilities, profile.Capabilities,
        [descriptor, ..profile.Dependencies], out var bindings, out var errors))
{
    snapshot.Dispose();
    return new CandidateResult(null, errors);
}
if (!ModuleComposer.TryCompose([descriptor, ..profile.Dependencies], bindings,
        out var composition, out var compositionDiagnostics))
{
    snapshot.Dispose();
    return new CandidateResult(null, compositionDiagnostics.Select(d => new AdmissionDiagnostic(
        d.Code, "composition", string.Join(",", d.Modules), 0, 0, d.Message)).ToArray());
}
```

- [ ] **Step 4: Rerun** composition/service tests, then all admission tests. Expected: `NA0006` rejections occur before example probes, and selected bindings are exact.
- [ ] **Step 5: Commit** with `[codex/249-nitrogen-typed-capabilities] Enforce requested host grants`.

### Task 4: Prove the box capability and record verification

**Files:**
- Modify: `Nitrogen/Nitrogen.Tests/Workspace/Admission/GeneratedBoxAdmissionTests.cs`
- Modify: `issues/249-nitrogen-typed-capabilities.md`
- Modify: `docs/roadmap.md`

**Interfaces:** The generated box package requests `Geometry.BoxMesh`. Its host profile grants that exact pure capability and retains the trusted lowerer/probe. The accepted composition exposes that single binding, and the probe compares all vertices and indices against the editor generator.

- [ ] **Step 1: Write the end-to-end assertions.** Verify the loaded/generated box request, admission success, exactly one `HostBindings` entry with the exact signature, and the existing 24-vertex/36-index mesh parity. Change the requested ID to an unknown or missing ID and assert `NA0006`, zero additional probe/host calls, and the old accepted lease still parses and executes. Ensure an extra host grant is absent from the accepted composition.
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~GeneratedBoxAdmissionTests -m:1 -p:UseSharedCompilation=false`. Expected: the new assertions fail until the proof fixture and earlier tasks are complete; if the first run passes, record that outcome and avoid unnecessary production changes.
- [ ] **Step 3: Fix only demonstrated integration defects.** Keep edits inside the admission files or proof fixture. Do not change the editor production path or add effectful handlers.
- [ ] **Step 4: Verify** with `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj` and `dotnet test Gravity.slnx --filter "Gate!=Extended"`; compare RagdollEditor failures with issue 248. Run `git diff --check 92fc5c77` and check that no extended-gate trigger paths changed. Run the extended gate only if they did.
- [ ] **Step 5: Update** issue 249 and the roadmap with exact results, branch status, and the reviewed-local trust boundary. Commit with `[codex/249-nitrogen-typed-capabilities] Prove pure box capability`.

## Completion review

- [ ] Compare the branch with all four spec sections: request parsing/hash, immutable host grant, exact admission selection, and box execution proof.
- [ ] Review `git diff 92fc5c77..HEAD` for unrelated edits. Request one fresh whole-branch reviewer if available; address concrete findings with a failing test first.
- [ ] Leave the issue branch unmerged and unpushed; issues 244–248 remain dependent unmerged history.
