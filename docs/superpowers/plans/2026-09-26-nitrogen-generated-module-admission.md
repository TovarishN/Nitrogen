# Nitrogen Generated-Module Admission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admit a reviewed local declarative `.ngr` package only after its grammar, host-owned module contract, and example corpus pass; keep the previous accepted module on failure.

**Architecture:** A strict package loader and model-based C# policy run before `GrammarWorkspace.Compile`. A host profile creates the only semantic descriptor and host bindings; `ModuleComposer` validates them. An admission service tests a collectible candidate snapshot and atomically publishes an accepted handle with explicit leases.

**Tech Stack:** C#/.NET 10, `System.Text.Json`, `SHA256`, `Nitrogen.Grammar`, `Nitrogen.Workspace`, `ModuleComposer`, xUnit.

**Spec:** [2026-09-26-nitrogen-generated-module-admission-design.md](../specs/2026-09-26-nitrogen-generated-module-admission-design.md)

## Global Constraints

- Branch `codex/248-nitrogen-generated-module-admission`; every commit subject starts `[codex/248-nitrogen-generated-module-admission]`. Preserve unrelated untracked work and do not merge or push.
- This path accepts human-reviewed local packages only. No authored C#, helper sources, arbitrary assembly references, or automatic untrusted-agent admission. `GrammarWorkspace` remains an in-process compiler, not a sandbox.
- The package cannot specify operation signatures or bindings. Only a registered host profile may supply them. Existing Motion, Policy, Geometry editor, and live grammar-language paths remain unchanged.
- Use a separate candidate workspace for each admission. Do not expose `GrammarWorkspace.Current` as the active module. Publish only after every test passes; dispose rejected candidates and retain a prior accepted handle.
- Fast gate: `dotnet test Gravity.slnx --filter "Gate!=Extended"`; extended gate only for documented training, `artifacts/`, or velocity-profile changes. Issue 247 baseline has seven named RagdollEditor failures.

## File map

- Create `Nitrogen/Nitrogen.Workspace/Admission/ModulePackage.cs`: immutable manifest/example types and strict directory loader with limits and hash.
- Create `Nitrogen/Nitrogen.Workspace/Admission/DeclarativeGrammarPolicy.cs`: AST-based rejection of authored C# entry points.
- Create `Nitrogen/Nitrogen.Workspace/Admission/HostModuleProfile.cs`: trusted descriptor/binding/probe contract, with no package-supplied executable code.
- Create `Nitrogen/Nitrogen.Workspace/Admission/ModuleAdmissionService.cs`: candidate compilation, composition, example verification, activation, diagnostics, and handle ownership.
- Add focused tests under `Nitrogen/Nitrogen.Tests/Workspace/Admission/` and a generated-box proof using the existing `GeometryBoxMeshHost.Binding` only from the test project.
- Update `issues/248-nitrogen-generated-module-admission.md` and `docs/roadmap.md` after verification.

## Review Focus

1. `../outside.ngr`, an absolute path, duplicate grammar path, or an unknown JSON property must be rejected before any disk read beyond the package root (Task 1 tests).
2. A semantic block on an extension alternative or a `symbol property` must be rejected before Roslyn compilation, even if no helper `.cs` file exists (Task 2 tests).
3. A package naming a valid grammar but a profile with a different module or a missing required host binding must fail without replacing the active module (Task 3 tests).
4. A negative sample with an expected diagnostic must never call the host; a positive sample with an unexpected root or wrong result must fail admission (Task 4 tests).
5. A prior accepted handle must stay usable through a failed candidate or a successful replacement, then release its collectible context after the final lease is disposed (Task 4 tests).

---

### Task 1: Parse and identify bounded candidate packages

**Files:**
- Create: `Nitrogen/Nitrogen.Workspace/Admission/ModulePackage.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/ModulePackageTests.cs`

**Interfaces:**
- Produces `ModulePackageLoader.Load(string directory) -> ModulePackageLoadResult` with `Package` nullable and structured `AdmissionDiagnostic`s. `ModulePackage` contains `Id`, `ProfileId`, `ModuleName`, `StartRule`, sorted grammar texts, ordered `ModuleExample`s, and `Sha256`. Define the shared records with these fields:

