# Extract Grammar Definitions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for native execution, or superpowers:subagent-driven-development if the user selects that approach. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Move selected complete rules or alternatives into a new reusable `.ngr` module through a reviewed VS Code workspace edit.

**Architecture:** Parsed declarations and resolved identities drive extraction; source slices preserve authored text. The service prepares and validates a virtual workspace change, while VS Code collects destination information and previews/applies versioned edits. Validation compiles generated code and configured helpers without loading or executing the resulting assembly.

**Tech Stack:** C#, existing Nitrogen grammar analysis/generation, Roslyn compilation, LSP, TypeScript and vscode-languageclient, xUnit and Node tests.

**Spec:** `docs/superpowers/specs/2026-10-13-extract-grammar-definitions-design.md`

## Global Constraints

- Move selected definitions only; leave their dependencies in place.
- Create a new syntax module in a new file using existing composition and extension mechanisms.
- Quote functions, syntax-pattern matching, arbitrary expression fragments, and automatic movement of dependencies are separate future work.
- Do not run evaluation handlers or arbitrary user operations during validation.
- Destination creation must fail rather than overwrite an existing file.
- Use the editor's workspace-edit preview/application and undo facilities; do not write files directly from the server.
- No LSP restart is required after applying extraction.
- Keep the uncommitted Units demo and unrelated DateCalc editor settings outside any extraction-specific commit or PR.
- Changes to generated C# identities are not binary-compatible by default; unsupported consumer updates disable the action.

## Review Focus

1. A comment or string containing a rule name must never be rewritten as a reference (Task 2).
2. A new file covered by a broad grammar glob must not add alternatives to unrelated language compositions (Task 3).
3. Editing a dependent file while the destination picker is open must invalidate the proposal (Tasks 3–4).
4. Case-equivalent paths, symlinks escaping the workspace, or an existing destination must not overwrite user content (Tasks 3–5).
5. A helper with a side-effecting static initializer must not execute merely because a preview is prepared (Task 3).

## File responsibilities

- `Nitrogen.Grammar/GrammarExtraction.cs`: selection normalization, dependency/reference identity analysis, source-preserving grammar transformation.
- `Nitrogen.Grammar/GrammarExtractionTypes.cs`: immutable analysis inputs/results, exported grammar symbol mappings.
- `Nitrogen.Workspace/GrammarCompilationValidation.cs`: compile-only validation using the same generator/options/references as workspace compilation.
- `Nitrogen.Workspace/GrammarWorkspace.cs`: factor the existing compile preparation into a shared path; retain normal runtime loading behavior.
- `Nitrogen.LanguageService/ExtractionTypes.cs`: service proposal/request/edit contracts.
- `Nitrogen.LanguageService/NitrogenLanguageService.Extraction.cs`: current-buffer snapshot, proposal lifetime, composition/config changes and revalidation.
- `Nitrogen.LanguageService/Lsp/LspMessages.cs`, `LspServer.cs`: capability-aware extraction actions, requests and versioned resource edits.
- `editors/vscode/src/extractGrammar.ts`: destination/module collection, preview and application.
- `editors/vscode/src/extension.ts`, `package.json`: register command and tests.
- Focused tests beside the corresponding existing grammar/workspace/language-service/client tests.
- `docs/editor-support.md`: usage, limitations, examples.

## Task 1: Select complete definitions and preserve their source

**Files:** Create grammar extraction files and `Nitrogen.Tests/Grammar/GrammarExtractionTests.cs`.

**Interfaces:** Define these public records in `Nitrogen.Grammar` (arrays use `IReadOnlyList<T>`):

```csharp
public sealed record ExtractionSource(string Uri, string Text);
public sealed record ExtractionProblem(string Code, string Message, string Uri, GrammarSpan Span);
public enum ExtractionShape { Rules, Alternatives }
public sealed record ExtractionSelection(string Uri, string Module, ExtractionShape Shape,
    string? ExtensionTarget, IReadOnlyList<GrammarSpan> Definitions, GrammarSpan Removal);
public sealed record ExtractionSelectionResult(ExtractionSelection? Selection,
    IReadOnlyList<ExtractionProblem> Problems);
public static partial class GrammarExtraction
{
    public static ExtractionSelectionResult Select(ExtractionSource source, GrammarSpan selection);
}
```

