# Signature Language Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recognize Nitrogen grammars and programs by a leading signature, add source-text diagnostics to native files, and then immediately add workspace language-name discovery.

**Architecture:** Put the header reader and language declaration in Nitrogen.Grammar, which also supplies sources to the netstandard2.0 generator. Nitrogen.Workspace owns normalized definitions, classification, extraction, dependency tracking, and parse/bind/check analysis; LSP, CLI, and build checking consume that pipeline. Deliver explicit local paths across all three hosts first, then add workspace name resolution without replacing these contracts.

**Tech Stack:** C#, existing .NET 10 runtime projects, netstandard2.0 Grammar/Generator, Roslyn 4.12, xUnit, TypeScript/VS Code LanguageClient, MSBuild, existing Nitrogen parser and semantic runtime.

**Spec:** [Approved design](../specs/2026-10-12-signature-language-discovery-design.md)

## Global Constraints

- Work only in `/Users/dmitrikamenetski/work/Nitrogen`; preserve `examples/DateCalc/.vscode/settings.json` and all unrelated edits.
- A grammar has one language declaration, multiple syntax modules, and an explicit start rule. Default generated namespace is `<LanguageName>.Syntax`.
- Allow BOM, blank lines, and comments before a signature. Recognized malformed signatures produce diagnostics.
- Existing `.ngr`, `nitrogen.json`, generated clients, explicit CLI arguments, and C# string integration remain compatible.
- Explicit augmentation preserves native language IDs/providers and supplies diagnostics only in v1.
- All source locations use UTF-16 offsets and retain original line endings; map primary and related diagnostic locations.
- V1 supports local references, whole-document augmentation, and explicit comment-block regions; each region has an independent semantic project.
- Full runtime analysis uses the existing parse/bind/check pipeline. Do not execute runtime/helper checking inside the netstandard2.0 generator.
- Generator dependencies come from supplied AdditionalFiles. Build checking uses registered project/dependency inputs; editor discovery stays inside workspace roots/exclusions.
- Future native semantic models require provider adapters; v1 adds no Roslyn-specific public model requirement or fake universal AST.
- Commit each verified task using explicit paths. Existing Git-derived build numbering handles each commit; do not add a second counter.
- Stage 2 workspace name discovery follows the Stage 1 acceptance gate immediately. No remote package discovery.

## Review Focus

1. Signature-looking text in native strings must not activate Nitrogen; C# raw/verbatim strings and Razor embedded strings need negative tests (Tasks 1, 4).
2. Non-BMP characters, CRLF, multiple regions, and related locations must map exactly to the host file (Tasks 4, 5).
3. Removing/changing a signature or rule during an in-flight check must clear old owned diagnostics and reject stale results (Task 6).
4. Overlapping signatures/globs, excluded closed files, and duplicate language names must have deterministic behavior without duplicate diagnostics (Tasks 3, 5, 12).
5. Generator/build inputs must be reproducible without undeclared disk reads or recursive builds; native compilation and packaged consumers must still work (Tasks 8, 9, 10).

---

## File and contract map

Paths below are relative to the Nitrogen root. New files are split by responsibility rather than enlarging GrammarLanguages or the main service class.

| Area | Files | Responsibility |
|---|---|---|
| Bootstrap | `Nitrogen.Grammar/DocumentSignatures.cs`, `LanguageDeclaration.cs`; existing `GrammarModel.cs`, `GrammarParser.cs`, lexer/validator/dumper | Signature recognition, declarations, diagnostics, netstandard2.0 compatibility |
| Self hosting | `Nitrogen.Ngr/Nitrogen.ngr`, `GrammarSemantics.cs`, `NgrProjector.cs` | The same declaration through real generated parsing/lowering |
| Definition loading | `Nitrogen.Workspace/LanguageDefinitions.cs`, `LanguageDefinitionLoader.cs`, `LanguageDefinitionResolver.cs`, `WorkspaceLanguageIndex.cs` | Canonical IDs, inputs, dependencies, name resolution |
| Source analysis | `Nitrogen.Workspace/DocumentClassification.cs`, `SourceProjection.cs`, `HostCommentExtractor.cs`, `DocumentAnalysisSession.cs`, `AugmentationConfiguration.cs` | Classification, host extraction, shared checking, incremental ownership |
| LSP | `Nitrogen.LanguageService/NitrogenLanguageService.DocumentAnalysis.cs`, existing service lifecycle, `GrammarLanguages.cs`, `LanguageRegistry.cs`, `Lsp/LspServer.cs` | Adapt shared results, primary features, native diagnostic coexistence |
| VS Code | `editors/vscode/src/signatureDocuments.ts`, `extension.ts`, `package.json`; generated-client renderer/resources | Activation, ownership, routing, watchers |
| CLI | `Nitrogen.Cli/DocumentCheckCommand.cs`, existing `CliOptions.cs`, `NitrogenCli.cs`, `GrammarSession.cs` | Inferred parse/check and workspace checking |
| Generator | existing `Nitrogen.Generator/NitrogenGenerator.cs`, generator props; new generator tests | Supplied signed grammar recognition |
| Build package | `Nitrogen.Build/Nitrogen.Build.csproj`, `build/Nitrogen.Build.props`, `build/Nitrogen.Build.targets`, `eng/build-signature-smoke.sh` | Opt-in project inputs and bundled CLI checker |
| Examples/docs | `examples/Signatures/`, root `README.md`, editor README, packaging workflows/smoke | Working no-config and native augmentation examples |

