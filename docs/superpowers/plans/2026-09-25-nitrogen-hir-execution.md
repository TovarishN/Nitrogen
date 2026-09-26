# Nitrogen Typed HIR Execution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Evaluate one finite numeric Nitrogen HIR slice through exactly bound host operations and explicit symbol inputs, returning source-linked results or diagnostics.

**Architecture:** Keep issue 244's `HostOperationBinding` intact. A new registry checks that each supplied binding matches the semantic catalog and has a typed numeric delegate. An evaluator preflights a HIR root, then interprets constants, symbol references, and operations using the frozen registry; Motion and Policy remain on their current production compilers.

**Tech Stack:** .NET/C#, xUnit, `Nitrogen.Runtime`, `Nitrogen.MotionDsl`, `Nitrogen.Tests`.

**Spec:** [2026-09-25-nitrogen-hir-execution-design.md](../specs/2026-09-25-nitrogen-hir-execution-design.md)

## Global Constraints

- Work on `codex/245-nitrogen-hir-execution`, based on issue 244 commit `5daa8b16`; prefix commits with `[codex/245-nitrogen-hir-execution]`.
- Keep `HostOperationBinding`, `.ngr` syntax, generated parser code, Motion/Policy compilers, and Game3D runtime callers compatible.
- Execute only finite `Core.Scalar` and `Units.Angle` float values with trusted, pure C# handlers. Do not add a general object value model.
- Do not invoke a handler during registry binding or when HIR preflight reports an error.
- Preserve unrelated untracked work, especially `JpcSharp/docs/superpowers/plans/2026-07-12-held-biped-contact-control.md`.
- Run the full Nitrogen suite and `dotnet test Gravity.slnx --filter "Gate!=Extended"`; compare the seven RagdollEditor baseline failures from issue 244 by test name.

## File map

- Create `Nitrogen/Nitrogen.Runtime/Semantic/ExecutionValue.cs`: supported typed numeric value and immutable evaluation result/diagnostic records.
- Create `Nitrogen/Nitrogen.Runtime/Semantic/HostOperationRegistry.cs`: exact catalog/delegate validation and frozen handler lookup.
- Create `Nitrogen/Nitrogen.Runtime/Semantic/HirPreflight.cs`: source-linked validation before any handler runs.
- Create `Nitrogen/Nitrogen.Runtime/Semantic/HirEvaluator.cs`: recursive numeric evaluation and handler error containment.
- Create `Nitrogen/Nitrogen.Tests/Semantic/ExecutionValueTests.cs`: type, finiteness, and input invariants.
- Create `Nitrogen/Nitrogen.Tests/Semantic/HostOperationRegistryTests.cs`: binding contract and determinism.
- Create `Nitrogen/Nitrogen.Tests/Semantic/HirPreflightTests.cs`: operation, constant, and input validation before execution.
- Create `Nitrogen/Nitrogen.Tests/Semantic/HirEvaluatorTests.cs`: recursion, result origins, and handler behavior.
- Create `Nitrogen/Nitrogen.Tests/Semantic/HirExecutionIntegrationTests.cs`: dynamic Motion and Policy lowering-to-execution proofs.
- Update `issues/245-nitrogen-hir-execution.md` and `docs/roadmap.md` only after verified status changes.

## Review Focus

These input classes are easy to miss; the listed tasks must test them:

1. A binding with the correct operation ID and return type but reversed input types must fail before execution (Task 2).
2. A preflight failure in a nested operation or symbol must prevent every handler call in the root (Task 3).
3. A symbol from another parse with the same name must not satisfy the current HIR reference (Task 3).
4. A handler that returns null, a wrong type, or a non-finite value must yield an origin-bearing diagnostic (Task 4).
5. A rewritten root with multiple `SourceOrigin`s must return all those origins on success (Task 4).

---

### Task 1: Define finite typed values and result shapes

**Files:**
- Create: `Nitrogen/Nitrogen.Runtime/Semantic/ExecutionValue.cs`
- Create: `Nitrogen/Nitrogen.Tests/Semantic/ExecutionValueTests.cs`

**Interfaces:**
- Produces: `ExecutionValue(SemanticType type, float number)` with `Type` and `Number`.
- Produces: `ExecutionDiagnostic(string Code, SourceOrigin Origin, string Message)` and `ExecutionResult(ExecutionValue? Value, IReadOnlyList<SourceOrigin> Origins, IReadOnlyList<ExecutionDiagnostic> Diagnostics)`.

- [ ] **Step 1: Write failing tests.** Assert structural angle/scalar types and exact finite values survive construction; null type, `Core.Bool`, `Core.Error`, `NaN`, and both infinities throw argument exceptions. Assert `ExecutionResult` copies origin and diagnostic lists so caller mutation cannot change a returned result.

