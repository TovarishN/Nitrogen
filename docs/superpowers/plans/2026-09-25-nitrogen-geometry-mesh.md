# Nitrogen Renderable Box Mesh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse, check, lower, inspect, and execute `box 1 2 3;` as an immutable renderable mesh through Gravity's existing box mesh generator.

**Architecture:** A new `Nitrogen.Geometry` library owns the generated grammar, semantic descriptor, typed HIR lowerer, immutable mesh value, and narrow geometry executor. An adapter in `RagdollEditor` provides the exact host delegate and calls its existing `MeshGenerator.GenerateBox`. The numeric evaluator and all production compiler/renderer paths stay unchanged.

**Tech Stack:** .NET/C#, Nitrogen source generator, xUnit, `Nitrogen.Runtime`, `Nitrogen.LanguageService`, `RagdollEditor`.

**Spec:** [2026-09-25-nitrogen-geometry-mesh-design.md](../specs/2026-09-25-nitrogen-geometry-mesh-design.md)

## Global Constraints

- Work on `codex/246-nitrogen-geometry-mesh`, based on issue 245 commit `6fad7ef6`; prefix commits with `[codex/246-nitrogen-geometry-mesh]`.
- Preserve issue 245's finite numeric `ExecutionValue`/`HirEvaluator` APIs. Geometry execution is one explicit mesh result, not a general object-value system.
- Keep Motion, Policy, RagdollEditor, and Game3D production compilation/rendering callers on their existing paths. The new editor dependency is only for the host adapter.
- Input dimensions are full lengths; pass half extents to the existing generator. Return copied, read-only positions, normals, and indices.
- Preserve unrelated untracked work, especially `JpcSharp/docs/superpowers/plans/2026-07-12-held-biped-contact-control.md`.
- Run the new focused Nitrogen adapter tests, the full Nitrogen suite, and `dotnet test Gravity.slnx --filter "Gate!=Extended"`, which includes RagdollEditor.Tests. Compare the seven RagdollEditor failure names with issue 245's log. Run the extended gate only if predictive-control training, `artifacts/`, or velocity-profile files change.
- Do not merge or push without a separate user request. Issues 244 and 245 are unmerged dependencies of this branch.

## File map

- Create `Nitrogen/Nitrogen.Geometry/Nitrogen.Geometry.csproj` and `Geometry.ngr`: generated `.geom` syntax and semantic checks.
- Create `Nitrogen/Nitrogen.Geometry/GeometryValues.cs`: invariant finite-float parsing and positive-dimension predicate used by generated checks.
- Create `Nitrogen/Nitrogen.Geometry/BoxMeshModule.cs`: module descriptor, `Geometry.Mesh` type, and exact `Geometry.BoxMesh` signature.
- Create `Nitrogen/Nitrogen.Geometry/GeometryHirLowerer.cs`: source-linked `HirOperation` with three scalar constants.
- Create `Nitrogen/Nitrogen.Geometry/GeometryMesh.cs`: immutable mesh payload and validation.
- Create `Nitrogen/Nitrogen.Geometry/GeometryExecutor.cs`: preflight, typed host invocation, source diagnostics and result.
- Create `RagdollEditor/GeometryBoxMeshHost.cs`: adapter to existing `MeshGenerator.GenerateBox`.
- Create focused tests under `Nitrogen/Nitrogen.Tests/Geometry/` for each contract and end-to-end inspection.
- Modify `Gravity.slnx`, `Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`, and `RagdollEditor/RagdollEditor.csproj` for project references.
- Update `issues/246-nitrogen-geometry-mesh.md` and `docs/roadmap.md` after verification.

## Review Focus

1. `1e999` may parse as infinity or fail float parsing; it must produce `GE0001` at that literal, never a mesh or an uncaught exception (Task 1).
2. Three scalar inputs make a reordered *type* signature indistinguishable; wrong arity/result must be rejected and swapping numeric values must visibly change the correct mesh axes (Tasks 4–5).
3. A non-finite child in a hand-built HIR root must prevent every host call, even though the generated grammar rejects it earlier (Task 4).
4. A handler returning null or throwing while constructing a mesh with invalid indices/normals must yield `GX0004` at the box origin (Task 4).
5. Editing a valid open `.geom` document to an invalid dimension must invalidate the old inspection root and expose the current `GE0001` location (Task 5).