The verified generator entry is `Nitrogen.Generator/NitrogenGenerator.cs`; retain its existing class and incremental pipeline. No mass file moves.

### Shared value types (introduced by Tasks 1–5)

Use these concrete contracts, preserving existing public constructors with optional additions rather than changing legacy callers:

```csharp
// Nitrogen.Grammar, arrays are EquatableArray<T> to match the bootstrap model.
public enum SignatureMode { Grammar, Language, Augment }
public enum AnalysisInputMode { Document, Regions }
public sealed record LanguageReference(string Value, bool IsPath);
public sealed record DocumentSignature(SignatureMode Mode, LanguageReference? Reference,
    AnalysisInputMode Input, int Start, int Length);
public sealed record SignatureReadResult(EquatableArray<DocumentSignature> Signatures,
    string MaskedText, EquatableArray<GrammarDiagnostic> Diagnostics);
public static class DocumentSignatures
{
    public static SignatureReadResult Read(string text);
}
public sealed record LanguageDecl(string Name, string Start, string? Namespace,
    EquatableArray<string> Grammars, EquatableArray<string> Sources,
    EquatableArray<string> Usings, EquatableArray<string> Extensions,
    EquatableArray<LanguagePresentationOption> Presentation);
public sealed record LanguagePresentationOption(string Category, string Name, string Value);
// Add Languages as an optional second GrammarFile constructor parameter.

// Nitrogen.Workspace; immutable records, offsets always UTF-16.
public sealed record DocumentInput(string Path, string Text, long Version);
public sealed record InputSnapshot(string Root, IReadOnlyDictionary<string, DocumentInput> Files,
    bool RegisteredInputsOnly);
public sealed record AnalysisLocation(string Path, int Start, int Length);
public sealed record AnalysisIssue(string Code, string Message, AnalysisLocation Location,
    IReadOnlyList<AnalysisLocation> Related, string Source, bool IsError);
public sealed record LanguageDefinition(string Id, string Name, string Start,
    string GeneratedNamespace, IReadOnlyList<DocumentInput> Grammars,
    IReadOnlyList<DocumentInput> Sources, IReadOnlyList<string> Usings,
    IReadOnlyList<string> Extensions, IReadOnlyList<LanguagePresentationOption> Presentation);
public sealed record DefinitionResult(LanguageDefinition? Definition,
    IReadOnlyList<AnalysisIssue> Diagnostics);
public interface ILanguageDefinitionResolver
{
    DefinitionResult Resolve(LanguageReference reference, string originPath, InputSnapshot inputs);
}
public sealed record AnalysisSelection(string Key, LanguageDefinition Definition,
    AnalysisInputMode Input, bool IsPrimary);
public sealed record ClassificationResult(IReadOnlyList<AnalysisSelection> Selections,
    IReadOnlyList<AnalysisIssue> Diagnostics);
public sealed record SourceProjection(string HostPath, string Text, int HostStart)
{
    public AnalysisLocation Map(int start, int length) => new(HostPath, HostStart + start, length);
}
public sealed record ProjectedInput(AnalysisSelection Selection, SourceProjection Projection);
public sealed record ProjectionResult(IReadOnlyList<ProjectedInput> Inputs,
    IReadOnlyList<AnalysisIssue> Diagnostics);
public sealed record DocumentAnalysisResult(string Path, long Version,
    IReadOnlyList<AnalysisIssue> Diagnostics);
```

