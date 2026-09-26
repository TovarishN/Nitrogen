# Nitrogen Reusable Geometry Definitions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a three-scalar box definition in one `.geom` file callable from another, producing the existing renderable box mesh with definition and call origins.

**Architecture:** Extend the generated geometry grammar with exported definitions, scoped scalar parameters, and calls. A geometry lowerer resolves definitions through the existing `Project`, expands their bodies into typed HIR, substitutes ordered scalar arguments, and reports source-linked errors. The expanded root still executes through the issue 246 `GeometryExecutor` and editor adapter.

**Tech Stack:** .NET/C#, Nitrogen `.ngr` source generator, `Nitrogen.Runtime` binding/semantics/HIR, `Nitrogen.Geometry`, `Nitrogen.LanguageService`, xUnit.

**Spec:** [2026-09-26-nitrogen-geometry-definitions-design.md](../specs/2026-09-26-nitrogen-geometry-definitions-design.md)

## Global Constraints

- Work on `codex/247-nitrogen-geometry-definitions`, based on issue 246 commit `7a23fa58`; prefix commits with `[codex/247-nitrogen-geometry-definitions]`.
- Preserve direct `box 1 2 3;` syntax and execution, the exact `Geometry.BoxMesh(Scalar, Scalar, Scalar) -> Geometry.Mesh` signature, and the current editor host adapter.
- Each definition has exactly three distinct `Scalar` parameters. Top-level `make` arguments are numeric; a definition body may forward parameter references through a nested `make`. Defer arbitrary arity, mesh-valued parameters, arithmetic, imports, transforms, and general runtime calls.
- Definitions are visible when their document is present in the same `Project`; in `NitrogenLanguageService` both documents must be open. Do not add filename-based lookup.
- Keep Motion, Policy, Game3D, and editor production compilation/rendering paths unchanged. Preserve unrelated untracked files, especially `JpcSharp/docs/superpowers/plans/2026-07-12-held-biped-contact-control.md`.
- Run focused geometry tests, the full Nitrogen suite, and `dotnet test Gravity.slnx --filter "Gate!=Extended"`. Compare RagdollEditor failures with issue 246's seven named baseline. Run the extended gate only if predictive-control training, `artifacts/`, or velocity-profile files change.
- Do not merge or push without a separate user request. Issues 244–246 are unmerged dependencies.

## File map

- Modify `Nitrogen/Nitrogen.Geometry/Geometry.ngr`: document statements, exported definitions, scoped typed parameters, template boxes, and calls.
- Modify `Nitrogen/Nitrogen.Geometry/GeometryValues.cs`: only if a shared literal/annotation predicate is needed by generated checks.
- Modify `Nitrogen/Nitrogen.Geometry/BoxMeshModule.cs`: register the call lowerer alongside the existing direct-box lowerer.
- Create `Nitrogen/Nitrogen.Geometry/GeometryDefinitionExpander.cs`: resolve one definition and substitute its body using bound symbol identity; recursive expansion and cycle guard live here.
- Modify `Nitrogen/Nitrogen.Runtime/Semantic/HirLowering.cs`: expected-error reporting from a lowering callback, plus a registration opt-in for handling unresolved names.
- Modify `Nitrogen/Nitrogen.Runtime/Semantics/FileSemantics.cs`: narrow access to a related file in the same `ProjectSemantics`.
- Add focused tests under `Nitrogen/Nitrogen.Tests/Geometry/` and `Nitrogen/Nitrogen.Tests/Semantic/`.
- Update `issues/247-nitrogen-geometry-definitions.md` and `docs/roadmap.md` after verification.

## Review Focus

1. Removing or editing the definition while a call document stays open must invalidate `InspectDocument`'s old mesh root (Task 5).
2. Two exported definitions with the same spelling must not let the lowerer pick one arbitrarily (Task 3).
3. A self-call or two-definition cycle must produce `GD0004` at the closing call, without stack overflow or host invocation (Task 4).
4. Forwarded parameters with the same spelling in two definitions must substitute by bound symbol identity, preserving each source origin (Task 4).
5. `1e999`, zero, or negative values in either a call or definition body must produce a source-linked `GE0001`, never a mesh (Tasks 1 and 3).

---

### Task 1: Parse and bind definitions and calls without disturbing direct boxes

