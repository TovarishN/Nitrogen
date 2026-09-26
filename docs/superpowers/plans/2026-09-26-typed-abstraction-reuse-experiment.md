# Typed Abstraction Reuse Experiment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admit agent-authored pure typed abstractions, execute them in a small support-and-balance language, and measure whether a fresh agent reuses an abstraction from an earlier task.

**Architecture:** Keep the existing reviewed-local `.ngr` admission and numeric evaluator compatible. Add a bounded declarative semantic file to the package, compile its typed definitions into HIR, and evaluate an extended pure value slice. Use a fixed host-owned expression grammar and four small host modules for the pilot; a separate runner supplies fresh agent sessions and records matched controls.

**Tech Stack:** .NET 10, C# preview, `System.Text.Json`, xUnit, existing `Nitrogen.Runtime`, `Nitrogen.Workspace`, and generated `.ngr` syntax.

**Spec:** [typed-abstraction-reuse-experiment-design.md](../specs/2026-09-26-typed-abstraction-reuse-experiment-design.md), with [task cards](../../experiments/typed-abstraction-reuse/task-suite.md) and [run record schema](../../experiments/typed-abstraction-reuse/run-record.schema.json).

## Global Constraints

- Preserve the existing `ModulePackageLoader`, `HostModuleProfile`, `ModuleComposer`, `HirEvaluator`, Geometry executor, and their current callers.
- The agent artifact contains declarative data only: no authored C#, delegates, assembly references, or arbitrary host bindings.
- Use exact module-qualified types and signatures; never erase length or mass to `Core.Scalar` to pass validation.
- Execute only pure granted operations after composition, type checking, lowering, and preflight; retain definition and call origins.
- Keep generated task variants and oracle outputs outside the agent-visible workspace until evaluation ends.
- Run `dotnet build Nitrogen.slnx` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj` after product changes; run focused tests after each task.
- Commit each independently passing task without staging `.idea/`; do not merge or push.

## File map

| File | Responsibility |
| --- | --- |
| `Nitrogen.Workspace/Admission/DerivedPackage.cs` | Load and hash one bounded `semantics.json` beside the existing manifest. |
| `Nitrogen.Workspace/Admission/DerivedDefinitionMapper.cs` | Map the validated package data to Runtime definitions. |
| `Nitrogen.Runtime/Semantic/DerivedDefinition.cs` | Immutable nominal record, function, and expression definitions. |
| `Nitrogen.Runtime/Semantic/DerivedBinder.cs` | Resolve imports, fields, functions, calls, and exact types; return source-linked diagnostics. |
| `Nitrogen.Runtime/Semantic/PureValue.cs` | Finite typed number, boolean, record, and list values. |
| `Nitrogen.Runtime/Semantic/PureHir.cs` | HIR nodes for record, field, conditional, list, and derived call. |
| `Nitrogen.Runtime/Semantic/PureEvaluator.cs` | Pure operation registry, whole-tree preflight, and bounded evaluation of extended HIR. |
| `Nitrogen.Runtime/Semantic/DerivedCompiler.cs` | Lower checked bodies to HIR templates and instantiate calls with both origins. |
| `Nitrogen.Workspace/Admission/ModuleAdmissionService.cs` | Admit a derived package atomically through existing composition and example gates. |
| `Nitrogen.Experiments/` | Fixed pilot grammar, host capabilities, deterministic oracle, and run orchestration. |
| `Nitrogen.Tests/Experiment/` | Focused parser, binder, evaluator, admission, environment, and runner tests. |

The new expression grammar is host-owned and fixed for this pilot. `semantics.json` defines typed exports and expression bodies; it is **not** treated as arbitrary `.ngr` C# semantics. The precise JSON contract is frozen by Task 1 tests before any agent run.

## Review Focus

1. A record with the expected name but wrong field type must be rejected before execution (Task 2).
2. A derived function that calls itself indirectly must fail with a source-linked cycle diagnostic (Task 4).
3. A list or nested record containing a nonfinite number must fail preflight before any host call (Task 3).
4. A failed replacement package must leave the old accepted lease callable (Task 5).
5. B must not receive A's transcript or hidden oracle through its catalog, prompt, or filesystem (Task 7).

---

### Task 1: Bounded declarative artifact

**Files:** Create `Nitrogen.Workspace/Admission/DerivedPackage.cs`; modify `Nitrogen.Workspace/Admission/ModulePackage.cs`; test `Nitrogen.Tests/Experiment/DerivedPackageTests.cs`.

**Interfaces:** Produce `DerivedPackage` with `ModuleId`, `Imports`, `Records`, `Functions`, `Examples`, `RequestedCapabilities`, and `Sha256`; `DerivedPackageLoader.Load(string directory)` returns package or `AdmissionDiagnostic`s. `semantics.json` has `module`, `imports`, `records`, `functions`, and `examples`; each expression is a JSON tree with one of `number`, `bool`, `parameter`, `record`, `field`, `list`, `if`, or `call` tags. The existing `module.json` gains optional `semantics: "semantics.json"`; absence retains current behavior.

The smallest valid body shape is explicit about names and types; operation arguments are ordered arrays. The loader checks shape and bounds, while Task 2 checks the type claims.

```json
{
  "module": "Balance", "imports": ["Units"], "records": [],
  "functions": [{
    "id": "Balance.IdentityLength", "parameters": [{"name": "x", "type": "Units.Length"}],
    "result": "Units.Length", "body": {"parameter": "x"}
  }],
  "examples": [{"id": "identity", "arguments": [{"number": 0.5, "type": "Units.Length"}],
    "expected": {"number": 0.5, "type": "Units.Length"}}]
}
```

- [ ] **Step 1: Write failing tests.** Create a valid small module and assert stable hash under JSON whitespace changes; reject duplicate keys, unknown tags, escaping paths, symlinks, more than 64 KiB, more than 16 functions, and any `csharp` or `assembly` property.

```csharp
var loaded = DerivedPackageLoader.Load(directory);
Assert.Empty(loaded.Diagnostics);
Assert.Equal("Balance", loaded.Package!.ModuleId);
Assert.Equal(64, loaded.Package.Sha256.Length);
Assert.Contains(DerivedPackageLoader.Load(badDirectory).Diagnostics, d => d.Code == "ND0001");
```

- [ ] **Step 2: Confirm red.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~DerivedPackageTests`; expect compile failure because the loader is absent.
- [ ] **Step 3: Implement the strict loader.** Use `JsonDocument` with a closed field set, depth limit 32, UTF-8 byte bound, no linked file, and canonical length-prefixed hashing of parsed logical fields. Copy all arrays into immutable/read-only containers. Keep the existing package hash unchanged when `semantics` is absent; include the derived hash when present.