The records above describe interfaces, not a new parser/compiler. Adapt existing semantic diagnostics to AnalysisIssue; keep native provider models outside these contracts. Constructors exposed only for internal tests can remain internal with the existing friend assembly pattern.

## Stage 1 — Explicit local paths across all hosts

### Task 1: Recognize and mask leading signatures

**Files:** Create `Nitrogen.Grammar/DocumentSignatures.cs`; create `Nitrogen.Tests/Grammar/DocumentSignatureTests.cs`.
**Interfaces:** Produce `DocumentSignatures.Read(string) -> SignatureReadResult` and the signature types above. Read generic leading trivia and supported comment wrappers; host-aware exclusion of embedded strings belongs to Task 4.

- [ ] Add a failing xUnit test exercising CRLF, BOM, and a wrapped directive:

```csharp
[Fact]
public void Wrapped_header_keeps_offsets()
{
    const string text = "\uFEFF\r\n// doc\r\n// #!nitrogen augment \"./Rules.syntax\"\r\nclass C {}";
    var result = DocumentSignatures.Read(text);
    Assert.Empty(result.Diagnostics);
    Assert.Equal(SignatureMode.Augment, Assert.Single(result.Signatures).Mode);
    Assert.Equal(text.Length, result.MaskedText.Length);
    Assert.Equal(text.IndexOf("class", StringComparison.Ordinal),
        result.MaskedText.IndexOf("class", StringComparison.Ordinal));
    Assert.Equal(text.Count(c => c == '\n'), result.MaskedText.Count(c => c == '\n'));
}
```

