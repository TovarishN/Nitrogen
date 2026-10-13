# Generated Language Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Generate stable configured-language factories so Extract Block automatically preserves helpers' syntax composition.

**Architecture:** Normalize each configured language into a composition description in Nitrogen.Grammar. A shared emitter generates its syntax-only CreateBuilder and Start members; generator and live workspace adapters feed the same description. Preserve explicit semantic registration and existing extraction checks for direct-module consumers.

**Tech Stack:** C#, .NET, dependency-free netstandard2.0 grammar/analyzer code, Roslyn incremental generator, xUnit, MSBuild, VS Code LSP client.

**Spec:** `docs/superpowers/specs/2026-10-13-generated-language-composition-design.md`

## Global Constraints

- `CreateBuilder()` returns a fresh builder containing every selected syntax module exactly once.
- Factory: `<LanguageName>Composition` in the configured generated namespace; Start uses existing `Nitrogen.Rule`.
- Explicit semantic registrations; no helper execution during extraction validation.
- Source generation reads only registered inputs, never undeclared filesystem paths.
- Module-only projects retain their current generated API with no guessed factory.
- Preserve unrelated modifications, currently `docs/editor-support.md`.
- Nitrogen.Grammar stays dependency-free netstandard2.0; the analyzer remains self-contained.
- Do not change versions, publish a release, or create a PR in this implementation task.

## Review Focus

1. A selected grammar contains several modules: all belong to the factory, exactly once (Task 1).
2. Two configured languages share dependencies: factories must retain separate membership and namespaces (Task 2).
3. Moving a configured start rule changes its module: stable Start must follow the new setting (Task 4).
4. Only configuration name/start changes: the live compilation cache must invalidate (Task 3).
5. Helper or generated types collide with a factory name: fail with an actionable diagnostic rather than emit conflicting public APIs (Tasks 1–3).

## Task 1: Shared composition contract and emitter

**Files:**
- Create `Nitrogen.Grammar/LanguageComposition.cs`.
- Create `Nitrogen.Grammar/CompositionWriter.cs`.
- Create `Nitrogen.Tests/Grammar/CompositionWriterTests.cs`.
- Modify `Nitrogen.Grammar/GrammarCompiler.cs` only as needed to expose validated module emission names; retain its current Compile overload.

**Interfaces:**
- `LanguageComposition(string Name, string Start, string Namespace, EquatableArray<GrammarInput> Grammars)`.
- `CompositionWriter.Write(LanguageComposition composition)` produces `GrammarCompileResult`, using existing diagnostics/generated-source records.
- The writer validates membership/start against parsed grammar modules, deduplicates module identities, and resolves generated module class names/namespaces through existing emitter naming conventions.

- [x] Add failing tests for a factory with two modules in one grammar, shared dependency duplicates, unknown starts, invalid C# language identifiers, and factory/module-name collisions. Assert diagnostic location and no composition source on failure.

Representative fixture and output assertions:

```csharp
const string grammar = "syntax module A { syntax Program = B.Word; } syntax module B { token Word = \"ok\"; }";
var composition = new LanguageComposition("Demo", "A.Program", "Demo.Syntax",
    new[] { new GrammarInput("a.ngr", grammar, "Demo.Syntax") });
var result = CompositionWriter.Write(composition);
Assert.False(result.HasErrors);
string code = Assert.Single(result.Sources).Code;
Assert.Contains("DemoComposition", code);
Assert.Contains("global::Demo.Syntax.AModule.Instance", code);
Assert.Contains("global::Demo.Syntax.BModule.Instance", code);
Assert.Contains("global::Demo.Syntax.AModule.Program", code);
```

