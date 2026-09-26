# Nitrogen Module Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compose a C# registered Nitrogen module as a validated unit of syntax, semantics, start rules, and required host bindings.

**Architecture:** Add an immutable descriptor and a separate `ModuleComposer` in `Nitrogen.Runtime`. The composer delegates grammar and semantic work to `LanguageBuilder`, resolves named start rules through each descriptor's syntax module, and checks host operation signatures without executing delegates. Existing builder callers stay intact.

**Tech Stack:** .NET/C#, xUnit, `Nitrogen.Runtime`, `Nitrogen.Tests`.

**Spec:** [2026-09-25-nitrogen-module-contract-design.md](../specs/2026-09-25-nitrogen-module-contract-design.md)

## Global Constraints

- Keep `.ngr` syntax, generator output, Motion/Policy compilers, and existing `LanguageBuilder.Build/TryBuild` behavior unchanged.
- Host delegates are required and stored, but never invoked in milestone 1.
- Do not load external assemblies, helper C#, or agent-generated modules.
- Use branch `codex/244-nitrogen-module-contract`; prefix every commit message with `[codex/244-nitrogen-module-contract]`.
- Preserve unrelated untracked work, especially `JpcSharp/docs/superpowers/plans/2026-07-12-held-biped-contact-control.md`.

## File map

- Create `Nitrogen/Nitrogen.Runtime/ModuleDescriptor.cs`: immutable descriptor, start-rule names, and host binding carrier.
- Create `Nitrogen/Nitrogen.Runtime/ModuleComposer.cs`: composition API, result, diagnostics, and exception conversion.
- Create `Nitrogen/Nitrogen.Tests/Semantic/ModuleDescriptorTests.cs`: constructor and copy invariants.
- Create `Nitrogen/Nitrogen.Tests/Semantic/ModuleComposerTests.cs`: composition, host validation, conflicts, and end-to-end fixture tests.
- Update `issues/244-nitrogen-module-contract.md` and `docs/roadmap.md` only when verified status changes.

## Review Focus

These user-facing inputs deserve explicit tests in the owning tasks:

1. Mutating the caller's descriptor lists after construction must not change the descriptor (Task 1).
2. A semantic-only dependency with a start rule name must fail with a module-specific diagnostic (Task 2).
3. Two descriptors declaring the same module ID must fail even if their content happens to match (Task 2).
4. A host binding with the right ID but reordered input types must fail exact-signature validation (Task 3).
5. Reversing descriptor or host-binding input order must leave diagnostic codes and messages stable (Tasks 2–3).

---

### Task 1: Define immutable descriptor and host binding shape

**Files:**
- Create: `Nitrogen/Nitrogen.Runtime/ModuleDescriptor.cs`
- Create: `Nitrogen/Nitrogen.Tests/Semantic/ModuleDescriptorTests.cs`

**Interfaces:**
- Produces: `ModuleDescriptor(string id, SyntaxModule? syntax, SemanticModule? semantics, IEnumerable<string> startRules, IEnumerable<OperationSignature> requiredOperations)` with copied read-only `StartRules` and `RequiredOperations`.
- Produces: `HostOperationBinding(OperationSignature signature, Delegate handler)` with non-null fields. Its handler is opaque until milestone 2.

- [ ] **Step 1: Write failing tests.** In `ModuleDescriptorTests`, construct a descriptor from mutable lists, mutate both lists, and assert its collections retain their original values. Assert empty ID, both parts null, start rule with no syntax, null signature, and null handler are rejected with argument exceptions. A semantic-only descriptor with no start rules is valid.