- [ ] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~DocumentSignatureTests`; expect missing reader/types, then implement scanning by index. Mask only directive/header characters with spaces, retaining `\r` and `\n`. Parse quoted paths, bare names, `document`/`regions`; reject incompatible primary signatures and malformed recognized directives with original spans.
- [ ] Add theories for all three wrappers, EOF, multiple augmentations, escaped quotes, unsupported suffixes, and a signature after real code. Assert `Read("var s = \"#!nitrogen language X\";").Signatures` is empty.
- [ ] Rerun the focused tests and `dotnet build Nitrogen.Generator`; require pass, including linked bootstrap sources.
- [ ] Commit only the new reader/tests: `feat: read offset-preserving Nitrogen signatures`.

### Task 2: Add language declarations to both grammar parsers

**Files:** Create `LanguageDeclaration.cs`; modify `GrammarModel.cs`, `GrammarParser.cs`, `GrammarLexer.cs`, `GrammarValidator.cs`, `GrammarDumper.cs`, `Nitrogen.Ngr/Nitrogen.ngr`, `GrammarSemantics.cs`, `NgrProjector.cs`; extend `GrammarParserTests.cs` and `SelfHostingTests.cs`.
**Interfaces:** Consume masked text from Task 1; produce `GrammarFile.Languages` containing `LanguageDecl`. Existing module-only GrammarFile construction remains valid.

- [ ] Add the bootstrap regression:

```csharp
[Fact]
public void Language_declaration_defines_start_and_sources()
{
    var parsed = GrammarParser.Parse("""
        language DateCalc {
            start DateCalc.Program;
            sources "./DateCalcLanguage.cs";
        }
        syntax module DateCalc { syntax Program = "hello"; }
        """);
    Assert.True(parsed.Success);
    var declaration = Assert.Single(parsed.File!.Languages);
    Assert.Equal("DateCalc.Program", declaration.Start);
    Assert.Equal("./DateCalcLanguage.cs", Assert.Single(declaration.Sources));
}
```

- [ ] Run the test to see failure. Extend actual tokens/declarations and validator; parse all spec settings, not just the test subset. Missing start, duplicate declarations/settings, and invalid start/namespace receive scoped diagnostics. Keep module parsing and dump output stable when no declaration exists.
- [ ] Add typed HIR lowerings for declaration/options in Nitrogen.ngr and GrammarSemantics, then project them in NgrProjector. Extend existing self-hosting parity tests with the exact declaration above plus every optional setting; compare complete projected/bootstrap GrammarFile and diagnostics.
- [ ] Extend grammar-language service tests for completion of declaration keywords/options, navigation from start/module and helper paths, and formatting round trips. Update the existing Ngr language metadata/formatting adapters where generated grammar support alone does not supply these features.
- [ ] Run grammar, self-hosting, declaration editor-feature, and formatting tests. Review any generated/snapshot differences; no handwritten AST shortcut.
- [ ] Commit explicit changed files: `feat: declare language metadata in Nitrogen grammars`.

### Task 3: Normalize definitions and resolve local dependencies

**Files:** Create Workspace `LanguageDefinitions.cs`, `LanguageDefinitionLoader.cs`, `LanguageDefinitionResolver.cs`, `AugmentationConfiguration.cs`; create `Nitrogen.Tests/Workspace/LanguageDefinitionTests.cs`; modify configuration adapters in `GrammarLanguages.cs` and `LanguagePluginModel.cs`.
**Interfaces:** Produce the definition/result/snapshot/resolver types above, `LanguageDefinitionLoader.Load(string rootPath, InputSnapshot inputs)`, and `AugmentationConfiguration.Parse(string path, string text)`. Preserve legacy JSON language entries; allow augment-only JSON.

- [ ] Write an in-memory missing-dependency test, then run it red:

```csharp
[Fact]
public void Missing_root_is_diagnostic_without_disk_fallback()
{
    var root = Path.GetFullPath("signature-tests");
    var inputs = new InputSnapshot(root, new Dictionary<string, DocumentInput>(), true);
    var resolver = new LanguageDefinitionResolver();
    var result = resolver.Resolve(new LanguageReference("./Missing.syntax", true),
        Path.Combine(root, "sample.txt"), inputs);
    Assert.Null(result.Definition);
    Assert.NotEmpty(result.Diagnostics);
}
```

- [ ] Implement canonical absolute identity with platform path comparison, origin-relative paths, root implicit inclusion, transitive grammar/helper dependencies, and cycle detection. Load supplied text first; adapters, not resolver internals, acquire eligible disk files. Default namespace is `declaration.Namespace ?? declaration.Name + ".Syntax"`.
- [ ] Add tests for A→B→A, same file through normalized aliases, unsaved dependency overrides, missing helpers, multiple modules, JSON/declaration conflicts, all presentation settings, and legacy namespace behavior. Bare names in Stage 1 produce a specific unsupported-name diagnostic.
- [ ] Parse augmentation arrays with `language`, `files`, `exclude`, and `input`; test augment-only config and invalid values. Paths/globs resolve against config directory, exclusions win; use a documented glob matcher with `**` behavior rather than suffix checks. Assert overlapping selectors have one canonical definition/input key.
- [ ] Run focused definition/config tests and existing plugin configuration tests; commit: `feat: resolve local language definitions and augmentation settings`.

### Task 4: Extract native comments and mapped region bodies

**Files:** Create Workspace `HostCommentExtractor.cs`, `SourceProjection.cs`; create `Nitrogen.Tests/Workspace/SourceProjectionTests.cs`.
**Interfaces:** Produce `HostCommentExtractor.Read(string path, string text) -> IReadOnlyList<SourceProjection>` (comment contents mapped to host), and `RegionExtractor.Extract(DocumentInput input, ILanguageDefinitionResolver resolver, InputSnapshot inputs) -> ProjectionResult`; put RegionExtractor in `SourceProjection.cs` initially. Standalone regions resolve their own language and return ProjectedInput values, retaining the resolved selection beside each body. Task 5 consumes these selections rather than resolving a region a second time.

- [ ] Write and run a red source-map test:

```csharp
[Fact]
public void Projection_maps_utf16_after_non_bmp_text()
{
    const string host = "// 😀\r\n/* #!nitrogen region \"./Calc.syntax\"\r\nbad\r\n#!nitrogen end\r\n*/";
    int body = host.IndexOf("bad", StringComparison.Ordinal);
    var projection = new SourceProjection("Page.cs", "bad\r\n", body);
    Assert.Equal(new AnalysisLocation("Page.cs", body, 3), projection.Map(0, 3));
}
```

- [ ] Use Roslyn lexical trivia for C# comments so regular/verbatim/raw/interpolated strings are excluded. Implement a focused Razor/HTML comment extractor that skips embedded C# string contexts; use existing Razor parsing dependencies if present, otherwise add a compatible Razor syntax dependency rather than pretending a delimiter regex is host-aware. Keep extraction behind path/host adapters; this does not expose a native semantic API.
- [ ] Parse region/end lines inside supported block comments; output exact body substrings and host offsets. Add end-to-end extraction tests for two C# regions, Razor regions, empty body, nested markers, missing end, mismatched wrapper, markers inside strings, and `*/` ending the host comment before a DSL end marker. Assert localized errors and no neighboring-file contamination.
- [ ] Verify a region with CRLF and supplementary Unicode maps both primary and related locations. Verify C# `""" /* #!nitrogen region ... */ """` and Razor embedded strings yield no regions.
- [ ] Run projection/extraction tests; commit: `feat: extract source-mapped Nitrogen comment regions`.

### Task 5: Build shared classification and analysis

**Files:** Create Workspace `DocumentClassification.cs`, `DocumentAnalysisSession.cs`; create `Nitrogen.Tests/Workspace/DocumentAnalysisTests.cs`; adapt `GrammarWorkspace.cs` only where shared compilation access requires it.
**Interfaces:** Produce `DocumentClassifier.Classify(DocumentInput input, InputSnapshot inputs) -> ClassificationResult` (constructed with resolver/config), and `DocumentAnalysisSession.AnalyzeAsync(DocumentInput input, InputSnapshot inputs, CancellationToken cancellationToken) -> Task<DocumentAnalysisResult>`; session owns compilation cache and is disposable. Consume Tasks 1–4; retain compiled languages/start rules internally for primary LSP feature adapters.

- [ ] Add a test constructing an InputSnapshot with a signed grammar, helper, and signed `.txt` program; assert parser and declared check diagnostics are returned. Start with a minimal literal grammar/check copied from existing semantic-check fixtures, so failure proves real bind/check execution rather than header parsing alone.
- [ ] Run `dotnet test Nitrogen.Tests --filter FullyQualifiedName~DocumentAnalysisTests`; expect missing classifier/session. Implement selection precedence: primary signature, then legacy extension; explicit augmentations plus matching config rules are additive. Standalone regions also select definitions. Deduplicate by canonical language ID, input mode, and actual region span.
- [ ] Implement analysis through GrammarWorkspace, Language.Parse, Project, and existing ProjectSemantics diagnostic traversal. Mask headers for whole-document inputs; use exact region bodies for region inputs. Map diagnostics and related locations through SourceProjection. Do not share binding state between regions.
- [ ] Add tests for native text rejected by a whole-document grammar, the same text with two valid regions, no cross-region name binding, duplicate signature/JSON selection, two independent augmentation languages, and excluded closed files. `regions` selectors only select explicit regions of the same definition; ordinary comments remain untouched.
- [ ] Run analysis and existing Workspace semantics tests; commit: `feat: share signed document classification and semantic analysis`.

### Task 6: Integrate LSP lifecycle, ownership, and primary features

**Files:** Create `NitrogenLanguageService.DocumentAnalysis.cs`; modify lifecycle in `NitrogenLanguageService.cs`, `GrammarLanguages.cs`, `LanguageRegistry.cs`, `Lsp/LspServer.cs`; create `Nitrogen.Tests/LanguageService/SignedDocumentTests.cs` and `AugmentationLspTests.cs`.
**Interfaces:** Adapt shared results into existing ServiceDiagnostic/LSP types with an optional source tag; extension-based TryFind remains usable. Primary signed documents use the existing completion/navigation/hover/binding features through their resolved language; augmentation supplies diagnostics only.

- [ ] Add protocol tests opening a signed arbitrary-extension program with no config and requesting definition/completion; add native `.cs` augmentation publishing a check diagnostic with its original URI/range and a language-specific Nitrogen source. Run tests red.
- [ ] Route open/change/close and watched dependencies/config through shared classification/session. Aggregate all Nitrogen contributions before publishDiagnostics; keep unrelated native diagnostic collections separate. Analyze closed matching workspace files using existing index boundaries and current unsaved text precedence.
- [ ] Implement an epoch per document/classification plus dependency generation; publish only when both remain current. Preserve last-good compiled language after a failed grammar/helper edit and expose a diagnostic/status identifying stale analysis. Detach old layers immediately on signature removal/change.
- [ ] Use controlled TaskCompletionSource barriers in tests: start version 1, reclassify version 2, finish version 2 then version 1, assert no version-1 publication. Cover delete/rename, rule removal, syntax-error recovery, unrelated dependency edits, and closing an unsaved document.
- [ ] Run SignedDocument, AugmentationLsp, existing LspServer, WorkspaceIndex, and navigation tests; commit: `feat: serve signed languages and native augmentation over LSP`.

### Task 7: Activate and route VS Code documents without taking native ownership

**Files:** Create `editors/vscode/src/signatureDocuments.ts` and its test; modify `extension.ts`, `package.json`, existing ownership helpers/tests, `Nitrogen.Cli/VsCode/VsCodeRenderer.cs`, and CLI embedded-resource list as needed.
**Interfaces:** Produce `signatureDocumentMode(text: string): 'grammar' | 'language' | 'augment' | undefined` as a cheap activation hint only; authoritative classification stays server-side. Main and generated clients retain one Nitrogen owner per primary document.

- [ ] Add a TypeScript unit test:

```typescript
assert.equal(signatureDocumentMode('// #!nitrogen augment "./Rules.syntax"\nclass C {}'), 'augment');
assert.equal(signatureDocumentMode('#!nitrogen language "./Calc.syntax"\n1 + 2'), 'language');
assert.equal(signatureDocumentMode('const s = "#!nitrogen language Calc";'), undefined);
```

- [ ] Run `npm test` in `editors/vscode` red. Add startup activation sufficient for no-config arbitrary-extension files, inspect already-open documents, and react to content changes. Use a generic contributed Nitrogen language ID for primary signed programs, preserve/restore prior document IDs when signatures disappear, and leave augmentation documents as C#/Razor. Verify supported VS Code APIs against official documentation during implementation.
- [ ] Broaden client document synchronization to eligible workspace files; route expensive primary providers only when selected. Middleware returns no Nitrogen primary result for augmentation-only files. Native providers continue normally. Watch actual grammar/helper/config dependencies, not only `.ngr`; debounce and honor exclusions.
- [ ] Extend main/generated-client ownership tests to mixed installation, augmentation, signature insertion/removal, initial startup, and multi-root workspaces. Generated clients reuse the same routing/ownership logic; embed any imported shared template source in CLI packaging.
- [ ] Run TypeScript tests/build and generated VS Code tests; commit: `feat: recognize signed documents in the main VS Code extension`.

### Task 8: Recognize signed AdditionalFiles in the source generator

**Files:** Modify existing generator entry and `Nitrogen.Generator/build/Nitrogen.Generator.props`; create `Nitrogen.Tests/Grammar/SignedGeneratorTests.cs` and reference the generator as a test assembly if required without losing analyzer reference behavior.
**Interfaces:** Consume shared reader/declaration, AdditionalText content, analyzer config namespace metadata, and supplied dependency inputs. Produce normal generated syntax sources/diagnostics; no Workspace/runtime dependency.

- [ ] Add a Roslyn GeneratorDriver test with an in-memory `.syntax` AdditionalText whose contents start `#!nitrogen grammar`, include the declaration, and a syntax module. Assert generated namespace from declaration, generated module presence, and zero generator errors. Run red.
- [ ] Filter candidates by legacy `.ngr` or recognized grammar signature. Resolve declaration grammar dependencies exclusively against supplied AdditionalFiles. Preserve original offsets by masking signature text and preserve incremental invalidation on source changes.
- [ ] Add tests for missing supplied dependency even when a same-named disk file exists, declaration/Namespace conflict, duplicate roots, arbitrary unsigned file ignored, legacy RootNamespace fallback, and changing a signed file/dependency without recreating the driver.
- [ ] Run generator and self-hosting tests plus `dotnet build Nitrogen.Generator`; require netstandard2.0 unchanged.
- [ ] Commit: `feat: generate parsers from signed grammar inputs`.

