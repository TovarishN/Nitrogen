# Nitrogen box mesh module — design

**Issue:** 246. **Date:** 2026-09-25. **Status:** approved and implemented on issue branch.
**Base:** `codex/245-nitrogen-hir-execution` at `6fad7ef6`; issues 244 and 245 are not merged into `master`.

## 1. Purpose and acceptance sample

Milestone 3 of [the Nitrogen roadmap](../../roadmap.md) proves that a separately authored Nitrogen module can produce a non-ragdoll, renderable geometry value. The sample is `box 1 2 3;` in a `.geom` document. Those dimensions are full lengths in the existing renderer's coordinate units. Successful execution yields a box mesh with positions, normals, and triangle indices equivalent to `Gravity.RagdollEditor.MeshGenerator.GenerateBox(new Vector3(0.5f, 1f, 1.5f))`: 24 face vertices and 36 indices. The same sample must parse, bind, pass semantic checks, lower to typed HIR, execute through a bound host adapter, and expose its mesh type and source span through Nitrogen language-service inspection.

The module is a proof and a reusable starting point for milestone 4. It does not switch any Motion, Policy, RagdollEditor, or Game3D production compiler or renderer path. No GPU upload or screenshot is needed to establish this contract; tests compare the returned renderable buffers with the existing generator.

## 2. Module boundary and grammar

Create a small `Nitrogen.Geometry` library beside `Nitrogen.MotionDsl`. Its generated `Geometry.ngr` syntax has one start rule, `Document`, containing one `Box` statement. The first grammar accepts only `box <number> <number> <number>;`, with decimal/scientific float literals and optional signs. It has no names, references, definitions, transform, or repeat syntax yet. The three dimensions are semantically `Core.Scalar`; the box expression is `Geometry.Mesh`. A `GE0001` check reports a non-finite or non-positive dimension at that literal's span. Syntax recovery diagnostics remain the parser's responsibility. The lowerer must not produce a root for an invalid document.

The library publishes one `ModuleDescriptor` containing generated syntax, a `SemanticModule` that exports `SemanticType.Named("Geometry", "Mesh")` and `Geometry.BoxMesh(Scalar, Scalar, Scalar) -> Geometry.Mesh`, the `Document` start rule, the required host signature, and a `LoweringRegistration` for the box node. `ModuleComposer` performs syntax/semantic composition and verifies the required host binding. The lowerer emits three finite `HirConstant` children and a `HirOperation` root. Child origins point at the individual numeric literals; the root origin points at the whole box statement. The catalog's ordered input/result signature remains the type authority.

## 3. Geometry value and execution

`Nitrogen.Geometry` defines `MeshVertex` as an immutable position/normal pair and `GeometryMesh` as copied, read-only vertex and `ushort` index lists. Construction rejects non-finite coordinates/normals, absent vertices, index counts not divisible by three, and out-of-range indices. This is one concrete geometry value, not a general host-object value model. The existing numeric `ExecutionValue` and `HirEvaluator` APIs remain intact.

A narrow `GeometryExecutor.Execute(root, composition)` accepts the lowered box root and a `ModuleComposition` with its host binding. Before invoking any handler, it checks the root's exact catalog signature, that its three children are finite `Core.Scalar` `HirConstant`s, and that the supplied binding is precisely `Func<IReadOnlyList<ExecutionValue>, GeometryMesh>`. Symbol inputs and nested numeric operations are deferred until the grammar introduces expressions. It passes ordered, read-only numeric arguments to the host and returns the mesh plus all root `SourceOrigin`s. `GX0001` reports a wrong root shape or catalog signature, `GX0002` a missing or incompatible host binding, `GX0003` an invalid child, and `GX0004` a thrown handler or null result. Each diagnostic points at the failing root or child; no host call occurs after a preflight error. Execution does not mutate the HIR or returned mesh.

## 4. Existing Gravity adapter

An editor-side adapter in `RagdollEditor` exposes the required `HostOperationBinding`. It converts full lengths to half extents, calls the existing `MeshGenerator.GenerateBox`, and copies each mutable `Vertex3D` and index into `GeometryMesh`. This keeps Nitrogen's syntax, semantic, and execution library independent of the macOS editor, while putting the renderer-specific dependency at the host edge. The editor adds a project reference to `Nitrogen.Geometry` solely for this adapter; its current mesh generation and compilation callers continue to use their existing paths.

The exact binding and result types must be checked before the adapter is invoked. Mesh generation is trusted in-process C# for this milestone; generated host code and capability permissions are later roadmap work.

## 5. Tests and verification

1. Compose the descriptor with the editor adapter; parse and bind the `.geom` sample, assert no syntax/binding/semantic diagnostics, lower one `Geometry.BoxMesh` root, then execute and compare every generated vertex position/normal and index with the existing mesh generator. Assert the root and numeric-child spans and result origins.
2. Test zero, negative, overflow/non-finite, and malformed dimensions: semantic or syntax diagnostics point at the bad literal and no host handler runs. Test wrong signature arity/result type, swapped numeric dimension values, wrong delegate shape, missing handler, null result, and thrown handler with origin-bearing diagnostics. All three signature inputs are `Core.Scalar`, so swapping their declared types would not change the signature; argument order is tested through output dimensions instead.
3. Register `.geom` with `NitrogenLanguageService`; open the same sample and assert `InspectDocument` and `Inspect` expose the typed mesh root and numeric child at their exact source locations. An edited invalid document must clear the old root and report the dimension diagnostic.
4. Run the focused geometry tests, the full Nitrogen suite, any affected RagdollEditor tests, and the repository fast gate. Compare RagdollEditor failures with issue 245's seven-name run. The extended gate is required only if predictive-control training, `artifacts/`, or velocity-profile files change.

## 6. Scope and delivery

Track this work in `issues/246-nitrogen-geometry-mesh.md` on `codex/246-nitrogen-geometry-mesh`, based on issue 245. Document the chained branch dependency. Do not merge or push without a separate user request. Named definitions, cross-file reuse, geometry transforms/repeat, broad object execution, package loading, untrusted generated code, and production app-path switching remain later steps.