```csharp
var angle = new ExecutionValue(SemanticType.Named("Units", "Angle"), 0.5f);
Assert.Equal(SemanticTypes.Angle, angle.Type);
Assert.Equal(0.5f, angle.Number);
Assert.Throws<ArgumentException>(() => new ExecutionValue(SemanticTypes.Bool, 1));
Assert.Throws<ArgumentException>(() => new ExecutionValue(SemanticTypes.Angle, float.NaN));
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ExecutionValueTests`. Expect missing-type compile errors.
- [ ] **Step 3: Implement** constructor validation and copied read-only result collections. Keep these as data types; no evaluator logic in this file.

```csharp
if (!type.Equals(SemanticTypes.Scalar) && !type.Equals(SemanticTypes.Angle))
    throw new ArgumentException("Only finite scalar and angle values are executable.", nameof(type));
if (!float.IsFinite(number)) throw new ArgumentException("A finite number is required.", nameof(number));
```

- [ ] **Step 4: Rerun** the Task 1 filter and confirm every test passes.
- [ ] **Step 5: Commit** Task 1 files with `[codex/245-nitrogen-hir-execution] Define numeric execution values`.

### Task 2: Bind typed handlers to exact catalog signatures

**Files:**
- Create: `Nitrogen/Nitrogen.Runtime/Semantic/HostOperationRegistry.cs`
- Create: `Nitrogen/Nitrogen.Tests/Semantic/HostOperationRegistryTests.cs`

**Interfaces:**
- Consumes: `SemanticCatalog`, `HostOperationBinding`, `ExecutionValue`, `OperationSignature`.
- Produces: `HostOperationRegistry.TryBind(SemanticCatalog catalog, IEnumerable<HostOperationBinding> bindings, out HostOperationRegistry? registry, out IReadOnlyList<CompositionDiagnostic> diagnostics)` and `Bind` throwing `SemanticCompositionException`.
- Produces: internal `TryGet(string operationId, out OperationSignature signature, out Func<IReadOnlyList<ExecutionValue>, ExecutionValue> handler)` for Task 3. `Catalog` remains public for HIR preflight.
- Binding diagnostic codes: `NR0001` absent catalog export, `NR0002` incompatible signature, `NR0003` wrong delegate shape, `NR0004` incompatible duplicate ID.

- [ ] **Step 1: Write failing tests.** Compose a small catalog from `SemanticModule("Test", [], [], [signature])`. Test an exact typed handler, missing export, reordered inputs, changed result, wrong delegate shape, two different handlers for one ID, equivalent duplicate records sharing one handler, and reversed binding order yielding identical diagnostics. Verify throwing `Bind` exposes the same structured diagnostics as `TryBind`. A side-effect counter must stay zero during binding.

```csharp
var signature = new OperationSignature("Test.Add", SemanticTypes.Angle,
    SemanticTypes.Scalar, SemanticTypes.Angle);
Func<IReadOnlyList<ExecutionValue>, ExecutionValue> add = args =>
    new(SemanticTypes.Angle, args[0].Number + args[1].Number);
var binding = new HostOperationBinding(signature, add);
Assert.True(HostOperationRegistry.TryBind(catalog, [binding], out var registry, out var errors));
Assert.Empty(errors);
Assert.NotNull(registry);
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~HostOperationRegistryTests`. Expect missing-registry compile errors.
- [ ] **Step 3: Implement** an ordinal ID map. Validate each supplied signature against `catalog.Operations[id]`, require the exact `Func<IReadOnlyList<ExecutionValue>, ExecutionValue>` delegate shape, and coalesce duplicates only when both structural signature and handler reference match. Sort diagnostics by code and operation ID. Copy the completed map into a read-only dictionary; never call a handler while binding.

```csharp
if (binding.Handler is not Func<IReadOnlyList<ExecutionValue>, ExecutionValue> handler)
    errors.Add(new CompositionDiagnostic("NR0003", [],
        $"Host operation '{binding.Signature.Id}' needs a numeric execution handler."));
```

- [ ] **Step 4: Rerun** the Task 2 filter; check `ModuleComposerTests` as the compatibility boundary.
- [ ] **Step 5: Commit** Task 2 files with `[codex/245-nitrogen-hir-execution] Bind typed host operations`.

### Task 3: Preflight HIR operations and symbol inputs

**Files:**
- Create: `Nitrogen/Nitrogen.Runtime/Semantic/HirPreflight.cs`
- Create: `Nitrogen/Nitrogen.Tests/Semantic/HirPreflightTests.cs`