```csharp
public static DerivedPackageLoadResult Load(string directory);
public sealed record DerivedPackageLoadResult(DerivedPackage? Package,
    IReadOnlyList<AdmissionDiagnostic> Diagnostics);
```

- [ ] **Step 4: Confirm green.** Rerun the focused filter and `dotnet build Nitrogen.slnx`; expect all new cases and the existing `ModulePackageTests` to pass.
- [ ] **Step 5: Commit.** Stage only Task 1 files and commit `Add bounded derived-module package format`.

### Task 2: Exact type and definition checking

**Files:** Create `Nitrogen.Runtime/Semantic/DerivedDefinition.cs`, `DerivedBinder.cs`, and `Nitrogen.Workspace/Admission/DerivedDefinitionMapper.cs`; test `Nitrogen.Tests/Experiment/DerivedBinderTests.cs`.

**Interfaces:** `DerivedBinder.Check(DerivedPackageDefinition source, SemanticCatalog imports, IReadOnlySet<string> grantedPureOperationIds)` returns `DerivedCheckResult(CheckedDerivedModule? Module, IReadOnlyList<DerivedDiagnostic> Diagnostics)`. A checked module exposes a `SemanticModule` and immutable typed bodies. `DerivedDiagnostic` contains code, artifact path, span, and message. The Workspace mapper converts the Task 1 package to `DerivedPackageDefinition`; Runtime does not depend on Workspace.

- [ ] **Step 1: Write failing tests.** Check a `Balance.Region` record with `left: Units.Length` and `right: Units.Length`; reject a `Units.Mass` field, missing import, duplicate type ID, wrong argument order, branch type mismatch, and a nominal type that only matches structurally.

```csharp
var result = DerivedBinder.Check(definition, catalog);
Assert.Null(result.Module);
Assert.Contains(result.Diagnostics, d => d.Code == "ND1004" && d.Path == "semantics.json");
```

- [ ] **Step 2: Confirm red.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~DerivedBinderTests`; expect missing binder symbols.
- [ ] **Step 3: Implement binding.** Resolve qualified IDs only through declared imports, register nominal records before checking bodies, require exact field and operation types, and check constructor invariants as typed boolean expressions. Reuse `SemanticCatalog.Compose` for import and duplicate-export checks; map its diagnostics to artifact spans. Do not allow a derived body to request an ungranted host operation.

```csharp
public static DerivedCheckResult Check(DerivedPackageDefinition source,
    SemanticCatalog imports, IReadOnlySet<string> grantedPureOperationIds);