---

### Task 1: Generate the box syntax and compose its semantic contract

**Files:**
- Create: `Nitrogen/Nitrogen.Geometry/Nitrogen.Geometry.csproj`
- Create: `Nitrogen/Nitrogen.Geometry/Geometry.ngr`
- Create: `Nitrogen/Nitrogen.Geometry/GeometryValues.cs`
- Create: `Nitrogen/Nitrogen.Geometry/BoxMeshModule.cs`
- Create: `Nitrogen/Nitrogen.Tests/Geometry/GeometryModuleTests.cs`
- Modify: `Gravity.slnx`
- Modify: `Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`

**Interfaces:**
- Produces: `BoxMeshModule.MeshType`, `BoxMeshModule.BoxSignature`, `BoxMeshModule.Descriptor` in `Nitrogen.Geometry`.
- Produces generated `Nitrogen.Geometry.Syntax.GeometryModule`, `GeometryKinds`, `BoxNodeSemantics`, `NumNodeSemantics`.
- `BoxMeshModule.Descriptor` initially has the syntax, semantic export, `Document` start rule and required host signature; Task 2 adds its lowering registration.

- [ ] **Step 1: Add only build scaffolding.** Add `Nitrogen.Geometry.csproj` with `Nitrogen.Runtime` and `Nitrogen.Generator` analyzer references and `Geometry.ngr` as an `AdditionalFiles` item with namespace `Nitrogen.Geometry.Syntax`, following `Nitrogen.MotionDsl.csproj`. Add `<Project Path="Nitrogen/Nitrogen.Geometry/Nitrogen.Geometry.csproj" />` to `Gravity.slnx` and `<ProjectReference Include="..\Nitrogen.Geometry\Nitrogen.Geometry.csproj" />` to `Nitrogen.Tests.csproj`. The project can be empty at this point.
- [ ] **Step 2: Write failing tests.** In `GeometryModuleTests`, use `ModuleComposer.Compose([BoxMeshModule.Descriptor], [new HostOperationBinding(BoxMeshModule.BoxSignature, (Func<object?>)(() => null))])`. Parse `box 1 2 3;` through `composed.StartRules[("Geometry", "Document")]`, put the tree into `Project`, and assert no parse, binding, or `ProjectSemantics` diagnostics. Assert `BoxMeshModule.BoxSignature` is `Geometry.BoxMesh(Scalar, Scalar, Scalar) -> Geometry.Mesh`. Use `[Theory]` for `box 0 2 3;`, `box -1 2 3;`, `box 1e999 2 3;`, and `box 1 2 0;`; assert `GE0001` at the bad literal's span. Parse `box 1 nope 3;` and assert a parser diagnostic whose span covers or begins at `nope`.

```csharp
var composed = ModuleComposer.Compose([BoxMeshModule.Descriptor],
    [new HostOperationBinding(BoxMeshModule.BoxSignature, (Func<object?>)(() => null))]);
using var parsed = composed.Language.Parse("box 1 2 3;", composed.StartRules[("Geometry", "Document")]);
Assert.True(parsed.Success);
```

- [ ] **Step 3: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~GeometryModuleTests`. Expected: compile fails because `BoxMeshModule` and generated geometry syntax do not exist.
- [ ] **Step 4: Implement** the grammar, helper, and descriptor. Use standard whitespace/comment trivia from `LanguageBuilder`. Give each `Num` an `Amount` property and `Core.Scalar` hover type, each `Box` a `Geometry.Mesh` hover type, and a separate `GE0001` check at each field. `GeometryValues` lives in `Nitrogen.Geometry.Syntax` so generated semantic code resolves it. Its `Parse` uses invariant `float.TryParse` and returns null for parse failure or non-finite values; `Positive` is true only for finite values above zero. Use the Policy number-token pattern and optional sign:

```ngr
syntax module Geometry
{
  token Digits = ['0'..'9']+;
  token Number = Digits ("." Digits)? (['e' 'E'] ['+' '-']? Digits)?;
  syntax Document = Shape:Box;
  syntax Num = Sign:("+" / "-")? Value:Number
  {
    out hover Type : string = "Core.Scalar";
    out Amount : float? = null;
    Amount = GeometryValues.Parse(Value.Text, Sign?.Text == "-");
  }
  syntax Box = "box" Width:Num Height:Num Depth:Num ";"
  {
    out hover Type : string = "Geometry.Mesh";
    check GE0001 GeometryValues.Positive(Width.Amount) : "box width must be finite and positive" at Width;
    check GE0001 GeometryValues.Positive(Height.Amount) : "box height must be finite and positive" at Height;
    check GE0001 GeometryValues.Positive(Depth.Amount) : "box depth must be finite and positive" at Depth;
  }
}
```

`GeometryValues.Parse` should follow this guard, then `BoxMeshModule` publishes the exact contract:

```csharp
public static float? Parse(string text, bool negative) =>
    float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
    float.IsFinite(value) ? (negative ? -value : value) : null;