```csharp
var names = new List<string> { "Doc" };
var required = new List<OperationSignature> { new("Test.Read", SemanticTypes.Scalar) };
var descriptor = new ModuleDescriptor("Test", UsesModule.Instance, null, names, required);
names.Add("Other");
required.Clear();
Assert.Equal(["Doc"], descriptor.StartRules);
Assert.Single(descriptor.RequiredOperations);
Assert.Throws<ArgumentException>(() => new ModuleDescriptor(" ", UsesModule.Instance, null, [], []));
Assert.Throws<ArgumentException>(() => new ModuleDescriptor("Empty", null, null, [], []));
Assert.Throws<ArgumentException>(() => new ModuleDescriptor("Bad", null, new SemanticModule("Bad", [], [], []), ["Doc"], []));
Assert.Throws<ArgumentNullException>(() => new HostOperationBinding(new OperationSignature("Test.Read", SemanticTypes.Scalar), null!));
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ModuleDescriptorTests`. Expect a compile failure because the types do not exist.
- [ ] **Step 3: Implement** the two sealed types with public read-only properties, `ArgumentNullException`/`ArgumentException` checks, `Array.AsReadOnly` copies, and no invocation logic. Keep this file limited to data and local invariants.

```csharp
Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("A module ID is required.", nameof(id)) : id;
if (syntax is null && semantics is null) throw new ArgumentException("Syntax or semantics is required.");
StartRules = Array.AsReadOnly(startRules.ToArray());
RequiredOperations = Array.AsReadOnly(requiredOperations.ToArray());
```
- [ ] **Step 4: Rerun** the same test command. Expect all `ModuleDescriptorTests` to pass.
- [ ] **Step 5: Commit** only the two Task 1 files with message `[codex/244-nitrogen-module-contract] Add immutable module descriptor`.

### Task 2: Compose descriptors, semantics, syntax, and start rules

**Files:**
- Create: `Nitrogen/Nitrogen.Runtime/ModuleComposer.cs`
- Create: `Nitrogen/Nitrogen.Tests/Semantic/ModuleComposerTests.cs`

**Interfaces:**
- Consumes: `ModuleDescriptor`, `HostOperationBinding`, `LanguageBuilder`, `CompositionDiagnostic`.
- Produces: `ModuleComposition` with `Language`, read-only start rules keyed by `(descriptor ID, rule name)`, and read-only host bindings keyed by operation ID.
- Produces: `ModuleComposer.TryCompose(IEnumerable<ModuleDescriptor> descriptors, IEnumerable<HostOperationBinding> bindings, out ModuleComposition? result, out IReadOnlyList<CompositionDiagnostic> diagnostics)` and `Compose` throwing `SemanticCompositionException` for failed composition.

- [ ] **Step 1: Write failing tests** for a valid `BaseModule`/`UsesModule` descriptor pair with `UsesModule.Doc`, a semantic-only Units dependency, a missing semantic import (`NC0001`), duplicate descriptor ID (`NM0001`), missing start rule (`NM0002`), and the Review Focus semantic-only and reverse-order cases. Assert failure leaves `result` null and codes/messages deterministic. Use `SemanticModule("NeedsUnits", ["Units"], [], [])` for the import case.

```csharp
var units = new ModuleDescriptor("Units", null,
    new SemanticModule("Units", [], [SemanticTypes.Angle], []), [], []);
var uses = new ModuleDescriptor("Uses", UsesModule.Instance,
    new SemanticModule("NeedsUnits", ["Units"], [], []), ["Doc"], []);
Assert.True(ModuleComposer.TryCompose([uses, units], [], out var result, out var errors));
Assert.Empty(errors);
Assert.NotNull(result);
Assert.Equal("Doc", result.StartRules[("Uses", "Doc")].Name);
Assert.False(ModuleComposer.TryCompose([uses, uses], [], out _, out var duplicate));
Assert.Contains(duplicate, d => d.Code == "NM0001");
```