- [x] Add a failing xUnit test using `GrammarExtraction.Select(new("file:///source.ngr", "syntax module A { syntax Word = \"x\"; }"), new(18, 18))`; assert one complete `Rules` definition owned by `A`. Check exact offsets against the literal before pinning the assertion.
- [x] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~GrammarExtractionTests`; confirm missing API is the initial failure.
- [x] Parse with `GrammarParser.Parse`, inspect `ModuleDecl.Rules`, `ExtensibleRule.Alternatives`, and `ModuleDecl.Extends`, and normalize whitespace-only selection margins. Never select by a matching substring alone.
- [x] Add tests for multiple contiguous rules, tokens, whole extensible rules, alternatives in both declaration shapes, partial selections, mixed shapes, crossed modules and recovery diagnostics. Assert refusal contains a specific explanation.
- [x] Implement leading-comment attachment using lexer/parser trivia boundaries: include directly attached comments, retain ambiguous intervening comments, preserve CRLF/LF and all semantic/lowering text. Assert exact moved and retained source slices.
- [x] Run the focused tests and review only Task 1 changes. Prepare a scoped commit only if commits are authorized; never stage the Units demo by directory-wide commands.

## Task 2: Transform modules using resolved dependencies

**Files:** Extend grammar extraction files; extend grammar extraction tests.

**Interfaces:**

```csharp
public sealed record ExtractionTextEdit(string Uri, GrammarSpan Span, string NewText);
public sealed record ExtractionSymbolMove(string Before, string After);
public sealed record ExtractionTransform(string DestinationText,
    IReadOnlyList<ExtractionTextEdit> Edits, IReadOnlyList<ExtractionSymbolMove> Symbols,
    IReadOnlyList<ExtractionProblem> Problems);
// On GrammarExtraction:
public static ExtractionTransform Transform(IReadOnlyList<ExtractionSource> sources,
    ExtractionSelection selection, string destinationUri, string destinationModule);
```

- [x] Write failing tests extracting `syntax Word = "x";` from `A` to `Shared`, and extracting an alternative into `extend syntax A.Expr`. Assert exact destination wrapper and updated references.
- [x] Run focused tests to confirm missing transformation behavior fails.
- [x] Use `GrammarAnalysis.Resolve` internally to map references before editing. Resolve implicit extension-owner lookups and imported names. Update `RefExpr` spans and grammar-owned reference clauses according to symbol identity, never global string replacement.
- [x] Resolve semantic-block dependencies through existing grammar/helper indexes or mark the proposal unavailable when their ownership cannot be established. Preserve attributes on the original extension point. Do not move symbol kinds/properties implicitly.
- [x] Add references to retained dependencies through qualification/imports. Preserve self-reference and mutual references among moved rules; update retained consumers and extension targets. Keep an empty source module if referenced.
- [x] Test same-spelling rules in different modules, quoted literals/comments with those names, imported references, token exclusions, rewrite quotes and semantic/lowering clauses. Refuse unsupported embedded reference rewrites instead of leaving a wrong target.
- [x] Compare before/after extension order from grammar analysis, mapping moved symbol identities; refuse a composition that cannot preserve the original order. Test collisions and cycles against existing validators rather than inventing a second cycle policy.
- [x] Run grammar extraction and existing cross-module/navigation-related grammar tests; review scoped changes.

## Task 3: Build a validated service proposal without runtime execution

**Files:** New workspace validation and service extraction files; modify `GrammarWorkspace.cs`; add `Nitrogen.Tests/Workspace/ExtractionValidationTests.cs` and `Nitrogen.Tests/LanguageService/GrammarExtractionServiceTests.cs`.

**Interfaces:** Define service contracts:

```csharp
public sealed record ExtractionPreparation(string? Id, string? SuggestedModule,
    string? SuggestedFileName, string? UnavailableReason);
public sealed record ExtractionRequest(string Id, string DestinationUri, string Module);
public sealed record ExtractionDocumentChange(string Uri, int? Version,
    IReadOnlyList<DocumentEdit> Edits);