public static bool Positive(float? value) => value is float number && float.IsFinite(number) && number > 0f;
```

```csharp
public static readonly SemanticType MeshType = SemanticType.Named("Geometry", "Mesh");
public static readonly OperationSignature BoxSignature = new("Geometry.BoxMesh", MeshType,
    SemanticTypes.Scalar, SemanticTypes.Scalar, SemanticTypes.Scalar);
public static readonly ModuleDescriptor Descriptor = new("Geometry", GeometryModule.Instance,
    new SemanticModule("Geometry", [], [MeshType], [BoxSignature]),
    ["Document"], [BoxSignature]);
```

- [ ] **Step 5: Rerun** the Task 1 filter. Expected: all valid/invalid grammar and semantic cases pass; no generated-code or analyzer errors. Commit Task 1 files with `[codex/246-nitrogen-geometry-mesh] Define checked geometry module`.

### Task 2: Lower a checked box with exact source origins

**Files:**
- Create: `Nitrogen/Nitrogen.Geometry/GeometryHirLowerer.cs`
- Create: `Nitrogen/Nitrogen.Tests/Geometry/GeometryHirLowererTests.cs`
- Modify: `Nitrogen/Nitrogen.Geometry/BoxMeshModule.cs`

**Interfaces:**
- Consumes: `BoxMeshModule.BoxSignature`, generated `GeometryKinds.Box` and `BoxNodeSemantics`/`NumNodeSemantics` from Task 1.
- Produces: `GeometryHirLowerer.Registration`, added to `BoxMeshModule.Descriptor.Semantics.Lowerers`.

- [ ] **Step 1: Write failing tests.** Compose the descriptor with `new HostOperationBinding(BoxMeshModule.BoxSignature, (Func<object?>)(() => null))`, parse/bind/check `box 1 2 3;`, call `HirLowering.Lower(file, composed.Language.SemanticCatalog, fixedSnapshotId)`, and assert one `HirOperation` with `BoxSignature`, three scalar constants in width/height/depth order, values `1, 2, 3`, literal spans, one full-statement root span and the supplied snapshot ID. For `box 0 2 3;`, assert no roots and a source-linked lowering diagnostic. Assert a recovered malformed document cannot lower a root.

```csharp
var root = Assert.IsType<HirOperation>(Assert.Single(lowered.Roots));
Assert.Equal(BoxMeshModule.BoxSignature, root.Signature);
Assert.Equal([1f, 2f, 3f], root.Arguments.Select(node => Assert.IsType<HirConstant>(node).Value));
```

- [ ] **Step 2: Run** the `FullyQualifiedName~GeometryHirLowererTests` filter. Expected: missing lowering registration makes the valid sample's root assertion fail.
- [ ] **Step 3: Implement** `GeometryHirLowerer.Registration = new(GeometryKinds.Box, BoxMeshModule.BoxSignature.Id, Lower)`. Read the generated `BoxNodeSemantics` fields in width/height/depth order. Locate each `Num` syntax node by its known `Span` and `GeometryKinds.Num` as `PolicyHirLowerer` does; use `context.Origin(node)` for the literal and `context.Origin(boxNode)` for the root. Build the `HirOperation` with three `HirConstant`s only when each `Amount` is finite and positive. Add the registration to `BoxMeshModule`'s `SemanticModule` construction. Do not bypass `HirLowering`'s semantic-diagnostic gate.

```csharp
static HirNode? Lower(LoweringContext context, int boxNode)
{
    var box = new BoxNodeSemantics(context.File, boxNode);
    var parts = new[] { box.Width, box.Height, box.Depth };
    if (parts.Any(part => !GeometryValues.Positive(part.Amount))) return null;
    var tree = context.File.Tree;
    var constants = parts.Select(part =>
    {
        var node = Enumerable.Range(0, tree.NodeCount).Single(candidate =>
            tree.Kind(candidate) == GeometryKinds.Num && tree.Span(candidate) == part.Span);
        return (HirNode)new HirConstant(part.Amount!.Value, SemanticTypes.Scalar, context.Origin(node));
    }).ToArray();
    return new HirOperation(BoxMeshModule.BoxSignature, constants, [context.Origin(boxNode)]);
}
```
- [ ] **Step 4: Rerun** the Task 2 filter plus `GeometryModuleTests`; expected all pass. Commit with `[codex/246-nitrogen-geometry-mesh] Lower checked box dimensions`.

### Task 3: Define an immutable renderable mesh value

**Files:**
- Create: `Nitrogen/Nitrogen.Geometry/GeometryMesh.cs`
- Create: `Nitrogen/Nitrogen.Tests/Geometry/GeometryMeshTests.cs`

**Interfaces:**
- Produces: `readonly record struct MeshVertex(Vector3 Position, Vector3 Normal)` and `GeometryMesh(IEnumerable<MeshVertex> vertices, IEnumerable<ushort> indices)` with read-only `Vertices` and `Indices`.

- [ ] **Step 1: Write failing tests.** Construct a one-triangle mesh and mutate both source arrays afterward; the returned lists must retain original values and reject `IList<T>` mutation. Assert constructor failure for null lists, zero vertices, zero indices, index count not divisible by three, out-of-range indices, and NaN/infinite position or normal components. Assert that a legal repeated index and a finite zero-length normal are accepted; normalization is not part of this value's contract.

```csharp
var vertices = new[] { new MeshVertex(Vector3.Zero, Vector3.UnitZ),
    new MeshVertex(Vector3.UnitX, Vector3.UnitZ), new MeshVertex(Vector3.UnitY, Vector3.UnitZ) };