- [ ] **Step 2: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ModuleComposerTests`. Expect a compile failure because `ModuleComposer` does not exist.
- [ ] **Step 3: Implement** the composer. Materialize descriptors once, reject duplicate IDs in ordinal order, resolve each start with `descriptor.Syntax?.GetRule(name)`, and collect `NM0002` for absence. Call `LanguageBuilder.Add/AddSemantic/TryBuild` for existing semantic checks. If any diagnostic exists, publish no result. Wrap successful language and resolved starts in read-only dictionaries. `Compose` reuses `TryCompose` and throws `SemanticCompositionException` with its diagnostics. Keep host bindings stored but defer their validation to Task 3; tests in this task use empty requirements and bindings.

```csharp
foreach (var descriptor in allDescriptors.OrderBy(d => d.Id, StringComparer.Ordinal))
{
    if (descriptor.Syntax is { } syntax) builder.Add(syntax);
    if (descriptor.Semantics is { } semantics) builder.AddSemantic(semantics);
    foreach (var name in descriptor.StartRules)
    {
        var rule = descriptor.Syntax?.GetRule(name);
        if (rule is null) errors.Add(new CompositionDiagnostic("NM0002", [descriptor.Id],
            $"Module '{descriptor.Id}' has no start rule '{name}'."));
        else starts.Add((descriptor.Id, name), rule.Value);
    }
}
```
- [ ] **Step 4: Rerun** the same test filter. Expect Task 2 cases to pass; ensure the existing `SemanticCatalogTests` filter also passes.
- [ ] **Step 5: Commit** Task 2 files with message `[codex/244-nitrogen-module-contract] Compose syntax and semantic descriptors`.

### Task 3: Validate host binding contracts

**Files:**
- Modify: `Nitrogen/Nitrogen.Runtime/ModuleComposer.cs`
- Modify: `Nitrogen/Nitrogen.Tests/Semantic/ModuleComposerTests.cs`

**Interfaces:**
- Consumes: `OperationSignature.Equals`, `Language.SemanticCatalog.Operations`, and `HostOperationBinding.Signature`.
- Produces: `NM0003` for an unexported requirement, `NM0004` for a missing binding, `NM0005` for an incompatible signature, and `NM0006` for duplicate binding IDs with incompatible signatures.

- [ ] **Step 1: Write failing tests** using `OperationSignature("Test.Rotate", SemanticTypes.Angle, SemanticTypes.Angle, SemanticTypes.Scalar)`. Assert success with an exact export and bound delegate; assert each code above for the corresponding broken input. For `NM0005`, keep the ID but reverse the ordered input types. Assert equivalent requirements from two descriptor IDs coalesce and reversed input order yields identical diagnostics. Add a side-effect counter delegate and assert composition never calls it. An unused binding should be accepted.

```csharp
var expected = new OperationSignature("Test.Rotate", SemanticTypes.Angle,
    SemanticTypes.Angle, SemanticTypes.Scalar);
var reversed = new OperationSignature("Test.Rotate", SemanticTypes.Angle,
    SemanticTypes.Scalar, SemanticTypes.Angle);
var descriptorRequiringExpected = new ModuleDescriptor("Test", null,
    new SemanticModule("Test", [], [], [expected]), [], [expected]);