```csharp
public sealed record AdmissionDiagnostic(string Code, string Stage, string Path,
    int Line, int Column, string Message);
public sealed record ModuleExample(string Id, string Path, string Source,
    IReadOnlyList<string> ExpectedDiagnostics, string? ExpectedResult);
public sealed record ModulePackageLoadResult(ModulePackage? Package,
    IReadOnlyList<AdmissionDiagnostic> Diagnostics);
public sealed record ModulePackage(string Id, string ProfileId, string ModuleName,
    string StartRule, IReadOnlyDictionary<string, string> Grammars,
    IReadOnlyList<ModuleExample> Examples, string Sha256);
```
- Manifest `module.json` shape: `{ "id": "box-demo", "profile": "geometry-box", "module": "GeneratedBox", "start": "GeneratedBox.Document", "grammars": ["box.ngr"], "examples": [{ "id": "valid", "path": "valid.box", "source": "box 1 2 3;", "diagnostics": [], "expectedResult": "box:1,2,3" }] }`. `expectedResult` may be absent only for a negative example.
- Use fixed limits: manifest 16 KiB; 4 grammar files, 64 KiB each, 256 KiB combined; 16 examples, 16 KiB per source. Only one-level relative `.ngr` filenames; no separators, `.` or `..`. Use UTF-8, reject unknown properties and duplicate IDs/paths. SHA256 hashes length-prefixed canonical UTF-8 fields plus sorted grammar content and example content.

- [ ] **Step 1: Write failing loader tests.** In a temporary package directory, write the manifest above and a simple grammar. Assert exact fields and repeatable hash. Test a second package with the same logical fields but different JSON whitespace gives the same hash. Test `../outside.ngr`, `/tmp/outside.ngr`, duplicate `box.ngr`, duplicate example ID, unknown `sources` property, missing file, a symlink to a file outside the root, a 65 KiB grammar, and a 17th example; each must yield `NA0001` and no package. Example assertion:

```csharp
var loaded = ModulePackageLoader.Load(root);
Assert.NotNull(loaded.Package);
Assert.Empty(loaded.Diagnostics);
Assert.Equal("box-demo", loaded.Package.Id);
Assert.Equal(loaded.Package.Sha256, ModulePackageLoader.Load(whitespaceVariantRoot).Package!.Sha256);
```
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ModulePackageTests`. Expected: compilation fails because `ModulePackage` is absent.
- [ ] **Step 3: Implement strict loading.** Parse `module.json` with `JsonDocument` and explicit property allowlists; check byte lengths before `ReadAllText`; reject unsafe names and `FileInfo.LinkTarget != null` before reading grammar files. Sort grammar names with `StringComparer.Ordinal`. For canonical hashing, append each UTF-8 field as a 4-byte length followed by bytes, then lowercase hex SHA256. Convert `JsonException` and `IOException` to `NA0001`. The path check begins with:

```csharp
static bool SafeGrammarName(string name) => name.Length > 4 && name.EndsWith(".ngr", StringComparison.Ordinal) &&
    name != "." && name != ".." && name.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(name);
```
- [ ] **Step 4: Rerun** `FullyQualifiedName~ModulePackageTests`. Expected: all valid and rejection cases pass; a missing package does not access the outside marker file.
- [ ] **Step 5: Commit** with `[codex/248-nitrogen-generated-module-admission] Load bounded declarative packages`.

### Task 2: Reject authored C# before workspace compilation

**Files:**
- Create: `Nitrogen/Nitrogen.Workspace/Admission/DeclarativeGrammarPolicy.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/DeclarativeGrammarPolicyTests.cs`

**Interfaces:**
- Produces `DeclarativeGrammarPolicy.Validate(ModulePackage package) -> IReadOnlyList<AdmissionDiagnostic>` using `GrammarParser.Parse` on each grammar. It returns existing grammar syntax codes for parse failures and `NA0002` at the offending span for C# surfaces.
- `AdmissionDiagnostic` is shared with Task 1; its path, 1-based line/column, stage, code, and message can render in issue logs and tests.

- [ ] **Step 1: Write failing policy tests.** A syntax/token/binding-only grammar passes. Separate grammars containing a rule semantics block (`{ out Value : int = 0; ... }`), an extension alternative semantics block, `ExtensibleRule.Properties`, and `symbol property` each yield `NA0002` at the block/property. A malformed `.ngr` yields its grammar syntax code with the correct line and does not proceed to compilation. Ensure a `using Other;` grammar import remains allowed. Pin a rule-block span with:

```csharp
var diagnostic = Assert.Single(DeclarativeGrammarPolicy.Validate(package));
Assert.Equal("NA0002", diagnostic.Code);
Assert.Equal("rules.ngr", diagnostic.Path);
Assert.Equal(4, diagnostic.Line);
```
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~DeclarativeGrammarPolicyTests`. Expected: missing policy type or failing rejection assertions.
- [ ] **Step 3: Implement AST inspection.** For each `GrammarParser.Parse(text).File`, walk `ModuleDecl.Rules` and `Extends`; reject non-null `SyntaxRule.Semantics`, non-null `Alternative.Semantics`, nonempty `ExtensibleRule.Properties`, and nonempty `ModuleDecl.SymbolProperties`. Convert `GrammarSpan.Start` to line/column using source text. Never compile a rejected package. The core scan is:

```csharp
foreach (var module in parsed.File.Modules)
{
    foreach (var rule in module.Rules)
    {
        if (rule is SyntaxRule { Semantics: { } block }) Report(block.Span);
        if (rule is ExtensibleRule extensible)
        {
            foreach (var property in extensible.Properties) Report(property.Span);
            foreach (var alternative in extensible.Alternatives)
                if (alternative.Semantics is { } body) Report(body.Span);
        }
    }
    foreach (var extension in module.Extends)
        foreach (var alternative in extension.Alternatives)
            if (alternative.Semantics is { } body) Report(body.Span);
    foreach (var property in module.SymbolProperties) Report(property.Span);
}
```
- [ ] **Step 4: Rerun** the policy tests and existing `FullyQualifiedName~WorkspaceTests`. Expected: policy decisions pass and unrestricted `GrammarWorkspace` behavior remains unchanged for its existing callers.
- [ ] **Step 5: Commit** with `[codex/248-nitrogen-generated-module-admission] Enforce syntax-only grammar policy`.

### Task 3: Compose a candidate with a registered host profile

**Files:**
- Create: `Nitrogen/Nitrogen.Workspace/Admission/HostModuleProfile.cs`
- Begin: `Nitrogen/Nitrogen.Workspace/Admission/ModuleAdmissionService.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/ModuleAdmissionCompositionTests.cs`

**Interfaces:**
- `HostModuleProfile` has immutable `Id`, `ModuleName`, `Func<SyntaxModule, ModuleDescriptor> Describe`, trusted dependency descriptors, exact host bindings, and `Func<ModuleComposition, FileSemantics, IReadOnlyList<HirNode>, string?, string?> Probe` (null means accepted result; a message means mismatch). Profile registry is a caller-supplied read-only dictionary keyed by ID.
- `ModuleAdmissionService.TryCandidate(ModulePackage, profiles)` returns a candidate snapshot/composition or diagnostics, without changing `Active`. It calls the policy before creating `GrammarWorkspace`, compiles in a fresh workspace with empty `Sources`, `References`, and `Usings`, requires exactly the manifest-named syntax module, calls `Describe`, then `ModuleComposer.TryCompose`.

`CandidateResult` is an internal record of `AdmissionCandidate? Candidate` and `IReadOnlyList<AdmissionDiagnostic> Diagnostics`. `AdmissionCandidate` owns its `WorkspaceSnapshot`, `ModuleComposition`, host profile, and start-rule map; `Dispose()` unloads an unaccepted snapshot, while `TakeSnapshot()` transfers ownership to Task 4.

```csharp
public sealed record HostModuleProfile(string Id, string ModuleName,
    Func<SyntaxModule, ModuleDescriptor> Describe,
    IReadOnlyList<ModuleDescriptor> Dependencies,
    IReadOnlyList<HostOperationBinding> Bindings,
    Func<ModuleComposition, FileSemantics, IReadOnlyList<HirNode>, string?, string?> Probe);
```

- [ ] **Step 1: Write failing composition tests.** Valid generated `GeneratedBox` syntax plus a host profile reaches a composed candidate. Unknown profile, wrong module name, absent `GeneratedBox.Document`, a profile whose descriptor has a missing required host binding, and a profile whose operation signature disagrees with its binding all fail with `NA0003` or existing `NM0002/NM0004/NM0005` and do not publish. A forbidden semantics block fails with `NA0002` before its C# can run. Example composition assertion:

```csharp
var result = service.TryCandidate(package, profiles);
Assert.Null(result.Candidate);
Assert.Contains(result.Diagnostics, d => d.Code == "NM0004");
Assert.Null(service.Active);
```
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ModuleAdmissionCompositionTests`. Expected: missing candidate/profile APIs.
- [ ] **Step 3: Implement candidate composition.** Build a `GrammarWorkspace` per attempt, call `SetGrammar` in sorted path order, then `Compile`; on failure map `WorkspaceDiagnostic`s to admission diagnostics and dispose the snapshot. Select the sole syntax module by exact `ModuleName`; reject extra or missing modules. Verify the declared start is inside that module. Invoke only registered host `Describe`; compose `[descriptor, ..profile.Dependencies]` with `profile.Bindings`. Preserve `NM` codes and owner names. A candidate is internal and `IDisposable`, owning the workspace snapshot until accepted or rejected. The critical sequence is:

```csharp
var workspace = new GrammarWorkspace();
foreach (var (path, source) in package.Grammars) workspace.SetGrammar(path, source);
var snapshot = workspace.Compile();
if (!snapshot.Succeeded) return Failure(snapshot.Diagnostics, snapshot);
var syntax = AssertSingleModule(snapshot.Language!.Modules, profile.ModuleName);
var descriptor = profile.Describe(syntax);
if (!ModuleComposer.TryCompose([descriptor, ..profile.Dependencies], profile.Bindings,
        out var composition, out var errors)) return Failure(errors, snapshot);
return Success(new AdmissionCandidate(snapshot, composition!, profile));
```
- [ ] **Step 4: Rerun** composition tests and `FullyQualifiedName~ModuleComposerTests`. Expected: composition rejections are deterministic and existing C# descriptors retain their behavior.
- [ ] **Step 5: Commit** with `[codex/248-nitrogen-generated-module-admission] Compose candidates with trusted host profiles`.

### Task 4: Verify examples and publish a leased accepted handle

**Files:**
- Complete: `Nitrogen/Nitrogen.Workspace/Admission/ModuleAdmissionService.cs`
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/ModuleAdmissionServiceTests.cs`

**Interfaces:**
- `ModuleAdmissionService.Admit(ModulePackage package, IReadOnlyDictionary<string,HostModuleProfile> profiles) -> AdmissionResult` runs all gates and swaps `Active` only on success. `AdmissionResult` holds accepted package hash or diagnostics; `Active` is an immutable `AcceptedModule` with `Acquire() -> AcceptedModuleLease` and snapshot/composition access through the lease. On replacement, retire the prior handle; dispose its snapshot after its last lease. Service disposal retires the active handle.
- `AdmissionResult` is `record(bool Accepted, string? Sha256, IReadOnlyList<AdmissionDiagnostic> Diagnostics)`. `AcceptedModuleLease` exposes `Module`, `Composition`, and `StartRule` for the default start; it is `IDisposable` and decrements the module's lease count once.
- For each example, parse with its declared start; place successful trees in one `Project` so cross-file exports work; collect parse, `Project.Diagnostics(path)`, `FileSemantics.Diagnostics()`, and `HirLowering.Lower` codes. Compare sorted code multisets to manifest expectations. Run `Probe` only when expected and actual diagnostics are empty; mismatch is `NA0004` (diagnostics) or `NA0005` (result).

- [ ] **Step 1: Write failing activation tests.** Admit valid A, acquire a lease and parse its sample. Attempt invalid B (grammar syntax error), invalid C (wrong expected code), and invalid D (profile probe returns mismatch); assert `Active` remains A each time. Admit valid E while A's lease is held; assert both work, E becomes active, and A unloads only after its lease is released. A negative sample with an expected binder/parse diagnostic must not call `Probe` or the host. A positive sample returning zero or two HIR roots when one was expected is rejected by the host probe. Check input-order-independent diagnostics and package hash. Example lifecycle check:

```csharp
Assert.True(service.Admit(validA, profiles).Accepted);
using var old = service.Active!.Acquire();
Assert.False(service.Admit(invalidB, profiles).Accepted);
Assert.Same(old.Module, service.Active);
Assert.True(service.Admit(validE, profiles).Accepted);
Assert.NotSame(old.Module, service.Active);
using var parse = old.Composition.Language.Parse("box 1 2 3;", old.StartRule);
Assert.True(parse.Success);
```
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ModuleAdmissionServiceTests`. Expected: activation and lease API tests fail.
- [ ] **Step 3: Implement the pipeline.** Keep parsed results alive until all project/semantic/lowering checks finish; dispose them in a `finally`. Compare diagnostic code counts, not sets. If any example fails, dispose the candidate and return diagnostics without touching `Active`. On success construct an `AcceptedModule`, exchange `Active` under a lock, mark the old handle retired, and unload only when its lease count reaches zero. A failed or disposed service cannot hand out a new lease. The profile probe validates exact root type/count/signature and finite values before any host call. Publish at one point:

```csharp
if (diagnostics.Count != 0) { candidate.Dispose(); return new AdmissionResult(false, null, diagnostics); }
var accepted = new AcceptedModule(package.Sha256, candidate.TakeSnapshot(), candidate.Composition,
    candidate.StartRules);