### Task 9: Infer CLI parse/check definitions and support registered input manifests

**Files:** Create `Nitrogen.Cli/DocumentCheckCommand.cs`; modify `CliOptions.cs`, `NitrogenCli.cs`, `GrammarSession.cs`; extend `CliOptionsTests.cs`, `CliTests.cs`.
**Interfaces:** Add `check` command and `--inputs <manifest.json>` for build integration. Manifest schema: `{ "root": "absolute-root", "files": ["absolute-path"], "documents": ["absolute-path"] }`; files are all registered readable inputs, documents are analysis targets. CLI reads these into InputSnapshot with RegisteredInputsOnly=true. Normal file/workspace checking builds a workspace-bounded snapshot.

- [ ] Add CLI tests for `parse signed.txt`, `check Page.razor`, and a registered-input manifest excluding an existing on-disk dependency. Assert inferred start, original mapped locations, nonzero check errors, and exclusion behavior. Run focused tests red.
- [ ] Route inference/check through the shared resolver/session. Keep explicit `--grammar`/`--start` parse/watch unchanged; diagnose incompatible signature/explicit settings rather than choosing silently. `check <directory>` enumerates eligible signed/config-selected documents under exclusions.
- [ ] Emit MSBuild-readable `path(line,column): error CODE: message` output for manifest mode and normal concise CLI diagnostics otherwise. Cancellation stops checking; argument errors=2, analysis errors=1, success=0. Never recursively build helper projects; consume declared source files through existing GrammarWorkspace compilation.
- [ ] Add conflict, malformed header, empty workspace, related-location, closed-file, and missing-helper tests. Run CLI, workspace analysis, and generated-plugin tests.
- [ ] Commit: `feat: infer CLI language selection and check native sources`.