**Files:**
- Modify: `Nitrogen/Nitrogen.Geometry/Geometry.ngr`
- Test: `Nitrogen/Nitrogen.Tests/Geometry/GeometryDefinitionSyntaxTests.cs`
- Test: `Nitrogen/Nitrogen.Tests/Geometry/GeometryModuleTests.cs`

**Interfaces:**
- Produces generated `Definition`, `Parameter`, `TemplateBox`, `ParameterRef`, and `Make` syntax views and kinds in `Nitrogen.Geometry.Syntax`.
- Produces `shape` exports and `parameter` scoped references for `Project`; Task 3 consumes their bound `Symbol`s.

- [ ] **Step 1: Write failing syntax and binding tests.** Parse `def crate(width: Scalar, height: Scalar, depth: Scalar) = box width height depth;` in `definitions.geom` and `make crate(1, 2, 3); make crate(3, 2, 1);` in `scene.geom` using the composed `Document` start. Put both parsed trees in one `Project`; assert parse success, no `Project.Diagnostics` on either path, one exported `shape` declaration, three scoped `parameter` declarations, and both call references resolve to the *same* exported `Symbol`. Keep a `box 1 2 3;` parse/lowering assertion. Add failing cases for duplicate parameter names (`NB0002`), duplicate exported `crate` in two files (`NB0003`), unresolved call (`NB0001`), and `1e999`, `0`, `-1` in call or template-body numeric positions (eventually `GE0001` at the literal). Example setup:

```csharp
var composed = ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);
using var definition = composed.Language.Parse(definitionText, composed.StartRules[("Geometry", "Document")]);
using var scene = composed.Language.Parse(sceneText, composed.StartRules[("Geometry", "Document")]);
var project = new Project(composed.Language);
project.Set("definitions.geom", definition.Tree);
project.Set("scene.geom", scene.Tree);
Assert.Same(project.Resolve(project["scene.geom"].References[0])[0],
    project.Resolve(project["scene.geom"].References[1])[0]);
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~GeometryDefinitionSyntaxTests`. Expected: the new definition/call documents fail to parse.
- [ ] **Step 3: Implement grammar and generated checks.** Retain the existing `Num` and top-level `Box` rules so `GeometryHirLowerer` remains valid. Replace only the `Document` rule with repeated statements; add symbol metadata, identifier token, and these forms (adjust generated view field names to the compiler's emitted names, but keep the stated syntax and binding):

```ngr
symbols { shape parameter }
symbol property Type for parameter : string = "error";
token IdStart = ['a'..'z' 'A'..'Z' '_'];
token IdPart = ['a'..'z' 'A'..'Z' '0'..'9' '_'];
token Identifier = IdStart IdPart* except "def" "make" "box" "Scalar" "Mesh";
syntax Document = Statements:Statement*;
syntax Statement = Definition / Make / Box;
syntax Definition = "def" Name:Identifier "(" Width:Parameter "," Height:Parameter "," Depth:Parameter ")" "=" Body:DefinitionBody
  declares shape Name export scope;
syntax Parameter = Name:Identifier ":" Annotation:("Scalar" / "Mesh")
  declares parameter Name { symbol.Type = Annotation.Text; check GD0001 Annotation.Text == "Scalar" : "box parameters must be Scalar" at Annotation; }
syntax DefinitionBody = TemplateBox / Make;
syntax TemplateBox = "box" Width:Dimension Height:Dimension Depth:Dimension ";";
syntax Dimension = Num / ParameterRef;
syntax ParameterRef = Name:Identifier references parameter Name;
syntax Make = "make" Name:Identifier "(" Args:(Dimension; ",")* ")" ";"
  references shape Name { check GD0003 Args.Count == 3 : "make needs three dimensions" at Name; }
```

The token `Identifier` excludes `def`, `make`, `box`, `Scalar`, and `Mesh`. Use `GeometryValues.Positive` for all numeric dimensions; for template boxes check each numeric alternative without treating a parameter reference as a literal. Generated `symbol.Type` and a `GD0001` check reject a non-scalar parameter reference in a box body. Preserve `GE0001` on every invalid numeric literal.
- [ ] **Step 4: Rerun** `FullyQualifiedName~GeometryDefinitionSyntaxTests|FullyQualifiedName~GeometryModuleTests`. Expected: syntax/binding/direct-box compatibility tests pass. If the generator rejects an annotated alternative, express the same check in a generated helper called from `.ngr`, keeping diagnostic codes and spans unchanged.
- [ ] **Step 5: Commit** Task 1 files with `[codex/247-nitrogen-geometry-definitions] Parse and bind geometry definitions`.

### Task 2: Add expected-error reporting for cross-file lowering

**Files:**
- Modify: `Nitrogen/Nitrogen.Runtime/Semantic/HirLowering.cs`
- Modify: `Nitrogen/Nitrogen.Runtime/Semantics/FileSemantics.cs`
- Test: `Nitrogen/Nitrogen.Tests/Semantic/HirLoweringTests.cs`

**Interfaces:**
- Produces `LoweringContext.Report(string code, SourceOrigin origin, string message)`; `HirLowering.Lower` returns those diagnostics alongside its existing ones.
- Produces `FileSemantics.RelatedFile(string path) -> FileSemantics`, resolving only documents already in the same project.
- Extends `LoweringRegistration` with optional `HandlesUnresolvedReferences = false`; only the geometry `Make` registration opts in, leaving existing Motion/Policy preflight unchanged.

- [ ] **Step 1: Write failing runtime tests.** Register a lowerer that calls `context.Report("GXTEST", context.Origin(node), "expected failure")` and returns null; assert `LoweringResult.Diagnostics` contains exactly that code and source span. Register a second lowerer with `HandlesUnresolvedReferences: true` on a syntax rule that references an absent symbol; assert the callback runs and reports its own diagnostic, while the default registration still yields `NH0002`. With two parsed documents in one `ProjectSemantics`, assert `RelatedFile("definitions.geom")` returns the matching `FileSemantics` and a missing path throws `KeyNotFoundException`.

```csharp
var registration = new LoweringRegistration(kind, signature.Id,
    (context, node) => { context.Report("GXTEST", context.Origin(node), "expected failure"); return null; });
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~HirLoweringTests`. Expected: compile fails because the reporting and related-file APIs are absent.
- [ ] **Step 3: Implement the narrow runtime APIs.** Accumulate reported `LoweringDiagnostic`s on `LoweringContext`, append them to `HirLowering.Lower`'s final diagnostics, and preserve ordering by origin path/span and report order. Keep callback-reported diagnostics even when it returns null. Add an optional registration boolean (default false); bypass only the current generic unresolved-reference guard for an opted-in rule, so the call lowerer can distinguish missing and ambiguous definitions and report `GD0002`. Do not bypass recovery or invalid semantic/binding checks. `RelatedFile` delegates to the owning `ProjectSemantics` indexer and does not parse or load files.
- [ ] **Step 4: Rerun** the focused `HirLoweringTests` and the existing `HirPreflightTests`, `HirEvaluatorTests`, and geometry tests. Expected: all pass and non-geometry lowering behavior is unchanged.
- [ ] **Step 5: Commit** with `[codex/247-nitrogen-geometry-definitions] Report expected lowering diagnostics`.

### Task 3: Expand one cross-file definition into box HIR

**Files:**
- Create: `Nitrogen/Nitrogen.Geometry/GeometryDefinitionExpander.cs`
- Modify: `Nitrogen/Nitrogen.Geometry/BoxMeshModule.cs`
- Test: `Nitrogen/Nitrogen.Tests/Geometry/GeometryDefinitionExpansionTests.cs`

**Interfaces:**
- Consumes `FileSemantics.RelatedFile`, `LoweringContext.Report`, generated geometry views, and existing `Project` resolution through `FileSemantics.SymbolOf`.
- Produces `GeometryDefinitionExpander.Registration`, a `LoweringRegistration` for `GeometryKinds.Make` with `HandlesUnresolvedReferences: true`.
- Produces only ordinary `HirOperation(BoxMeshModule.BoxSignature, ...)` roots for top-level calls; no new executor type.

- [ ] **Step 1: Write failing expansion tests.** Parse and bind the approved two-file sample. Lower `scene.geom` with a fixed snapshot ID; assert two box roots with constant children `[1,2,3]` and `[3,2,1]`, each root has both `scene.geom` call and `definitions.geom` body origins, and each child has its call literal plus definition parameter-use/declaration origins. Assert no root is emitted when lowering `definitions.geom` alone. Test `make absent(1,2,3);` and two exported `crate` definitions: `GD0002` in lowering diagnostics at the call name, no root. Test `make crate(1,2);`: `GD0003` in `FileSemantics.Diagnostics()` at the call name and no root. Test a bad `Mesh` annotation: `GD0001` in the definition file's semantic diagnostics and no call root. Test `1e999`, zero, and negative call values: `GE0001` at the literal and no root.

```csharp
var lowered = HirLowering.Lower(new ProjectSemantics(project)["scene.geom"],
    composed.Language.SemanticCatalog, snapshotId);
Assert.Equal(2, lowered.Roots.Count);
Assert.Equal([1f, 2f, 3f], Assert.IsType<HirOperation>(lowered.Roots[0]).Arguments
    .Select(node => Assert.IsType<HirConstant>(node).Value));
Assert.Contains(lowered.Roots[0].Origins, origin => origin.Path == "definitions.geom");
```

- [ ] **Step 2: Run** `FullyQualifiedName~GeometryDefinitionExpansionTests`. Expected: there are no call roots before registration.
- [ ] **Step 3: Implement one-level typed substitution.** On a top-level `Make`, use `context.File.SymbolOf(makeNode)` to require one bound `shape` symbol; a null result reports `GD0002`. Use `RelatedFile(symbol.Path!)` and the declaration node to read the three `Parameter` symbols and body. Build a template `HirOperation` from `TemplateBox` dimensions: literal dimensions become finite `HirConstant`; parameter references become `HirSymbolRef(SemanticSymbol.From(boundParameter, "Geometry", SemanticTypes.Scalar), origin)`. Map each *bound parameter Symbol* to the corresponding validated call constant. Use `HirTraversal.Rewrite` to replace parameter references, then attach the top-level call origin to the result, retaining the definition and argument origins. For expected failures, `context.Report` the specified `GD`/`GE` code and return null. Guard the generated `Make` registration so a nested call inside a definition is not emitted as a standalone root. Check the source definition's binding/semantic diagnostics before constructing its body. Register the lowerer in `BoxMeshModule.Descriptor`.

```csharp
public static LoweringRegistration Registration { get; } =
    new(GeometryKinds.Make, BoxMeshModule.BoxSignature.Id, Lower, HandlesUnresolvedReferences: true);
```

- [ ] **Step 4: Rerun** expansion, direct geometry, and executor filters. Expected: two-file one-level calls lower correctly; direct boxes and issue 246 execution remain unchanged.
- [ ] **Step 5: Commit** with `[codex/247-nitrogen-geometry-definitions] Expand cross-file box calls`.

### Task 4: Forward parameters and stop recursive cycles

**Files:**
- Modify: `Nitrogen/Nitrogen.Geometry/GeometryDefinitionExpander.cs`
- Test: `Nitrogen/Nitrogen.Tests/Geometry/GeometryDefinitionExpansionTests.cs`

**Interfaces:**
- Extends the Task 3 expander to handle a definition body whose `Make` calls another definition.
- Uses the existing `Geometry.BoxMesh` root as the final result and `GD0004` for cycles.

- [ ] **Step 1: Write failing recursive cases.** Define `outer(width: Scalar, height: Scalar, depth: Scalar) = make inner(width,height,depth);` in one file and `inner(width: Scalar,height: Scalar,depth: Scalar) = box width height depth;` in another. A third file calls `outer(1,2,3)`; assert its final root has three constants and origins from all three files, proving that same-spelled parameters are substituted by bound identity. Add a renamed-parameter variant to prove order still comes from parameter position. Define direct self-recursion and an `outer`↔`inner` cycle; assert one `GD0004` at the closing `make`, no roots, no handler call, and no overflow. Call `outer` twice with different arguments; assert distinct call origins and deterministic vertices/indices after execution.
- [ ] **Step 2: Run** `FullyQualifiedName~GeometryDefinitionExpansionTests`. Expected: the forwarded call or cycle assertions fail against one-level expansion.
- [ ] **Step 3: Implement recursive expansion.** Use `HashSet<Symbol>(ReferenceEqualityComparer.Instance)` as the active definition stack, not a name set. Resolve each nested call in its owning definition file; map the callee's parameters by their bound symbols, evaluate forwarded argument nodes from the caller's environment, and recurse until `TemplateBox`. On an already-active symbol, report `GD0004` at that nested call's name. Use `try/finally` to remove each symbol. Preserve all distinct definition/call origins when cloning HIR; do not reuse mutable argument maps between separate top-level calls.

```csharp
if (!active.Add(definitionSymbol))
{
    context.Report("GD0004", callOrigin, "cyclic geometry definition");
    return null;
}
try { return ExpandBody(definitionFile, definitionNode, arguments, active); }
finally { active.Remove(definitionSymbol); }
```

- [ ] **Step 4: Rerun** expansion, direct geometry, and executor filters. Expected: all pass, including no host invocation on a cycle.
- [ ] **Step 5: Commit** with `[codex/247-nitrogen-geometry-definitions] Expand forwarding and reject cycles`.

### Task 5: Prove inspection, invalidation, and renderable reuse

**Files:**
- Test: `Nitrogen/Nitrogen.Tests/Geometry/GeometryDefinitionIntegrationTests.cs`
- Modify: `Nitrogen/Nitrogen.Geometry/GeometryDefinitionExpander.cs` only if integration tests uncover a defect.
- Modify: `issues/247-nitrogen-geometry-definitions.md`
- Modify: `docs/roadmap.md`

**Interfaces:**
- Consumes Task 4 HIR roots and the unchanged `GeometryExecutor`, `GeometryBoxMeshHost.Binding`, and `NitrogenLanguageService`.
- Produces the accepted two-file geometry proof and recorded test evidence.

- [ ] **Step 1: Write failing end-to-end tests.** Register `.geom` in `LanguageRegistry` with `BoxMeshModule.Descriptor`'s `Document` start; open the definition and use files. Assert `InspectDocument(useUri)` has two `Geometry.Mesh` roots, `Inspect(useUri, positionOf("2"))` selects the scalar child at that literal, and `Definition` navigation from `crate` reaches the exported declaration. Execute both roots through the unchanged editor binding and compare every position, normal, and triangle index with `MeshGenerator.GenerateBox(new Vector3(0.5f,1f,1.5f))` and the swapped half extents. Change the definition to an invalid type/body; assert the old inspected root is gone and the current diagnostic points at the changed source. Restore it, then close it; assert call inspection no longer returns a successful root.

```csharp
var composition = ModuleComposer.Compose([BoxMeshModule.Descriptor], [GeometryBoxMeshHost.Binding]);
var registry = new LanguageRegistry();
registry.Add(new LanguageEntry("geometry", composition.Language,
    new Dictionary<string, Rule> { [".geom"] = composition.StartRules[("Geometry", "Document")] }));
using var service = new NitrogenLanguageService(registry);
service.Open(definitionUri, 1, definitionText);
service.Open(useUri, 1, callText);
Assert.Equal(2, service.InspectDocument(useUri)!.Roots.Count);
```

- [ ] **Step 2: Run** `FullyQualifiedName~GeometryDefinitionIntegrationTests`. Expected: a failure identifies any missed integration boundary; if all tests pass, record that Tasks 1–4 already satisfy this proof and do not invent a production change.
- [ ] **Step 3: Fix only demonstrated integration defects.** Keep the project-version cache invalidation already present in `NitrogenLanguageService.InspectDocument`; repair any missing source-origin or lowerer diagnostic propagation in the geometry path rather than adding a second cache. Ensure a removed definition yields `GD0002` at the call name, and neither invalid nor missing definitions invoke the host. The expected cache behavior is:

```csharp
var before = service.InspectDocument(useUri)!;
service.Change(definitionUri, 2, invalidDefinition);
var after = service.InspectDocument(useUri)!;
Assert.NotEqual(before.SnapshotId, after.SnapshotId);
Assert.Empty(after.Roots);
```
- [ ] **Step 4: Run verification.** Run `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`, `dotnet test Gravity.slnx --filter "Gate!=Extended"`, and `git diff --check 7a23fa58`. Record exact test counts and compare all seven RagdollEditor failure names with issue 246. The extended gate is not triggered unless the documented file classes changed.
- [ ] **Step 5: Update docs and commit.** Mark issue 247's implementation/review status and append the milestone 4 result in `docs/roadmap.md`; commit with `[codex/247-nitrogen-geometry-definitions] Prove reusable box definitions`.

## Completion review

- [ ] Compare the whole branch with spec sections 1–5: two-file syntax, binding identity, typed HIR expansion, origins, cycles, diagnostics, inspection invalidation, and existing mesh parity.
- [ ] Review `git diff 7a23fa58..HEAD` for unrelated changes and production-path modifications. Have one fresh whole-branch reviewer check the branch if available; record concrete findings and test-first fixes in issue 247.
- [ ] Leave the branch unmerged and unpushed; issues 244–246 remain unmerged dependencies.