public sealed record ExtractionProposal(string DestinationUri, string DestinationText,
    IReadOnlyList<ExtractionDocumentChange> Changes,
    IReadOnlyList<string> Notices, string? UnavailableReason);
// On NitrogenLanguageService:
public ExtractionPreparation PrepareExtraction(string uri, DocumentRange range);
public ExtractionProposal ResolveExtraction(ExtractionRequest request);
```

`DocumentEdit(DocumentRange Range, string NewText)` is the existing service edit type in
`ServiceTypes.cs`; reuse it. Keep these extraction records independent
of LSP serialization. A proposal with `UnavailableReason` has no applicable edits.

- [x] Write failing service tests with temporary workspace files and `service.Open` buffers. Prepare extraction, resolve a new destination, and assert source/destination/reference edits reflect unsaved text.
- [x] Factor workspace compilation so a compile-only entry point shares grammar generation, namespace options, references and helper source compilation, but stops before assembly load/reflection. Return diagnostics and generated source metadata; normal `Compile` still performs its existing load path.
- [x] Test a configured helper whose static initializer writes a sentinel file. Preparing/resolving an extraction must leave the sentinel absent. Test broken generated-kind references produce an unavailable reason.
- [x] Store a bounded proposal snapshot keyed by an opaque ID: all participating buffers' versions, closed-file content hashes, config text and affected composition identities. Invalidate on any participating change, workspace reconfiguration or disposal. Expire old IDs with a small bounded cache; missing IDs require preparation again.
- [x] Resolve every language composition containing the source, update explicit grammar lists and moved configured start rules, and avoid redundant glob additions. Preserve JSON comments/format when supported by the actual configuration reader; do not reserialize unrelated settings.
- [x] Enumerate compositions that would acquire the destination through discovery/globs. Reject a destination that silently changes an unrelated composition. Test one shared source used by two languages and an unrelated glob consumer.
- [x] Canonicalize the new path against its existing parent, require `.ngr`, reject workspace escapes and existing case-equivalent files, and recheck existence at resolution. Do not create directories or files during proposal generation.
- [x] Compile each affected virtual composition without executing user helpers. Refuse new grammar/helper errors, unresolved references, unsupported API consumers and ordering changes. Explain known external-consumer limitations in notices. If a baseline composition is already invalid, refuse with its diagnostic instead of claiming behavioral preservation.
- [x] Test stale source/dependent/config buffers, changed closed files, existing destinations and symlink escapes. Run focused workspace/service tests and review scoped changes.

## Task 4: Expose extraction through LSP

**Files:** Modify LSP messages/server; add `Nitrogen.Tests/LanguageService/GrammarExtractionLspTests.cs`.

**Interfaces:** JSON-RPC requests `nitrogen/prepareGrammarExtraction` with document URI/range, and `nitrogen/resolveGrammarExtraction` with the service request plus client capability checks. The latter returns a code action with a versioned `WorkspaceEdit`, or a structured unavailable reason. Code-action command ID: `nitrogen.extractGrammar`.

- [x] Write failing protocol tests for `refactor.extract` and the custom prepare/resolve requests. Keep existing quick-fix tests unchanged as a regression gate.
- [x] Extend code-action context to honor `only`; allow refactor actions without diagnostics and command-bearing actions without an eager edit. Advertise `quickfix` and `refactor.extract`.
- [x] Add LSP `documentChanges` support with `CreateFile` (`overwrite: false`, `ignoreIfExists: false`) and versioned text edits, while preserving rename/quickfix `changes` behavior. Register all new payloads with source-generated JSON serialization.
- [x] Read client workspace resource-operation/document-change capabilities during initialization. Unsupported clients receive disabled/unavailable extraction rather than a malformed edit.
- [x] Adapt current service proposal to resource edits: create destination, populate its text, then apply source/reference/config edits with captured versions. Reject stale requests and destination races before returning the edit.
- [x] Test JSON round trips, unsupported clients, nonmatching `only`, stale proposals and complete multi-file payloads. Run extraction and existing LSP quick-fix/rename tests; review scoped changes.

## Task 5: Collect inputs and preview the edit in VS Code

**Files:** Create `extractGrammar.ts` and `extractGrammar.test.ts`; modify extension activation/package contributions and test script.

**Interfaces:** Export `registerGrammarExtraction(context: ExtensionContext, client: LanguageClient): void`; isolate prompt/request/preview orchestration behind injected functions so Node tests do not require a running VS Code process.

- [x] Write failing orchestration tests for cancellation at each prompt, accepted path/name, unavailable proposals, and version changes while prompts are open. Assert cancellation makes no apply request.
- [x] Register `nitrogen.extractGrammar` as a command and consume code-action arguments; direct command invocation uses the active selection. Ask the service for defaults, show a save dialog and module-name input, then resolve the request.
- [x] Use the language client's workspace-edit conversion, preserving version checks. Reviewed amendment: native entry confirmation allows unsafe subset selection. Show immutable before/after documents through the documented `vscode.changes` command, collect **Apply all changes** or **Cancel**, re-resolve the snapshot, then apply the complete converted edit without entry confirmation metadata. Verify minimum-supported-editor behavior and actual preview in the live acceptance gate; do not depend on internal commands or silently apply without review. Resource edits can partially fail: report application failure accurately and retain normal editor recovery/undo.
- [x] Respect refusal/cancellation and surface notices, including generated API/external-consumer limits, before application. Check apply results; open the destination only after successful application. Dispose registrations through the extension context.
- [x] Add the new Node test entry to the existing package test script. Run `npm test` and `npm run compile` from `editors/vscode`; review scoped changes.

## Task 6: Verify behavior and document the refactoring

**Files:** New `Nitrogen.Tests/LanguageService/GrammarExtractionParityTests.cs`; update `docs/editor-support.md` and the extraction spec status only after verification.

- [x] Add before/after integration fixtures with tokens, ordinary/extensible rules, alternative moves, binding attributes, lowering and multiple grammar consumers. Apply the proposal to an isolated temporary copy.
- [x] Assert equal representative parse behavior/precedence, equivalent binding targets under owner mapping, diagnostic code/message equivalence and HIR operation equivalence. Compare semantic origins through the move mapping rather than raw node-kind integers or old file offsets.
- [x] Reuse the same language service for the application and assert definition/reference navigation reaches the destination with updated spans. Test no restart and no stale diagnostics after configuration/file updates.
- [x] Run `dotnet build Nitrogen.slnx -warnaserror`, `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`, and the VS Code checks. Record exact results; do not treat compiler success as a live editor acceptance result.
- [x] Verify VS Code live: select a complete definition, preview all touched files, cancel once, apply once, inspect navigation and generated-language behavior, then undo and inspect restoration. Verify an existing destination is refused. Use a disposable workspace and CUA for native UI.
- [x] Document rules versus alternatives, dependencies left in place, generated API limitations, supported selection and destinations, refusal explanations, and preview/undo. Keep quote functions/pattern matching identified as future work.
- [x] Run `git diff --check`, inspect only extraction-related changes, and prepare a scoped review. Do not commit, push or publish unrelated work; request-specific commit/PR instructions govern integration.

## Plan self-review

All spec sections map to Tasks 1–6. The five Review Focus cases each have explicit tests.
No new grammar syntax or dependency was introduced. Core transformation remains usable
without VS Code, and server-side proposal generation never creates files. Generated API
breakage and composition ordering are refusals, not silent best-effort edits. The read-only changes view and complete application/undo passed live acceptance on the installed VS Code. Minimum-version compatibility uses documented APIs; the older editor binary was not separately exercised.

## Verification record (2026-10-13)

- `dotnet build Nitrogen.slnx -warnaserror`: 0 warnings, 0 errors.
- `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`: 1,525 passed, 0 failed.
- VS Code `npm test`: 32 passed; `npm run compile` and VSIX packaging passed.
- Source/configuration symlink refusal also passed RED then GREEN regression checks.
- Fresh final review found rewrite-category preservation, imported extension resolution,
  and qualified reference/rename gaps. Regression tests failed first, then passed.
- Live disposable VS Code workspace: destination and review cancellation, all-file
  preview, complete application, F12 to new file, extracted grammar changes updating
  sample diagnostics without restart, complete undo, and existing-file refusal verified.
- `git diff --check`: clean. Units demo and DateCalc settings remain unrelated dirty work.
- No commit, push, PR or release was requested for this implementation.