### Task 10: Package opt-in .NET build integration

**Files:** Create `Nitrogen.Build/Nitrogen.Build.csproj`, `Nitrogen.Build/WriteAnalysisManifest.cs`, `Nitrogen.Build/build/Nitrogen.Build.props`, `Nitrogen.Build/build/Nitrogen.Build.targets`, `eng/build-signature-smoke.sh`; modify `Nitrogen.slnx`, `.github/workflows/packages.yml`, `eng/package-smoke.sh`.
**Interfaces:** One `Nitrogen.Build` PackageReference enables integration; `NitrogenEnabled` defaults true in that package and may be disabled. Package depends on matching Generator/Runtime versions and bundles the CLI's framework-dependent publish output under `tools/net10.0/any/`. Publish CLI during pack using a dedicated intermediate directory; do not assume developer output paths. No globally installed CLI required.

- [ ] Add an external temporary consumer smoke script referencing local-feed packages, a signed arbitrary-extension grammar, helper source, native C# host, and signed program; run against current packages to fail because Build is absent.
- [ ] Implement props/targets to collect registered Compile/None/Content/explicit AdditionalFiles inputs, excluding bin/obj and generated files; include candidates once as AdditionalFiles. Track supplied grammar/helper/config inputs through MSBuild incremental Inputs/Outputs. Runtime scanning determines signatures; do not use filename extension as the only grammar filter.
- [ ] Before CoreCompile, write the manifest using an MSBuild task with JSON serialization (not shell-escaped JSON), then invoke the bundled CLI via the current dotnet host. Write the task in `Nitrogen.Build/WriteAnalysisManifest.cs`, reference Microsoft.Build.Framework/Utilities.Core as private build dependencies, and package task dependencies beside its assembly. Execute only when signed/configured analysis exists; the preparatory scan may run on eligible inputs but unconfigured projects launch no checker. Never call Build from the check target.
- [ ] Run consumers proving: successful native compilation plus Nitrogen checking; mapped region errors fail the build; changing grammar/helper/config rechecks; missing registered dependency fails even if present elsewhere; no signatures/config launches no checker; disable flag bypasses integration; unsigned legacy generator consumers still build. Test paths with spaces and quotes and Windows-compatible invocation.
- [ ] Add Build to package CI and smoke pack loops. Run package smoke from a local feed; inspect nupkg for bundled runtimeconfig/deps/helper dependencies. Commit only integration/packaging files: `feat: add packaged Nitrogen build checking`.