```

- [ ] **Step 4: Confirm green.** Rerun the focused filter and existing `SemanticCatalogTests`; expect exact nominal and unit rejection.
- [ ] **Step 5: Commit.** Stage Task 2 files and commit `Check declarative nominal types and functions`.

### Task 3: Pure aggregate value and HIR slice

**Files:** Create `Nitrogen.Runtime/Semantic/PureValue.cs`, `PureHir.cs`, and `PureEvaluator.cs`; test `Nitrogen.Tests/Experiment/PureEvaluatorTests.cs`.

**Interfaces:** `PureValue` is a closed immutable union of finite number with a `SemanticType`, boolean, nominal record fields, and bounded homogeneous list. `PureOperationRegistry` binds exact `OperationSignature`s to `Func<IReadOnlyList<PureValue>, PureValue>` handlers and rejects non-pure or mismatched signatures. `PureEvaluator.Evaluate(HirNode root, PureOperationRegistry registry, IReadOnlyDictionary<Symbol, PureValue> inputs, PureLimits limits)` returns `PureExecutionResult`; `PureLimits` caps node visits, list length, and call depth. Existing `ExecutionValue` and `HirEvaluator` stay as they are.

- [ ] **Step 1: Write failing tests.** Exercise typed record construction and field access, a conditional that skips the unused branch, a list with a nested invalid number, wrong input-symbol identity, missing binding, host result type mismatch, and node-limit exhaustion. Verify the host invocation counter remains zero after preflight failure.

```csharp
var result = PureEvaluator.Evaluate(root, registry, inputs, new PureLimits(10_000, 256, 32));
Assert.Contains(result.Diagnostics, d => d.Code == "NP0003" && d.Origin.Path == "a.balance");
Assert.Equal(0, hostCalls);
```

- [ ] **Step 2: Confirm red.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~PureEvaluatorTests`; expect missing types.
- [ ] **Step 3: Implement the closed value and HIR cases.** Preflight every reachable node and nested value before invoking a host handler; verify exact signatures and preserve reference identity for symbol inputs. Evaluate only `HirConstant`, `HirSymbolRef`, `HirOperation`, and the new pure nodes. Reject unknown nodes and effects with stable `NP` diagnostics and origins.

```csharp
public sealed record PureLimits(int MaxNodeVisits, int MaxListLength, int MaxCallDepth);
public static PureExecutionResult Evaluate(HirNode root, PureOperationRegistry registry,
    IReadOnlyDictionary<Symbol, PureValue> inputs, PureLimits limits);
```

- [ ] **Step 4: Confirm green.** Rerun focused tests plus `HirEvaluatorTests` and `HirPreflightTests`; expect no numeric-slice regression.
- [ ] **Step 5: Commit.** Stage Task 3 files and commit `Evaluate bounded pure aggregate HIR`.

### Task 4: Lower derived operations with origins

**Files:** Create `Nitrogen.Runtime/Semantic/DerivedCompiler.cs`; modify `Nitrogen.Runtime/Semantic/HirNode.cs` only to support the new pure nodes through traversal; test `Nitrogen.Tests/Experiment/DerivedCompilerTests.cs`.

**Interfaces:** `DerivedCompiler.Compile(CheckedDerivedModule module, SemanticCatalog catalog)` returns immutable HIR templates or diagnostics. `DerivedCompiler.Instantiate(string operationId, IReadOnlyList<HirNode> arguments, SourceOrigin callOrigin)` returns a typed HIR root whose origins contain the call and definition. Expansion is by bound parameter identity, not text replacement.

- [ ] **Step 1: Write failing tests.** Check a two-primitive body, exact argument type rejection, retained definition/call spans, nested reuse, direct and indirect cycles, and stable results when another file defines a same-spelled symbol.

```csharp
var expanded = compiled.Instantiate("Balance.Clearance", arguments, callOrigin);
Assert.Equal(SemanticType.Named("Units", "Length"), expanded.Root!.Type);
Assert.Contains(expanded.Root.Origins, o => o.Path == "semantics.json");
Assert.Contains(expanded.Root.Origins, o => o.Equals(callOrigin));
```