lock (_gate)
{
    var previous = _active;
    _active = accepted;
    previous?.Retire();
}
return new AdmissionResult(true, package.Sha256, []);
```
- [ ] **Step 4: Rerun** service, composition, policy, loader, and existing workspace snapshot/unload tests. Expected: prior active handles remain valid and rejected snapshots unload.
- [ ] **Step 5: Commit** with `[codex/248-nitrogen-generated-module-admission] Admit validated candidate snapshots`.

### Task 5: Prove a generated box module and record gate evidence

**Files:**
- Test: `Nitrogen/Nitrogen.Tests/Workspace/Admission/GeneratedBoxAdmissionTests.cs`
- Modify: `issues/248-nitrogen-generated-module-admission.md`
- Modify: `docs/roadmap.md`

**Interfaces:**
- The proof's generated `.ngr` is syntax-only: `GeneratedBox.Document = Boxes:Box*`, `Box = "box" Width:Digits Height:Digits Depth:Digits ";"`. The trusted test profile locates the generated `Box` kind via `SyntaxModule.GetKindName(localKind)` over the grammar's bounded rule count, registers a `LoweringRegistration` to `Geometry.BoxMesh`, and uses `GeometryBoxMeshHost.Binding`. Its probe executes exactly one `HirOperation` via `GeometryExecutor` and checks full vertices/indices against `MeshGenerator.GenerateBox(new Vector3(0.5f,1f,1.5f))`.

- [ ] **Step 1: Write failing end-to-end tests.** A package with `box 1 2 3;` and a negative sample `box no 2 3;` must admit under the trusted profile, report no unexpected diagnostics, and yield a 24-vertex/36-index renderable mesh identical to the existing generator. Change the grammar to require `cube` while retaining the old sample; the candidate must fail `NA0004`, and a lease on the old accepted module must still parse/execute `box 1 2 3;`. Verify the manifest cannot replace the profile's host operation with a wrong ID/signature. Pin the mesh with:

```csharp
var result = GeometryExecutor.Execute(Assert.Single(lowered.Roots), composition);
var mesh = Assert.IsType<GeometryMesh>(result.Mesh);
var (vertices, indices) = MeshGenerator.GenerateBox(new Vector3(0.5f, 1f, 1.5f));
Assert.Equal(indices, mesh.Indices);
Assert.Equal(vertices.Select(v => new MeshVertex(v.Position, v.Normal)), mesh.Vertices);
```
- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~GeneratedBoxAdmissionTests`. Expected: the proof fails until the admission flow and host profile are complete; if Tasks 1–4 already satisfy it, record a first-run pass and avoid unnecessary production edits.
- [ ] **Step 3: Fix only demonstrated integration defects.** Keep changes inside admission files; do not wire this proof into the editor runtime. Preserve candidate paths and diagnostic codes through the full pipeline, and make a failed sample retain the previous active handle. If the first run passes, record that the prior tasks supplied the complete proof and make no production edit here.
- [ ] **Step 4: Run verification.** Run `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`; run `dotnet test Gravity.slnx --filter "Gate!=Extended"`; compare the seven RagdollEditor failure names with issue 247; run `git diff --check ca711f72`. Run the extended gate only if documented trigger files changed.
- [ ] **Step 5: Update** issue 248 and roadmap with exact results and scope/trust boundary, then commit with `[codex/248-nitrogen-generated-module-admission] Prove generated box admission`.

## Completion review

- [ ] Compare the whole branch with spec sections 1–5: package fields and hash, source-only policy, profile authority, exact diagnostic expectations, HIR/execution probe, atomic publication, leases, and trust statement.
- [ ] Review `git diff ca711f72..HEAD` for unrelated edits and product-path changes. Request one fresh whole-branch reviewer if available; address concrete findings with a failing test first.
- [ ] Leave the branch unmerged and unpushed; issues 244–247 remain unmerged dependencies.