### Task 11: Document examples and pass the Stage 1 acceptance gate

**Files:** Create `examples/Signatures/README.md`, `DateCalc.syntax`, `sample.txt`, `NativeChecks.syntax`, `Host.cs`, `Page.razor`, `nitrogen.json`, and `Signatures.csproj`; modify root/editor READMEs and example inclusion in tests as needed.
**Interfaces:** Demonstrate the exact user-visible syntax and installation paths; reuse DateCalc semantics/helpers through explicit relative source declarations rather than copying a second handwritten evaluator.

- [ ] Add no-config explicit-path sample parsing to CLI integration tests; add native document/region sample checking with expected diagnostic codes. Run red before writing examples.
- [ ] Write runnable examples for signed grammar/program, C# whole-file augmentation, C#/Razor regions, workspace glob augmentation, and one-package build integration. README gives start/declaration defaults, comment/BOM rules, precedence/conflicts, exclusions, diagnostics-only native behavior, helper dependencies, runtime requirements, and troubleshooting stale last-good analysis.
- [ ] Run `dotnet test Nitrogen.slnx`, VS Code `npm test`/build, existing build-identity checks, and package/build smoke. Record any baseline failures distinctly; no claim of a passing full gate without outputs.
- [ ] In real VS Code verify arbitrary-extension primary navigation, native C# and Razor IDs, simultaneous native/Nitrogen errors, no duplicate hints/providers with generated clients, signature removal, and grammar/helper save without restart. If GUI verification is unavailable, record that gap explicitly and do not call it verified.
- [ ] Commit examples/docs: `docs: demonstrate signed languages and native diagnostics`. Stage 1 is reviewable here; continue directly to name discovery after this gate passes.

## Stage 2 — Immediate workspace language-name discovery

### Task 12: Index declarations and resolve bare names

