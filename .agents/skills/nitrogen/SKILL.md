---
name: nitrogen
description: Use when authoring, composing, validating, or executing Nitrogen DSL programs and semantic modules, or changing Nitrogen grammars, runtime, CLI, language service, or tests in this repository.
---

# Nitrogen

Nitrogen is a standalone .NET language workbench. `.ngr` grammars generate parsers and typed syntax views through a C# source generator; runtime semantics, typed HIR, a language server, and a VS Code client support authored languages. Use semantic contracts to decide what a program means. Treat syntax and IDE presentation as ways to express and inspect those contracts.

## Agent workflow

1. **Discover capabilities.** Find the task's grammar, `SyntaxModule`, `SemanticModule`, `ModuleDescriptor`, start rule, representative programs, and host operation bindings. Inspect exported `SemanticType`s, `OperationSignature`s, imports, lowerers, and diagnostics. A declared operation is executable only when the host supplies a matching binding.
2. **Compose the smallest language.** Prefer existing modules and operations. Include required imports and a valid start rule. Use `ModuleComposer.TryCompose(...)` or `LanguageBuilder.TryBuild(...)` as appropriate, and resolve composition diagnostics before authoring a program. Do not create duplicate type or operation identities with different contracts.
3. **Write the smallest typed program.** Follow the actual grammar. Preserve units, coordinate/reference frames, entity identities, and other domain types through every operation. Convert between them only with an explicit, declared conversion. Do not erase an `Angle`, `Time`, frame, or domain type to `Core.Scalar` merely to make validation pass. The only built-in semantic types are `Core.Scalar`, `Core.Bool`, `Core.Error`, and `Units.Angle`; confirm any other type (such as a time or frame type) in a module's exports before relying on it. Prefer declarative clauses (`lowers`, `lowers literal`, `declares … type`) for typing and lowering; they are checked against the composed catalog (`NM0008`–`NM0010`, `NT0001`–`NT0004`).
4. **Validate deterministically.** Parse, bind names, run semantic/type/value checks, lower to typed HIR where supported, inspect lowering diagnostics, and bind host operations. Run `HirPreflight.Check(...)` for its finite numeric slice; for other result types, inspect and use the host's domain-specific checks before its handler runs. Record which stages are available and passed. A successful parse alone is insufficient.
5. **Execute the validated artifact.** Invoke an evaluator or host only after all applicable checks pass and the task authorizes execution. Validate again if source, modules, inputs, or host bindings change. Host handlers may have effects; apply the host's authorization boundary before calling them.

## Repair from diagnostics

Use diagnostic code, message, and source span/origin to fix the earliest underlying cause. Repeat composition → parse/bind/semantic checks → HIR lowering → applicable preflight or domain checks after each targeted repair. Prefer fixing a missing import, wrong type, or mismatched operation signature over adding a cast or weakening a rule. If the missing concept recurs across tasks, define a reusable typed semantic module with explicit imports, exports, conversions, lowerers, and required host bindings; validate it with more than one consumer. After a bounded number of targeted repairs, report the remaining diagnostics and leave the program unexecuted.

| Symptom | Check first |
| --- | --- |
| `NC0001` / `NC0002` | Missing semantic import / import cycle |
| `NC0003`–`NC0005` | Conflicting type, operation, or lowerer |
| `NM` composition diagnostic | Descriptor, start rule, export, and exact host binding |
| `NM0008`–`NM0010` | `lowers` operation, literal or declared type, and argument count against the catalog |
| `NT0001`–`NT0004` | Argument type, type name, literal text, and missing declared type |
| Parse, binding, or type diagnostic | Grammar, symbol scope, and qualified domain type |
| `NE` preflight diagnostic | HIR node, input symbol identity/type, and host handler |

## Concrete example

The repository's Geometry language accepts the complete program `box 1 2 3;` under start rule `Geometry.Document`. `BoxMeshModule.Descriptor` exports `Geometry.Mesh` and `Geometry.BoxMesh : (Core.Scalar, Core.Scalar, Core.Scalar) → Geometry.Mesh`. A zero, negative, or nonfinite dimension fails semantic validation even if it parses. Compose the descriptor with its exact host binding, parse, inspect `ProjectSemantics` diagnostics, and lower before calling `GeometryExecutor.Execute(...)`. The Geometry executor checks the HIR shape, signature, binding, and finite scalar arguments before invoking the handler; the generic numeric `HirPreflight` does not accept `Geometry.Mesh`. See `Nitrogen.Tests/Geometry` for the current executable path.

For a new frame-aware geometry capability, first search existing exports. If absent, a *proposed* contract might use `Geometry.Vector3<Units.Length, Frames.World>` and a transform operation that states its input and result frames. Those names illustrate semantic intent; they are not verified built-in exports or `.ngr` syntax. Define and bind such a capability once, then import it wherever needed.

## When changing Nitrogen itself

- For `.ngr` changes, inspect `Nitrogen.Grammar`, `Nitrogen.Generator`, and `Nitrogen.Ngr`. Generated C# is build output; edit the grammar or generator.
- For parsing or semantics, use `Nitrogen.Runtime` APIs and preserve existing behavior unless the task calls for a migration.
- For editor behavior, trace `Nitrogen.Workspace`, `Nitrogen.LanguageService`, `Nitrogen.Cli`, and `editors/vscode` as applicable.
- Verify core changes with `dotnet build Nitrogen.slnx` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`. Keep build/test results distinct from validation of a particular DSL program.