- [ ] **Step 2: Confirm red.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~DerivedCompilerTests`; expect missing compiler symbols.
- [ ] **Step 3: Implement compilation and expansion.** Topologically order derived calls, reject cycles before publication, compile expression tags to typed HIR, and substitute parameters by object identity. Use `HirTraversal.Rewrite` semantics for combined origins and retain exact result types. Bound expansion depth by `PureLimits.MaxCallDepth`.

```csharp
public static DerivedCompileResult Compile(CheckedDerivedModule module, SemanticCatalog catalog);
public DerivedExpansionResult Instantiate(string operationId,
    IReadOnlyList<HirNode> arguments, SourceOrigin callOrigin);
```

- [ ] **Step 4: Confirm green.** Run the focused filter and `GeometryDefinitionExpander` tests; expect origin and cycle behavior without geometry regression.
- [ ] **Step 5: Commit.** Stage Task 4 files and commit `Compile declarative operations to typed HIR`.

### Task 5: Atomic admission of checked semantics

**Files:** Modify `Nitrogen.Workspace/Admission/ModuleAdmissionService.cs`, `HostModuleProfile.cs`, `HostCapabilitySet.cs`, and `Nitrogen.Runtime/ModuleComposer.cs`; test `Nitrogen.Tests/Experiment/DerivedAdmissionTests.cs`.

**Interfaces:** `HostModuleProfile` may opt into derived semantics while still controlling syntax, imported descriptors, pure grants, and the success probe. `AcceptedModuleLease` exposes the checked derived exports and compiled templates. Distinguish host-required primitive operations, which need exact bindings, from derived exports, which expand before evaluation and need no host handler. A package without `semantics.json` follows the current admission path byte for byte.

- [ ] **Step 1: Write failing tests.** Admit a valid derived module with a positive and a wrong-unit negative example; reject an unknown capability, mismatched result, diagnostic mismatch, forbidden C# surface, and failed replacement. Keep an old lease callable after the failed replacement.

```csharp
var accepted = service.Admit(package, profiles);
Assert.True(accepted.Accepted);
using var lease = service.Active!.Acquire();
Assert.Equal(package.Sha256, lease.Module.Sha256);
Assert.False(service.Admit(invalidReplacement, profiles).Accepted);
Assert.NotNull(lease.Composition);
```

- [ ] **Step 2: Confirm red.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~DerivedAdmissionTests`; expect derived admission unavailable.
- [ ] **Step 3: Extend the existing candidate flow.** Load and check the derived file before composition, select exactly requested grants, compile templates, run positive and negative examples through parse/bind/type/lower/preflight/execute, then publish one immutable accepted handle. A failed candidate never changes `Active`; old lease lifetime follows existing retirement rules. Give new stages stable `ND` codes without changing existing `NA` meanings.

```csharp
// Candidate order: policy -> grammar compile -> derived check -> capability select
// -> ModuleComposer -> examples -> atomic publish.
```

- [ ] **Step 4: Confirm green.** Run focused tests and all `Nitrogen.Tests/Workspace/Admission` tests; expect current generated-box admission to remain valid.
- [ ] **Step 5: Commit.** Stage Task 5 files and commit `Admit checked derived modules atomically`.

### Task 6: Frozen one-dimensional host environment

**Files:** Create `Nitrogen.Experiments/Nitrogen.Experiments.csproj`, `PilotEnvironment.cs`, `PilotOracle.cs`, and `Pilot.ngr`; modify `Nitrogen.slnx`; test `Nitrogen.Tests/Experiment/PilotEnvironmentTests.cs` and the test project references.

**Interfaces:** `PilotEnvironment.Create()` returns the fixed `Core`, `Units`, `Geometry`, and `Optimization` descriptors and pure grants. `PilotOracle.Evaluate(PilotCase input)` returns the candidate ID and signed clearance using a separately coded reference calculation. The host-owned grammar parses task programs and imports admitted derived operations by exact ID.

The first host-owned program shape is `run Qualified.Operation(argument, ...);`, where arguments are typed literals, input names, lists, or nested calls. The generic binder resolves `Qualified.Operation` against the composed semantic catalog and reports an unknown or wrong-type call at that source span. The pilot grammar does not bake in `Balance` or the evaluator's target concept names.

- [ ] **Step 1: Write failing tests.** Check the published A examples (`0.7` pass and `-0.1` fail), B examples (`0.12` qualifies and `0.07` fails at `0.10`), boundary equality, reordered contacts, tied candidates by ID, empty candidate list, nonfinite values, zero total mass, and one contact. Assert a catalog scan finds no host `SupportRegion`, `StablePose`, signed-margin, or placement-selector operation.