var binding = new HostOperationBinding(reversed, (Func<object?>)(() => null));
Assert.False(ModuleComposer.TryCompose([descriptorRequiringExpected], [binding], out _, out var errors));
Assert.Contains(errors, d => d.Code == "NM0005");
```

- [ ] **Step 2: Run** the `ModuleComposerTests` filter. Expect host tests to fail on missing validation.
- [ ] **Step 3: Implement** deterministic ordinal grouping by operation ID for required signatures and provided bindings. Compare the required signature with the catalog export and binding via structural `Equals`; accumulate diagnostic codes above with descriptor owners. Accept equivalent repeated requirements, allow extra bindings, and store successful bindings in a read-only dictionary. Never invoke `Handler`.

```csharp
foreach (var requirement in requirements.OrderBy(r => r.Signature.Id, StringComparer.Ordinal))
{
    var id = requirement.Signature.Id;
    if (!language.SemanticCatalog.Operations.TryGetValue(id, out var exported))
        errors.Add(new CompositionDiagnostic("NM0003", [requirement.Owner], $"Operation '{id}' is not exported."));
    else if (!exported.Equals(requirement.Signature))
        errors.Add(new CompositionDiagnostic("NM0005", [requirement.Owner], $"Operation '{id}' has an incompatible export signature."));
    else if (!bindingsById.TryGetValue(id, out var binding))
        errors.Add(new CompositionDiagnostic("NM0004", [requirement.Owner], $"Operation '{id}' has no host binding."));
    else if (!binding.Signature.Equals(requirement.Signature))
        errors.Add(new CompositionDiagnostic("NM0005", [requirement.Owner], $"Operation '{id}' has an incompatible host signature."));
}
```
- [ ] **Step 4: Rerun** the `ModuleComposerTests` filter. Expect all host cases to pass; run the `SemanticCatalogTests` filter again.
- [ ] **Step 5: Commit** the two Task 3 files with message `[codex/244-nitrogen-module-contract] Validate required host bindings`.

### Task 4: Surface syntax conflicts and prove an end-to-end fixture

**Files:**
- Modify: `Nitrogen/Nitrogen.Runtime/ModuleComposer.cs`
- Modify: `Nitrogen/Nitrogen.Tests/Semantic/ModuleComposerTests.cs`
- Modify: `issues/244-nitrogen-module-contract.md`
- Modify: `docs/roadmap.md`

**Interfaces:**
- Consumes: `LanguageCompositionException`, `CalcModule`, `ClashModule`, `ClashCopyModule`, `Project`, `ProjectSemantics`, `HirLowering`.
- Produces: `NM0007` structured static extension conflict; valid fixture result parsed and lowered with the composed language.

- [ ] **Step 1: Write failing tests** that compose `CalcModule.Instance`, `ClashModule.Instance`, and `ClashCopyModule.Instance` through descriptors, then assert `TryCompose` returns `NM0007` with both conflicting module names. Build a successful Base/Uses fixture with one `LoweringRegistration`, parse `CrossModuleTests.Sample` with the resolved `Uses.Doc` rule, bind in `Project`, and assert `HirLowering.Lower` returns one origin-bearing root and no diagnostics.

```csharp
using var parsed = composed.Language.Parse(CrossModuleTests.Sample,
    composed.StartRules[("Uses", "Doc")]);
Assert.True(parsed.Success);
var project = new Project(composed.Language);
project.Set("sample.uses", parsed.Tree);
var lowered = HirLowering.Lower(new ProjectSemantics(project)["sample.uses"],
    composed.Language.SemanticCatalog);
Assert.Empty(lowered.Diagnostics);
Assert.Equal("sample.uses", Assert.Single(Assert.Single(lowered.Roots).Origins).Path);
```

- [ ] **Step 2: Run** the `ModuleComposerTests` filter. Expect the syntax-conflict case to fail because the current composer lets `LanguageCompositionException` escape.
- [ ] **Step 3: Implement** exception conversion around the syntax build only. Keep the original message and contributing module names in `CompositionDiagnostic("NM0007", ...)`; do not convert arbitrary exceptions or parse-time ambiguity into composition errors. Update the issue log and roadmap status with the exact test evidence after verification.

```csharp
try { success = builder.TryBuild(out built, out semanticDiagnostics); }
catch (LanguageCompositionException error)
{
    diagnostics = [new CompositionDiagnostic("NM0007", contributorIds, error.Message)];
    result = null;
    return false;
}
```
- [ ] **Step 4: Run** `dotnet test Nitrogen/Nitrogen.Tests/Nitrogen.Tests.csproj`, then `dotnet test Gravity.slnx --filter "Gate!=Extended"`, then `git diff --check`. Record counts and any baseline failures accurately. Run `dotnet test Gravity.slnx -p:RunExtendedTests=true` only if predictive-control training, `artifacts/`, or velocity-profile files changed. If Box2D dependency resolution raises `GRAV0001`, use the locally verified `Box2DSharpProject` path and report that configuration.
- [ ] **Step 5: Commit** only Task 4 files with message `[codex/244-nitrogen-module-contract] Prove composed module and record verification`.

## Completion review

- [ ] Compare the branch against the approved spec section by section, including opt-in compatibility and no delegate invocation.
- [ ] Review the complete branch diff for API misuse, diagnostics, test meaning, and unrelated changes.
- [ ] Report implementation, test evidence, and remaining risks in the issue before claiming completion. Do not merge or push without a separate user request.