**Files:** Create `Nitrogen.Workspace/WorkspaceLanguageIndex.cs`; modify `LanguageDefinitionResolver.cs`, snapshot adapters, service watchers/index hooks; create `Nitrogen.Tests/Workspace/WorkspaceLanguageDiscoveryTests.cs`; extend LSP/CLI/build integration tests.
**Interfaces:** `WorkspaceLanguageIndex.Update(InputSnapshot inputs)` builds name→canonical root candidates; `Find(string name) -> IReadOnlyList<string>` returns sorted canonical paths. Resolver accepts this index and implements bare-name resolution through the same definition loader/cache. RegisteredInputsOnly uses only its supplied files; editor uses its bounded workspace snapshot.

- [ ] Add a bare-name resolution test using two signed roots with identical language names; assert no definition and diagnostics listing both canonical paths in deterministic order. Add single-root success and missing-name tests; run red.
- [ ] Index only recognized signed grammar roots with valid language declarations. Deduplicate normalized root paths; do not choose an arbitrary candidate or resolve helper/module names as language names. Use current open text over disk and preserve malformed-declaration diagnostics separately from candidate ambiguity.
- [ ] Add add/change/rename/delete and unsaved rename tests. A declaration moving from `DateCalc` to `Calendar` invalidates only dependent selections; deleted/ambiguous names detach obsolete analysis instead of preserving an incorrect last-good identity.
- [ ] Run discovery, classification, LSP, CLI, and build smoke variants using `#!nitrogen language DateCalc` and `augment ProjectRules`. Verify a build cannot discover an unrelated workspace grammar that was not registered.
- [ ] Commit: `feat: discover workspace languages by declaration name`.

### Task 13: Complete discovery documentation and final verification

**Files:** Update signature examples/root/editor READMEs; extend acceptance tests in Tasks 6–12.
**Interfaces:** Document explicit paths as deterministic selection and bare names as workspace discovery with ambiguity diagnostics; no remote packages/native model providers promised.

- [ ] Add integration regressions combining bare-name regions, JSON augmentation, rename during an in-flight check, and two workspace roots declaring the same name. Assert one owned diagnostic per selected contribution and candidate paths on ambiguity.
- [ ] Run the failing new cases, implement only the corrections they expose, and rerun the focused suites.
- [ ] Update examples to show both explicit-path and bare-name forms, explaining project-input boundaries versus editor workspace boundaries and excludes.
- [ ] Repeat the full .NET/TypeScript/package gates after discovery changes; inspect live VS Code name add/rename/delete without LSP restart. Obtain an independent final code review using the chosen execution workflow and address concrete findings.
- [ ] Commit final docs/regressions: `docs: explain workspace language discovery and boundaries`. Report actual test results, any GUI/CI gaps, branch and commits. Create a PR, bump release versions, or publish only when requested; existing per-commit build identity continues automatically.

## Execution and validation notes

- Before execution, inspect current branch/artifacts/dirty state and use the worktree skill if isolation is needed. The navigation fix inherited from PR #65 is a dependency; do not describe it as new signature implementation or silently merge/publish it.
- The plan contains API/test excerpts; flesh out executable xUnit fixtures using real in-memory inputs and existing test utilities, never replace intended behavior with a canned stub. Each task starts red and ends green before its scoped commit.
- Task 10 packages a new build integration because generator-only NuGet cannot run the net10 runtime. Check the pack/publish dependency graph once before coding; no recursive consumer build is permitted.
- Review the plan and select native or subagent-driven execution before implementation. Native is recommended because all hosts depend on the same evolving definition/classification contracts.

## Plan self-review / requirement coverage

- Header syntax and masking: Task 1. Language metadata and self-hosting parity: Task 2. Legacy settings/conflicts/dependency inputs: Task 3.
- Native extraction, string false positives, independent regions and source maps: Tasks 4–5. Shared parse/bind/check, whole-document/region/config layering: Task 5.
- LSP ownership, closed files, dependency reload, stale cancellation and last-good behavior: Task 6. Main/generated VS Code activation and native coexistence: Task 7.
- Supplied-input generator, inferred CLI, and opt-in packaged checking: Tasks 8–10. All-host examples/compatibility/acceptance: Task 11.
- Immediate discovery with candidate ambiguity and dynamic updates: Tasks 12–13. Future provider semantics remain an explicit boundary, with no speculative provider implementation.
- Review Focus items have owner tests in Tasks 1/4, 4/5, 6, 3/5/12, and 8/9/10 respectively.