var mesh = new GeometryMesh(vertices, new ushort[] { 0, 1, 2 });
Assert.Equal(3, mesh.Vertices.Count);
```

- [ ] **Step 2: Run** the `FullyQualifiedName~GeometryMeshTests` filter. Expected: missing `GeometryMesh` and `MeshVertex` compile errors.
- [ ] **Step 3: Implement** copied arrays behind `Array.AsReadOnly`, finite checks for all six vector components per vertex, nonempty vertices, triangular index count, and `index < vertexCount` for every index. `GeometryMesh` exposes no mutable arrays. Keep the type independent of `RagdollEditor.Vertex3D`.

```csharp
var copiedVertices = vertices.ToArray();
var copiedIndices = indices.ToArray();
if (copiedVertices.Length == 0 || copiedIndices.Length == 0 || copiedIndices.Length % 3 != 0)
    throw new ArgumentException("A mesh needs vertices and triangle indices.");
if (copiedIndices.Any(index => index >= copiedVertices.Length))
    throw new ArgumentException("A triangle index is outside the vertex list.", nameof(indices));
Vertices = Array.AsReadOnly(copiedVertices);
Indices = Array.AsReadOnly(copiedIndices);
```
- [ ] **Step 4: Rerun** the Task 3 filter; expected all pass. Commit with `[codex/246-nitrogen-geometry-mesh] Define immutable mesh result`.

### Task 4: Preflight and execute one geometry operation

**Files:**
- Create: `Nitrogen/Nitrogen.Geometry/GeometryExecutor.cs`
- Create: `Nitrogen/Nitrogen.Tests/Geometry/GeometryExecutorTests.cs`

**Interfaces:**
- Consumes: Task 1 `BoxMeshModule.BoxSignature`, Task 3 `GeometryMesh`, issue 244 `ModuleComposition.HostBindings`, issue 245 `ExecutionValue` and `ExecutionDiagnostic`.
- Produces: `GeometryExecutor.Execute(HirNode root, ModuleComposition composition) -> GeometryExecutionResult` with `GeometryMesh? Mesh`, copied `Origins`, copied `Diagnostics`.
- Diagnostic codes: `GX0001` invalid box root/catalog signature, `GX0002` missing or wrongly typed host binding, `GX0003` non-finite or non-scalar child, `GX0004` thrown handler or null mesh.

- [ ] **Step 1: Write failing tests.** Use Task 2's lowered root and a `ModuleComposer` composition with a typed `Func<IReadOnlyList<ExecutionValue>, GeometryMesh>` handler. Assert argument order `1,2,3`, read-only argument list, one handler call, returned mesh identity, and copied root origins. Hand-build a root with a NaN scalar child and verify `GX0003` at that child and zero handler calls. Build an alternate one-argument or Scalar-result signature and assert `GX0001`; use a custom descriptor with the same syntax/semantics but `requiredOperations: []` and compose with no binding to assert `GX0002`. A wrong delegate shape also gives `GX0002`. Null return, explicit thrown exception, and a `GeometryMesh` constructor exception from inside a handler give `GX0004` at the root. Test `ModuleComposer.TryCompose` rejects a host signature with changed arity/result before execution.

```csharp
Func<IReadOnlyList<ExecutionValue>, GeometryMesh> host = values =>
{
    Assert.Equal([1f, 2f, 3f], values.Select(value => value.Number));
    return triangle;
};
var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor],
    [new HostOperationBinding(BoxMeshModule.BoxSignature, host)]);
