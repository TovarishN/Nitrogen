# Nitrogen reusable geometry definitions — design

**Issue:** 247. **Date:** 2026-09-26. **Status:** written spec approved for implementation planning.
**Base:** `codex/247-nitrogen-geometry-definitions` starts at issue 246 commit `7a23fa58`. Issues 244–246 are not merged into `master`.

## 1. Purpose and acceptance sample

Milestone 4 of [the Nitrogen roadmap](../../roadmap.md) makes the box mesh proof reusable across `.geom` files. A definition file declares a named box with three typed scalar parameters; another file calls it twice with different dimensions. Each call produces the same `Geometry.BoxMesh` HIR and renderable mesh as a direct `box` statement. The resulting HIR retains both the definition's and each call's source locations. The existing `box 1 2 3;` sample continues to work.

The first slice intentionally has one result type, `Geometry.Mesh`, and one parameter type, `Core.Scalar`. It proves names, types, substitution, cycles, and cross-file reuse without introducing a general function runtime or mesh-valued parameters.

## 2. Syntax, binding, and typing

Extend `Nitrogen.Geometry/Geometry.ngr` with documents containing zero or more statements. A document may define a box, call a definition, or contain a direct box. The accepted form is:

```geom
// definitions.geom
def crate(width: Scalar, height: Scalar, depth: Scalar) = box width height depth;

// scene.geom, in the same Project
make crate(1, 2, 3);
make crate(3, 2, 1);
```

Names are identifiers. Each definition has exactly three distinct `Scalar` parameters in this milestone. The body is either `box <dimension> <dimension> <dimension>;` or `make <name>(<dimension>, <dimension>, <dimension>);`; a dimension is a finite numeric literal or a reference to one of the definition's parameters. This body-call form permits a definition to reuse another definition and makes cycles observable. Top-level `make` arguments are finite numeric literals; the definition body can forward parameters. A direct top-level `box` still accepts only numeric literals. There are no default arguments, arithmetic, named arguments, imports, transforms, or mesh-valued parameters.

Generated binding metadata declares a `shape` symbol for each `def` with `export scope`, declares the three parameter symbols inside that scope, and references `shape` at every `make`. Existing `Project` resolution supplies cross-file lookup. A definition file must be in the same `Project` as the call file; in the language service, both documents must be open. Duplicate/ambiguous exported names and unresolved references are reported through binding plus geometry diagnostics at the relevant name. No filename-based implicit lookup or separate import graph is added.

Generated checks and a geometry lowering helper reject duplicate parameter names, an annotation other than `Scalar`, non-finite or non-positive numeric dimensions, and a wrong call argument count. In this slice, a bad type is demonstrated by a parameter annotation or body reference because top-level call arguments are numeric literals. Expected failures have source-linked geometry diagnostics rather than uncaught exceptions. A bad definition cannot produce a call root.

## 3. HIR expansion and origins

Use **typed HIR expansion at lowering time**. The definition body becomes a box-shaped HIR template containing `Core.Scalar` parameter references. At a `make`, resolve the exported definition by its existing bound `Symbol`, validate three ordered arguments, and substitute scalar HIR constants for the parameter references. If the body calls another definition, expand it recursively. Track the definition symbols in the active expansion stack; a repeated symbol is a cycle and produces a diagnostic at the closing call. No host handler runs for a failed expansion.

The expanded result is an ordinary `HirOperation` with the existing `BoxMeshModule.BoxSignature` and three finite scalar constants. `GeometryExecutor` and `GeometryBoxMeshHost` therefore execute it without new runtime value kinds. Every returned root keeps the call statement and definition body origins; each substituted child keeps the argument literal, parameter declaration/use, and definition-body origins as applicable. Origins use one caller-supplied snapshot ID for all files in that expansion, and retain their distinct paths and spans. Repeated calls have independent call origins and may reuse the same definition origin. Preserve a definition's bound symbol identity during lookup; same-spelled names are not interchangeable.

The generic `HirLowering` API may gain a small diagnostic-reporting hook on `LoweringContext` so the geometry lowerer can report expected cross-file, type, arity, and cycle failures without throwing. `FileSemantics` may expose a narrow way to retrieve another file in its `ProjectSemantics`. These changes must preserve existing Motion and Policy lowering behavior. Only top-level box and make statements become executable/inspectable roots; definition-body syntax does not appear as a standalone output root.

## 4. Diagnostics, inspection, and verification

Use `GD0001` for an invalid declaration/body, `GD0002` for a missing or ambiguous definition, `GD0003` for wrong call arity, and `GD0004` for cyclic expansion. Keep `GE0001` for bad numeric dimensions and the existing binder codes for duplicate parameters or exported names. Each diagnostic points at the smallest relevant parameter, argument, name, or call span. Language-service `InspectDocument` returns one mesh HIR root per valid top-level call; `Inspect` at an argument selects its scalar child with the argument range. Definition and use navigation continues to use `Project` binding. Editing or closing a definition invalidates dependent call inspections; the service must not retain a stale successful root.

Tests cover: direct box compatibility; a two-file definition plus two calls with distinct dimensions; exact renderable mesh parity for both calls; parameter and call origin sets; a forwarded body call; duplicate parameters; invalid annotations and dimensions; wrong arity; missing and ambiguous names; direct and indirect cycles; and changed/removed definition documents. Focused geometry tests, the full Nitrogen suite, and Gravity's fast gate must run. The known seven RagdollEditor failures from issue 246 are the comparison baseline. The extended gate applies only if its documented training, `artifacts/`, or velocity-profile triggers are touched.

## 5. Scope and delivery

Track work in `issues/247-nitrogen-geometry-definitions.md` on `codex/247-nitrogen-geometry-definitions`. Keep the existing Motion, Policy, Game3D, and editor production compilation/rendering paths unchanged. Defer arbitrary parameter lists, mesh-valued parameters, a general call runtime, nested mesh composition, grammar-package loading, and generated-agent trust controls. Do not merge or push without a separate user request.