**Interfaces:**
- Consumes: `HostOperationRegistry.Catalog/TryGet`, `HirTraversal.PreOrder`, `Nitrogen.Binding.Symbol`, `ExecutionValue`, and Task 1 result types.
- Produces: `HirPreflight.Check(HirNode root, HostOperationRegistry registry, IReadOnlyDictionary<Symbol, ExecutionValue> inputs)` returning read-only diagnostics for Task 4's evaluator to consume.
- Preflight codes: `NE0001` unsupported/non-finite constant or node, `NE0002` missing symbol input, `NE0003` wrong symbol input type, `NE0004` operation differs from catalog export, `NE0005` missing bound operation.

- [ ] **Step 1: Write failing tests.** Use hand-built HIR with distinct `SourceOrigin`s. Test a missing symbol, wrong input type, missing inner handler with a bound outer handler, a HIR signature different from the catalog signature, a non-finite constant, and an unsupported HIR node subclass. Assert exact diagnostic origin and zero handler calls for each failure. Build two parsed Motion documents with the same `gain` name and show that an input keyed by the first document's `Symbol` does not satisfy the second HIR reference.

```csharp
var root = new HirOperation(outerSignature,
    [new HirOperation(innerSignature, [new HirConstant(1, SemanticTypes.Angle, childOrigin)],
        [innerOrigin])], [outerOrigin]);
var diagnostics = HirPreflight.Check(root, registryWithOnlyOuter, new Dictionary<Symbol, ExecutionValue>());
Assert.Contains(diagnostics, diagnostic =>
    diagnostic.Code == "NE0005" && diagnostic.Origin == innerOrigin);
Assert.Equal(0, outerCalls);
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~HirPreflightTests`. Expect missing-preflight compile errors.
- [ ] **Step 3: Implement** deterministic preorder preflight. For constants check supported finite number, for references use symbol object identity and exact type, and for operations check equality against the registry catalog and presence of a bound handler. Return all safely found preflight diagnostics; do not invoke handlers in this phase. Unknown HIR node classes receive `NE0001` at their origin.

```csharp
foreach (var node in HirTraversal.PreOrder(root))
    switch (node)
    {
        case HirSymbolRef reference when !inputs.TryGetValue(reference.Symbol.Binding, out _):
            errors.Add(new ExecutionDiagnostic("NE0002", reference.Origins[0],
                $"No input was supplied for '{reference.Symbol.Binding.Name}'."));
            break;
        case HirOperation operation when !registry.Catalog.Operations.TryGetValue(operation.Signature.Id, out var exported)
                                         || !exported.Equals(operation.Signature):
            errors.Add(new ExecutionDiagnostic("NE0004", operation.Origins[0],
                $"Operation '{operation.Signature.Id}' differs from the catalog."));
            break;
    }
```

- [ ] **Step 4: Rerun** the Task 3 filter and confirm the preflight cases pass. The result for a valid root is an empty diagnostic list.
- [ ] **Step 5: Commit** Task 3 files with `[codex/245-nitrogen-hir-execution] Preflight HIR execution`.

### Task 4: Evaluate numeric HIR and contain handler errors

**Files:**
- Create: `Nitrogen/Nitrogen.Runtime/Semantic/HirEvaluator.cs`
- Create: `Nitrogen/Nitrogen.Tests/Semantic/HirEvaluatorTests.cs`

**Interfaces:**
- Consumes: `ExecutionValue`, `HostOperationRegistry.TryGet`, and `HirPreflight.Check`.
- Produces: `HirEvaluator.Evaluate(HirNode root, HostOperationRegistry registry, IReadOnlyDictionary<Symbol, ExecutionValue> inputs)`.
- Produces: a successful `ExecutionResult.Value` and root `Origins`; `NE0006` for a thrown handler and `NE0007` for a null or incompatible result. Non-finite returns constructed inside a handler surface as `NE0006` because `ExecutionValue` rejects non-finite numbers.

- [ ] **Step 1: Write failing tests.** Check a standalone angle constant, a typed symbol reference, a nested add/identity operation and argument order, a read-only handler argument list, all retained origins after `HirTraversal.Rewrite`, an exception thrown by a handler, null result, wrong result type, and a handler attempting `new ExecutionValue(SemanticTypes.Angle, float.PositiveInfinity)`. Check exact result/diagnostic values and origins. A failed child must prevent its parent's handler call.

```csharp
var value = HirEvaluator.Evaluate(new HirConstant(0.25f, SemanticTypes.Angle, origin),
    registry, new Dictionary<Symbol, ExecutionValue>());
Assert.Empty(value.Diagnostics);
Assert.Equal(0.25f, value.Value!.Number);
Assert.Equal([origin], value.Origins);
```