var result = GeometryExecutor.Execute(root, composition);
Assert.Empty(result.Diagnostics);
Assert.Same(triangle, result.Mesh);
```

- [ ] **Step 2: Run** the `FullyQualifiedName~GeometryExecutorTests` filter. Expected: missing executor/result compile errors.
- [ ] **Step 3: Implement** deterministic preflight of the single `Geometry.BoxMesh` root and all three children before invoking the handler. Compare the operation against both `BoxMeshModule.BoxSignature` and `composition.Language.SemanticCatalog.Operations`, fetch `composition.HostBindings[BoxSignature.Id]`, check full binding signature and exact delegate type, then construct ordered finite `ExecutionValue` arguments. Only then invoke the handler inside a `try/catch`, converting a throw or null to `GX0004`. Return copied root origins and one `ExecutionDiagnostic` for a failure. The handler receives `Array.AsReadOnly(arguments)`.

```csharp
if (root is not HirOperation operation || !operation.Signature.Equals(BoxMeshModule.BoxSignature) ||
    !composition.Language.SemanticCatalog.Operations.TryGetValue(BoxMeshModule.BoxSignature.Id, out var exported) ||
    !exported.Equals(operation.Signature))
    return Failure("GX0001", root.Origins[0], root.Origins);
if (!composition.HostBindings.TryGetValue(BoxMeshModule.BoxSignature.Id, out var binding) ||
    !binding.Signature.Equals(operation.Signature) ||
    binding.Handler is not Func<IReadOnlyList<ExecutionValue>, GeometryMesh> handler)
    return Failure("GX0002", root.Origins[0], root.Origins);