```csharp
var environment = PilotEnvironment.Create();
Assert.DoesNotContain(environment.Capabilities, c => c.Id.Contains("Margin", StringComparison.Ordinal));
Assert.Equal(0.7, PilotOracle.Clearance([-0.6, 0.2, 0.8], 0.1), 6);
```

- [ ] **Step 2: Confirm red.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~PilotEnvironmentTests`; expect missing project/types.
- [ ] **Step 3: Implement the four-module catalog and pilot grammar.** Use typed `Units.Length` and `Units.Mass`, immutable contact/load/candidate records, finite bounded lists, exact pure signatures, and deterministic candidate tie-breaking. The oracle must not call the derived module under test. Export a canonical catalog JSON and hash for run records.

```csharp
public static PilotEnvironment Create();
public static PilotOutcome Evaluate(PilotCase input);
```

- [ ] **Step 4: Confirm green.** Run the focused filter, `dotnet build Nitrogen.slnx`, and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`; expect all to pass.
- [ ] **Step 5: Commit.** Stage Task 6 files and commit `Add fixed support experiment environment`.

### Task 7: Fresh-session matched runner and records

**Files:** Create `Nitrogen.Experiments/ExperimentRunner.cs`, `RunRecord.cs`, and `AgentSession.cs`; test `Nitrogen.Tests/Experiment/ExperimentRunnerTests.cs`; update [task-suite.md](../../experiments/typed-abstraction-reuse/task-suite.md) with the frozen catalog hash and exact pilot invocation.

**Interfaces:** `IAgentSession.RunAsync(AgentPrompt prompt, CancellationToken cancellationToken)` is supplied by the external model adapter. `ExperimentRunner.RunPairAsync(PilotCase pair, IAgentSessionFactory factory, ExperimentLimits limits)` starts A once, then starts three independent B sessions and returns three `RunRecord`s. Each B session receives only its condition's prompt and read-only artifact view. Each record serializes to [the existing per-condition schema](../../experiments/typed-abstraction-reuse/run-record.schema.json).

- [ ] **Step 1: Write failing tests with a scripted fake session factory.** Assert exactly one A session and three distinct B sessions, no A transcript in any B prompt, no artifact in the two controls, no oracle file access, randomized condition order from a recorded seed, and resolved-call proof only when the imported operation maps to A's accepted hash. Simulate A admission failure and ensure the record reports it without creating a substitute artifact.

```csharp
var records = await ExperimentRunner.RunPairAsync(pair, scriptedFactory, limits, CancellationToken.None);
Assert.Equal(4, scriptedFactory.CreatedCount);
Assert.Equal(3, records.Count);
Assert.All(scriptedFactory.BPrompts, p => Assert.DoesNotContain("A transcript", p.Text));
```

- [ ] **Step 2: Confirm red.** Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ExperimentRunnerTests`; expect missing runner symbols.
- [ ] **Step 3: Implement orchestration and evidence capture.** Keep the model adapter outside the host grants, enforce attempt/time/token budgets, store every source and diagnostic artifact by hash, validate the JSON record against the checked-in schema, and classify B correctness using the independent oracle. Add a command for a local scripted pilot; the model adapter can be supplied by the execution environment without changing Nitrogen's trust boundary.

```csharp
public interface IAgentSessionFactory
{
    IAgentSession StartFresh(string phase, string condition, int seed);
}
public static Task<IReadOnlyList<RunRecord>> RunPairAsync(PilotCase pair,
    IAgentSessionFactory factory, ExperimentLimits limits, CancellationToken cancellationToken);
```

- [ ] **Step 4: Confirm green.** Run the focused filter, the scripted two-pair pilot, `dotnet build Nitrogen.slnx`, and the complete Nitrogen test project. Inspect stored prompts and resolved-call traces. A scripted pilot validates the harness only; do not report it as an agent result.
- [ ] **Step 5: Commit.** Stage Task 7 files and commit `Record matched fresh-session reuse trials`.

## Experiment handoff

After the implementation passes the focused and full gates, freeze model version and adapter, catalog and oracle hashes, generated held-out pair set, prompt text, budgets, and stopping rule. Run two real pilot pairs and exclude them from confirmatory analysis. Revise the protocol only before the confirmatory freeze. Then run twelve held-out pairs across three seeds with artifact, no-carry, and text-only B sessions; publish raw records, paired B success counts, uncertainty intervals, A admission rate, and resolved-call reuse rate. The confirmatory run is a separate measured activity, not a test suite assertion or a claim from the scripted pilot.