- [ ] **Step 2: Run** the Task 4 filter. Expect missing-evaluator compile errors while preflight tests stay green.
- [ ] **Step 3: Implement** postorder recursive evaluation after successful preflight. Copy arguments into a read-only list before calling the typed handler. Catch handler exceptions at that operation's origin, reject null or wrong-type returns, stop at the first failed child, and return the root's copied `Origins` on success.

```csharp
try
{
    var value = handler(Array.AsReadOnly(arguments));
    if (value is null || !value.Type.Equals(operation.Signature.Result))
        return Failure("NE0007", operation.Origins[0], "Host result does not match the operation result type.");
    return Success(value);
}
catch (Exception error)
{
    return Failure("NE0006", operation.Origins[0],
        $"Host operation '{operation.Signature.Id}' failed: {error.GetType().Name}: {error.Message}");
}
```

- [ ] **Step 4: Rerun** the Task 3 and Task 4 filters. Expect all preflight and evaluator tests to pass.
- [ ] **Step 5: Commit** Task 4 files with `[codex/245-nitrogen-hir-execution] Evaluate numeric HIR with source diagnostics`.

### Task 5: Prove Motion and Policy execution, then verify the repository

**Files:**
- Create: `Nitrogen/Nitrogen.Tests/Semantic/HirExecutionIntegrationTests.cs`
- Modify: `issues/245-nitrogen-hir-execution.md`
- Modify: `docs/roadmap.md`

**Interfaces:**
- Consumes: `NitrogenMotionParser.Language`, `NitrogenPolicyParser.Language`, `HirLowering.Lower`, `HostOperationRegistry.Bind`, and `HirEvaluator.Evaluate`.
- Produces: a parsed/lowered/executed Motion track and Policy clamp, with numeric and source-origin assertions.

- [ ] **Step 1: Write failing end-to-end tests.** Follow `AngleProofTests` to parse `MotionTypingTests.Skill(MotionTypingTests.Track("gain + 1deg"))`, create `Project` and `ProjectSemantics`, lower, and select the `Motion.AngleSlot` root. Bind typed handlers for `Motion.AddScalarAngle` and `Motion.AngleSlot`; supply `0.5f` keyed by the lowered reference's `Symbol.Binding`. Assert an `Units.Angle` result within `1e-5f` of `0.5f + MathF.PI / 180f` and the root source origin. Use `PolicyValueChecksTests.Reference` with `clamp knee 0.2`, lower its `Policy.Clamp` root, bind an identity handler, and assert `0.2f` radians with no symbol inputs. Before implementing any integration-only adapter, run the filter and confirm the intended assertion fails; if the already implemented API makes the tests pass immediately, record that the integration test is a verification case and add no product adapter.

```csharp
var reference = Assert.IsType<HirSymbolRef>(HirTraversal.PreOrder(motionRoot).Single(node => node is HirSymbolRef));
var inputs = new Dictionary<Symbol, ExecutionValue>
{
    [reference.Symbol.Binding] = new(SemanticTypes.Scalar, 0.5f)
};
var result = HirEvaluator.Evaluate(motionRoot, registry, inputs);
Assert.Empty(result.Diagnostics);
Assert.InRange(MathF.Abs(result.Value!.Number - (0.5f + MathF.PI / 180f)), 0f, 1e-5f);
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~HirExecutionIntegrationTests`. Check the actual result and diagnose any failure at its boundary; do not change production behavior to fit an incorrect fixture.
- [ ] **Step 3: Make only necessary narrow fixes** revealed by the real integration tests. For each product fix, preserve a failing test before the code change. Keep existing Motion/Policy compiler paths intact.
- [ ] **Step 4: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`, `dotnet test Gravity.slnx --filter "Gate!=Extended"`, and `git diff --check`. Record counts and compare the RagdollEditor failure names with issue 244's baseline. Run `dotnet test Gravity.slnx -p:RunExtendedTests=true` only if predictive-control training, `artifacts/`, or velocity-profile files changed.
- [ ] **Step 5: Update** the issue and roadmap with exact verification and branch status, then commit Task 5 files with `[codex/245-nitrogen-hir-execution] Prove typed Motion and Policy execution`.

## Completion review

- [ ] Compare the branch against the approved spec section by section, including root origins, preflight behavior, and no compiler/runtime path switch.
- [ ] Review the whole branch diff for signature loopholes, snapshot-safe symbol matching, source diagnostics, and unrelated changes.
- [ ] Record review findings and fixes in the issue. Do not merge or push without a separate user request; issue 245 depends on issue 244.