var arguments = new ExecutionValue[3];
for (var i = 0; i < arguments.Length; i++)
{
    if (operation.Arguments[i] is not HirConstant constant ||
        !constant.Type.Equals(SemanticTypes.Scalar) || !float.IsFinite(constant.Value))
        return Failure("GX0003", operation.Arguments[i].Origins[0], root.Origins);
    arguments[i] = new ExecutionValue(SemanticTypes.Scalar, constant.Value);
}
try
{
    var mesh = handler(Array.AsReadOnly(arguments));
    return mesh is null ? Failure("GX0004", root.Origins[0], root.Origins)
        : new GeometryExecutionResult(mesh, root.Origins, []);
}
catch (Exception)
{
    return Failure("GX0004", root.Origins[0], root.Origins);
}
```

The private `Failure(code, origin, origins)` helper constructs `new GeometryExecutionResult(null, origins, [new ExecutionDiagnostic(code, origin, message)])` with a clear message. The result constructor copies both collections with `Array.AsReadOnly(...ToArray())` as `ExecutionResult` does.
- [ ] **Step 4: Rerun** the Task 4 filter and Task 1–3 filters; expected all pass. Commit with `[codex/246-nitrogen-geometry-mesh] Execute bound box mesh operation`.

### Task 5: Adapt the existing renderer mesh and prove the full pipeline

**Files:**
- Create: `RagdollEditor/GeometryBoxMeshHost.cs`
- Create: `Nitrogen/Nitrogen.Tests/Geometry/GeometryIntegrationTests.cs`
- Modify: `RagdollEditor/RagdollEditor.csproj`
- Modify: `Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`
- Modify: `issues/246-nitrogen-geometry-mesh.md`
- Modify: `docs/roadmap.md`

**Interfaces:**
- Consumes: `BoxMeshModule.Descriptor/BoxSignature`, `GeometryMesh`, `GeometryExecutor`, `NitrogenLanguageService`.
- Produces: `GeometryBoxMeshHost.Binding` in `Gravity.RagdollEditor`, backed by `MeshGenerator.GenerateBox`.

- [ ] **Step 1: Add only project references** from `RagdollEditor` to `Nitrogen.Geometry` and from `Nitrogen.Tests` to `RagdollEditor`; do not add adapter code yet.
- [ ] **Step 2: Write failing integration tests.** Compose with `GeometryBoxMeshHost.Binding`. Parse, `Project.Set`, check `ProjectSemantics` diagnostics, lower and execute `box 1 2 3;`. Compare all 24 vertices and 36 indices against `MeshGenerator.GenerateBox(new Vector3(0.5f, 1f, 1.5f))`, including normals and axis extents; assert source origins. Create `LanguageRegistry` for `.geom` and `NitrogenLanguageService`, open the same sample, assert `InspectDocument` has one mesh root and `Inspect` at a dimension returns a Scalar child with the literal range. Change to `box 0 2 3;`, assert no current roots and `GE0001` at `0`. For a `box 3 2 1;` sample, assert X/Z extents swap, proving numeric argument order.

```csharp
var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);
var result = GeometryExecutor.Execute(root, composition);
Assert.Equal(24, result.Mesh!.Vertices.Count);
Assert.Equal(36, result.Mesh.Indices.Count);
```

- [ ] **Step 3: Run** the `FullyQualifiedName~GeometryIntegrationTests` filter. Expected: compile fails because `GeometryBoxMeshHost` does not exist.
- [ ] **Step 4: Implement** the adapter. `GeometryBoxMeshHost.Binding` holds a typed delegate; its handler divides full lengths by two, calls `MeshGenerator.GenerateBox(new Vector3(...))`, maps each `Vertex3D` to `MeshVertex`, and constructs `GeometryMesh` from copied indices. Keep existing renderer callers unchanged.

```csharp
public static readonly HostOperationBinding Binding = new(BoxMeshModule.BoxSignature,
    (Func<IReadOnlyList<ExecutionValue>, GeometryMesh>)(values =>
    {
        var halfExtents = new Vector3(values[0].Number, values[1].Number, values[2].Number) * 0.5f;
        var (vertices, indices) = MeshGenerator.GenerateBox(halfExtents);
        return new GeometryMesh(vertices.Select(vertex => new MeshVertex(vertex.Position, vertex.Normal)), indices);
    }));
```
- [ ] **Step 5: Rerun** the integration filter; then run `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`, `dotnet test Gravity.slnx --filter "Gate!=Extended"`, and `git diff --check`. The fast gate builds and runs RagdollEditor.Tests; no existing test directly covers `MeshGenerator`, so the new Nitrogen integration test is the focused adapter test. Compare the seven RagdollEditor failure names with issue 245's final list. Record exact counts, any baseline-only failures, and whether the extended gate was triggered in issue 246. Update the roadmap status, then commit with `[codex/246-nitrogen-geometry-mesh] Prove renderable box mesh pipeline`.

## Completion review

- [ ] Compare the branch against each spec section: generated module, typed checks/lowering, exact host preflight, immutable mesh, existing generator parity, and inspection of the same sample.
- [ ] Review the whole branch diff for unintended editor production-path changes and unrelated files. A fresh reviewer checks the whole branch if available; record findings and red-green fixes in issue 246.
- [ ] Keep the branch unmerged and unpushed; its base includes unmerged issues 244 and 245.
