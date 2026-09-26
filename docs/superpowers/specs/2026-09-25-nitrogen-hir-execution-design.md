# Nitrogen typed HIR execution — design

**Issue:** 245. **Date:** 2026-09-25. **Status:** approved for implementation.
**Base:** `codex/244-nitrogen-module-contract` at `5daa8b16`; issue 244 is not merged into `master`.

## 1. Purpose and proof

Milestone 2 of [the Nitrogen roadmap](../../roadmap.md) executes one small typed HIR slice. The primary proof lowers a dynamic Motion angle track such as `gain + 1deg`, supplies a scalar `gain`, invokes pure host operations for `Motion.AddScalarAngle` and `Motion.AngleSlot`, and returns the expected angle value. A Policy `clamp` document checks the constant-plus-operation path. No production Motion/Policy compiler or Game3D caller changes to HIR execution.

Success means a consumer can bind exact operation signatures before running, provide typed symbol values explicitly, evaluate `HirConstant`, `HirSymbolRef`, and `HirOperation`, and receive either a typed numeric value with root origins or a diagnostic pointing to the source node that failed. The result must be reproducible for the same HIR, inputs, and pure handlers.

## 2. Existing contract and execution boundary

`OperationSignature` carries a stable ID, ordered input types, and result type. `HirOperation` checks its child types at construction. Issue 244's `HostOperationBinding` stores an exact signature and a non-null `Delegate`, and `ModuleComposer` validates declared requirements against semantic exports and bindings without invoking delegates. This milestone adds a typed runtime binding step over those records; it does not break the issue 244 API. It can bind directly against a `SemanticCatalog`, so the existing `NitrogenMotionParser.Language` and `NitrogenPolicyParser.Language` can supply real lowerings without changing their language construction.

The execution slice supports finite `float` values of `Core.Scalar` and `Units.Angle`. `ExecutionValue` owns that type and number and rejects null, unsupported types, and non-finite numbers. Bool, entities, effects, arbitrary host objects, and geometry values are outside this slice. A handler uses `Func<IReadOnlyList<ExecutionValue>, ExecutionValue>`; the runtime gives it read-only, ordered inputs. Host delegates are trusted in-process C# code for this milestone. Agent-generated handlers and permissions are later work.

## 3. Host operation registry

`HostOperationRegistry.TryBind(catalog, bindings, out registry, out diagnostics)` materializes bindings once, validates each binding ID and full signature against the catalog, rejects incompatible duplicates and delegates with the wrong callable shape, then freezes an ID-indexed registry. Equivalent duplicate records coalesce only when they share the same handler instance, matching issue 244. Extra valid bindings are allowed. No handler is invoked while binding. A throwing `Bind` counterpart exposes the same structured diagnostics. Registry validation is independent of the evaluator and can be reused by a later geometry host.

Before running a root, the evaluator traverses every operation and symbol reference. It checks that each operation signature equals the catalog export and has a bound handler, and that every symbol input exists with the reference's exact `SemanticType`. A failed preflight returns diagnostics without invoking any handler. This catches a missing operation deep in the tree even when an outer operation is bound. It also prevents a value from a different project snapshot being matched merely by name: inputs are keyed by the actual `Nitrogen.Binding.Symbol` object associated with each `HirSymbolRef`.

## 4. Evaluation and source provenance

`HirEvaluator.Evaluate(root, registry, inputs)` recursively evaluates constants, references, then operations in argument order. For an operation, it passes evaluated `ExecutionValue`s to the typed handler and validates the returned type against the declared result. A null result, non-finite number, or thrown handler exception becomes an execution diagnostic rather than escaping as an application exception. The first failed child stops its parent; no later handler runs for that root. Successful results carry the root's `Origins`, including origins retained by HIR rewrites.

Each diagnostic has a stable `NE` code, a primary `SourceOrigin`, and a message. A missing or mismatched symbol points at the reference; an unsupported/non-finite constant points at that constant; an unbound or incompatible operation and a handler failure point at the operation. The diagnostic keeps the origin's path, snapshot ID, node, and span. The evaluator does not persist a source map or rebind symbols after an edit; callers use HIR and inputs from the same snapshot.

## 5. Tests and compatibility

1. Binding tests cover exact ordered signatures, absent exports, duplicate IDs, wrong delegate shape, no invocation during binding, and deterministic diagnostics across input order.
2. Evaluator tests cover constants, symbol inputs, nested operation order, typed result, missing input, wrong input type, missing deep handler, incompatible HIR signature, unsupported or non-finite values, null/wrong-type handler returns, and handler exceptions. Each failure asserts its diagnostic code and exact source origin; preflight failures assert zero handler calls.
3. End-to-end tests lower `gain + 1deg` through the existing Motion parser/binder/semantic catalog, bind two pure handlers, provide `gain = 0.5`, and check `0.5 + π/180` radians within floating-point tolerance. A Policy clamp sample binds an identity handler and checks its radian value. The tests inspect the existing lowerings rather than changing compiler outputs.
4. Run the full Nitrogen suite and the repository fast gate. Compare any non-Nitrogen failures with the issue 244 baseline. The extended gate is required only if predictive-control training, `artifacts/`, or velocity-profile files change.

## 6. Delivery boundary

Track work in `issues/245-nitrogen-hir-execution.md` on `codex/245-nitrogen-hir-execution`, based on issue 244. Record the dependency explicitly: merging or publishing issue 245 requires issue 244's commits or an equivalent merged base. Do not merge or push as part of this milestone without a separate user request. Geometry, reusable definitions, agent module admission, capability permissions, general value types, and production compiler switching remain later roadmap steps.