- [x] Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~CompositionWriterTests`; confirm the missing API fails before implementation.
- [x] Implement the record and writer with existing GrammarParser, CSharpText identifier handling, and generated module naming. Sort modules by ordinal module name, matching GrammarWorkspace. Emit a public static factory whose CreateBuilder returns a new LanguageBuilder; its Start property returns the resolved generated Rule. Do not enumerate semantic modules.

Generated shape:

```csharp
public static class DemoComposition
{
    public static global::Nitrogen.LanguageBuilder CreateBuilder() =>
        new global::Nitrogen.LanguageBuilder()
            .Add(global::Demo.Syntax.AModule.Instance)
            .Add(global::Demo.Syntax.BModule.Instance);
    public static global::Nitrogen.Rule Start => global::Demo.Syntax.AModule.Program;
}
```

- [x] Run focused tests and ensure generated C# compiles against runtime references, rather than relying only on source-text assertions.
- [x] Commit only this task's code/tests and the approved design/plan on the feature branch.

## Task 2: Registered configuration and source-generator integration

**Files:**
- Create `Nitrogen.Grammar/CompositionConfiguration.cs` for registered configuration selection.
- Modify `Nitrogen.Generator/RegisteredGrammarInputs.cs` and `Nitrogen.Generator/NitrogenGenerator.cs`.
- Modify `Nitrogen.Build/WriteAnalysisManifest.cs` and `Nitrogen.Build/build/Nitrogen.Build.targets` if required for item metadata.
- Create `Nitrogen.Tests/Grammar/CompositionGeneratorTests.cs`.
- Modify `Nitrogen.Tests/Grammar/SignedGeneratorTests.cs` source-count assertions to distinguish module and composition outputs.
- Locate existing WriteAnalysisManifest tests with `rg -n 'WriteAnalysisManifest' Nitrogen.Tests` and extend those fixtures.

**Interfaces:**
- `CompositionConfiguration.Resolve(IEnumerable<GrammarInput> registered)` returns selected composition descriptions and existing CompiledDiagnostic records.
- RegisteredGrammarInputs retains supplied-input normalization and feeds resolved compositions to CompositionWriter after grammar compilation succeeds.
- Nitrogen.Build registers relevant nitrogen.json files and their already-registered grammar dependencies as generator inputs. No hidden disk discovery.

- [x] Write failing generator tests using the AdditionalText/GeneratorDriver pattern in SignedGeneratorTests. Cover JSON configuration, signed language declarations, registered-only globs, changed config membership via ReplaceAdditionalText, overlapping shared dependencies, missing registered files, duplicate names, and explicit namespace conflicts.

Representative supplied configuration:

```json
{"languages":[
  {"name":"One","start":"A.Program","namespace":"Demo.Syntax","grammars":["a.ngr","shared.ngr"]},
  {"name":"Two","start":"B.Program","namespace":"Demo.Syntax","grammars":["b.ngr","shared.ngr"]}
]}
```

Assert OneComposition includes A/Shared and excludes B; assert TwoComposition includes B/Shared and excludes A. Run generated output through CSharpCompilation with real runtime metadata references and a helper that calls both factories.

- [x] Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter 'FullyQualifiedName~CompositionGeneratorTests|FullyQualifiedName~SignedGeneratorTests'` and record the expected failures.
- [x] Implement shared configuration normalization with no new analyzer runtime dependencies. Use a small dependency-free JSON reader restricted to decoding the existing configuration schema while correctly skipping arbitrary unrelated JSON values; test strings, escapes, nested objects, arrays, numbers, booleans, null, malformed and truncated input. Do not extract JSON fields with regular expressions. Resolve glob matches only against supplied paths, and reuse existing configuration semantics for defaults and language declarations.
- [x] Resolve factory ownership per configuration/declaration. Report ambiguous overlapping definitions instead of silently choosing. Preserve existing unconfigured module namespaces; configured namespaces must agree with emitted modules or report a conflict.
- [x] Add generator inputs for registered configurations in WriteAnalysisManifest. Test that a file merely existing beside registered inputs cannot be selected and that configuration content changes invalidate incremental generation.
- [x] Verify actual generator diagnostic locations can map to configuration inputs as well as grammar inputs.
- [x] Run focused generator/build-input tests, then commit the task's scoped files.

## Task 3: Live editor composition parity

**Files:**
- Modify `Nitrogen.Workspace/GrammarWorkspace.cs`.
- Modify `Nitrogen.Workspace/DocumentAnalysisSession.cs`.
- Modify `Nitrogen.LanguageService/GrammarLanguages.cs`.
- Modify `Nitrogen.LanguageService/NitrogenLanguageService.Quasiquotes.cs` where a configured workspace is constructed.
- Modify `Nitrogen.LanguageService/NitrogenLanguageService.Extraction.cs` validation workspace construction.
- Create `Nitrogen.Tests/Workspace/WorkspaceCompositionTests.cs`.
- Extend `Nitrogen.Tests/LanguageService/WorkspaceLanguageSemanticsTests.cs` and `Nitrogen.Tests/Workspace/DocumentAnalysisTests.cs`.

**Interfaces:**
- Add nullable `GrammarWorkspace.CompositionName` and `GrammarWorkspace.StartRule` properties; neither infers a language for unconfigured callers.
- Each configured caller supplies definition Name/Start, alongside its existing namespace/grammar inputs.
- CompileCore invokes the shared CompositionWriter with the workspace's resolved inputs.

- [x] Add failing tests compiling a helper that calls DemoComposition.CreateBuilder/Start in GrammarWorkspace. Assert a syntax-only factory stays usable with explicit semantic registrations. Test helper-type collisions and that ValidateCompilation does not execute a throwing helper initializer.
- [x] Add a document-session test changing only the configured language name/start; assert the factory recompiles, an invalid helper receives a diagnostic, and last-good serving behavior remains intact.
- [x] Run `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter 'FullyQualifiedName~WorkspaceCompositionTests|FullyQualifiedName~DocumentAnalysisTests|FullyQualifiedName~WorkspaceLanguageSemanticsTests'` and confirm missing-factory failures.
- [x] Thread composition properties through all configured workspace constructors. Extend DocumentAnalysisSession's fingerprint to include Name and Start. Emit factory diagnostics through existing workspace paths, before loading helpers.
- [x] Ensure actual runtime module ordering and factory ordering use the same ordinal ordering. Preserve existing semantic-provider discovery and custom workspace configure callbacks.
- [x] Run focused tests and commit the task's scoped files.

## Task 4: DateCalc migration and extraction regression

**Files:**
- Modify `examples/DateCalc/DateCalcEvaluator.cs` and `examples/DateCalc/DateCalc.csproj`.
- Modify `Nitrogen.Tests/LanguageService/GrammarExtractionServiceTests.cs` and `Nitrogen.Tests/LanguageService/GrammarExtractionParityTests.cs`.
- Extend `Nitrogen.Tests/DateCalc/DateCalcTests.cs` and its project AdditionalFiles registration if needed.

**Interfaces:**
- DateCalc uses DateCalcComposition.CreateBuilder and DateCalcComposition.Start.
- Existing Extract Block edits remain grammar/configuration edits; direct-module and quote-string protections stay enabled.

- [x] Add a failing extraction test with a helper using the generated factory. Resolve the extraction, apply its complete edit, compile the proposed language, and parse before/after using the stable Start member.
- [x] Add a start-rule extraction case with no moved generated view/kind consumers; assert the config start changes and Start continues to return the correct rule.
- [x] Exercise configured helper evaluation before/after extraction through the existing DateCalc test pipeline, comparing parse/binding/type/lowering diagnostics and projected values for variables and fortnights. Select a movable token/rule that has no unsupported C# generated-type consumer; retain refusals where another genuine API dependency would break.
- [x] Run focused tests and observe the failure before migration.
- [x] Register nitrogen.json plus necessary grammars as AdditionalFiles for the DateCalc project and any test project compiling its linked helpers. Replace only the explicit syntax factory and parse start:

```csharp
static readonly Lazy<Language> s_language = new(() => DateCalcComposition.CreateBuilder()
    .AddSemantic(MathModule.Semantics).AddSemantic(DateCalcLanguage.Semantics).Build());
// Within Run:
using var parsed = Language.Parse(source, DateCalcComposition.Start);
```

- [x] Run focused extraction/DateCalc tests and build `examples/DateCalc/DateCalc.csproj`. Confirm the direct-module refusal regression still passes.
- [x] Commit this task's scoped files.

## Task 5: Documentation, integration verification, and review

**Files:**
- Modify `README.md` and `docs/editor-support.md` narrowly.
- Update this plan's checkboxes with verification evidence.

- [x] Document factory API, stable Start, explicit semantics/customization, configuration registration for generator-only projects, and extraction limits. Preserve the user's existing editor-support edits by reviewing the baseline diff before editing.
- [x] Run `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`; investigate failures without weakening existing gates.
- [x] Run `python3 eng/check-links.py`, `python3 eng/test-build-identity.py`, and `git diff --check`.
- [ ] In VS Code, use an isolated DateCalc copy: extract a supported whole rule, inspect all-file preview, apply, use Go to Definition, edit the extracted rule and confirm diagnostics refresh, then undo and confirm complete restoration. Do not alter the user's open example files for this check.
- [x] Use requesting-code-review for one independent whole-branch review, address concrete findings, and rerun affected checks.
- [x] Commit scoped documentation changes and report implementation, evidence, remaining limits, branch, and commit IDs. No PR/release without a new request.

## Execution and isolation

Continue with the user's previously selected native execution method: primary agent implements tasks in order, with one whole-branch review at the end. At execution time follow using-git-worktrees to select/create an isolated checkout. The current Nitrogen checkout has an unrelated tracked editor-support edit and untracked approved design/plan; preserve those files and transfer only the approved artifacts into the implementation checkout as needed. Do not modify CsNitra.


## Execution evidence

Implemented in `/tmp/nitrogen-language-composition` on `codex/generated-language-composition`.
The plan's code tasks and independent review are complete. A shared configuration resolver
and dependency-free JSON decoder keep the analyzer self-contained. Source identities are
explicit; factory hint paths use a separate `Compositions/` prefix.

Verification on 2026-10-13: full build 0 warnings/errors, all 1,575 .NET tests passed,
32 VS Code client tests passed, client compile passed, 23 Markdown link checks passed,
build identity checks passed, and `eng/test-composition-inputs.py` passed. The latter
also failed as expected with config-selected input registration temporarily removed.
The real DateCalc regression reproduced the reported direct-module extraction refusal,
then passed with equivalent values after migrating to the generated factory.

An independent reviewer found and verified fixes for validation-before-emission and
factory collisions with sibling generated types, including module names ending in
Composition. No concrete findings remain. VS Code interaction acceptance is still
pending: the Mac was locked and CUA could not unlock it; an unlock request was sent.
No PR, merge, version change, or release was performed.
