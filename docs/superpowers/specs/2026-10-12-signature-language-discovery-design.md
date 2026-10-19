# Self-describing languages and source-text augmentation

Date: 2026-10-12
Status: Written design approved on 2026-10-12; implementation plan pending review.

## Intent and agreed scope

Reduce the boilerplate required to define and use a Nitrogen language. Grammars and programs must be recognizable by a signature near the beginning of the file, independently of its filename or extension. A grammar can supply the language configuration that currently requires nitrogen.json. Opening a signed program must load its explicitly referenced local language without nitrogen.json or a generated editor extension.

Support three entry points in the first implementation: VS Code, the CLI, and .NET builds. Preserve existing .ngr files, nitrogen.json languages, generated extensions, and C# string integrations.

A native-language file must also be able to request Nitrogen diagnostics without losing its native environment. Augmentation supports both the entire source document and explicitly marked DSL regions. Workspace configuration can apply augmentation to matching files without signatures in each file. The first implementation analyzes source text; future integrations may provide native syntax trees and semantic models for C#, Java, Go, TypeScript, JavaScript, C++, Rust, and other languages.

Explicit local paths come first. Workspace language-name discovery is the immediate next delivery stage, using the same resolver boundary. Remote package discovery and native semantic adapters are separate future stages.

## Current implementation constraints

- LanguageRegistry currently chooses a language/start rule from the URI extension.
- GrammarLanguages loads workspace definitions from nitrogen.json and recompiles their grammar/helper sources.
- The main VS Code extension selects ngr, configured extensions, and supported C# string documents. It does not activate solely because an arbitrary document contains a signature.
- NitrogenGenerator accepts only .ngr AdditionalFiles. A source generator cannot discover arbitrary workspace files absent from its compiler inputs.
- Source generation currently produces parser code and grammar diagnostics. Running custom diagnostics over host/program documents is a separate responsibility.
- Existing generated extensions and the main client have ownership rules to avoid duplicate IntelliSense. New recognition must preserve that invariant.

## Three document modes

1. Grammar: a Nitrogen grammar containing syntax modules and optionally a language declaration.
2. Primary language: the selected Nitrogen language owns parsing and editor features for the document body.
3. Augmentation: the original language remains active; one or more Nitrogen analyses contribute diagnostics.

An explicit primary signature overrides extension recognition. An augmentation signature adds analysis and does not replace the native language identity. With no signature, existing extension-based behavior remains available. A malformed Nitrogen signature produces a diagnostic rather than silently falling back to another language.

## Signature syntax and recognition

Recognize a UTF-8 BOM, leading whitespace/blank lines, and leading //, /* ... */, and <!-- ... --> comments. Signatures are allowed after that prefix or inside one of its comments. The scanner operates before the target language is known. Comment delimiters are bootstrap syntax, independent of the selected grammar's trivia rules.

A standalone grammar starts with:

```text
#!nitrogen grammar
```

A standalone program selects a definition by explicit local path:

```text
// Optional documentation

#!nitrogen language "./DateCalc.syntax"

let count = 3;
count fortnights;
```

C# whole-document augmentation uses:

```csharp
// #!nitrogen augment "./rules/ProjectRules.syntax"
```

A block comment is equivalent:

```csharp
/*
#!nitrogen augment "./rules/ProjectRules.syntax"
*/
```

A Razor/HTML-comment form is:

```html
<!-- #!nitrogen augment "./rules/PageRules.syntax" -->
```

The augment directive accepts an optional input selector: document (default) or regions. For example: #!nitrogen augment "./rules/PageRules.syntax" regions. Multiple leading augmentation directives are permitted; duplicate primary/grammar selectors or incompatible modes produce diagnostics. Region directives, including a region at the beginning of a host document, delegate to region extraction and do not count as competing primary signatures.

Quoted references are paths, resolved relative to the file containing the reference. Use filesystem normalization and the platform's path identity rules. No remote fetching occurs in the initial stages. Bare language names are reserved for the next discovery stage and initially receive an unsupported-reference diagnostic.

The header is handled outside the selected parser. Mask consumed bootstrap text with whitespace while retaining every newline and original offset. Whole-document augmentation similarly masks leading augmentation directives before analysis. Ordinary host text and comments outside consumed directives remain available to its grammar. Region extraction retains an explicit source map. Diagnostic ranges must refer to original host positions, including UTF-16 columns and related locations.

A Nitrogen-looking string literal must not be treated as a file signature. An incomplete recognized signature during editing gets a localized diagnostic and must not cause unrelated documents to change language.

## Language declaration

A defining grammar contains at most one language declaration and any number of syntax modules:

```ngr
#!nitrogen grammar

language DateCalc
{
    start DateCalc.Program;
    grammars "./Common.syntax";
    namespace DateCalc.Syntax;
    sources "./DateCalcLanguage.cs", "./MathModule.cs",
            "./DateCalcEvaluator.cs", "./DateCalcFixes.cs";
}

syntax module DateCalc
{
    using Common;
    // Rules, properties, checks and lowering clauses.
}
```

The language name and start rule are required. Namespace defaults to <LanguageName>.Syntax. Additional grammar paths, helper sources, and usings are optional. The declaration also permits optional extensions and presentation settings corresponding to existing tokens/types configuration, so moving configuration into the grammar does not discard those capabilities. These fields need grammar parsing, validation, completion, navigation, and formatting support.

The root grammar is automatically part of its language. Additional grammar paths are explicit files in stage 1; resolve relative paths from their containing declaration. Grammar dependencies may contain multiple syntax modules. Module using clauses retain their existing import meaning; they do not locate files. Repeated file dependencies are deduplicated. Report missing files, dependency cycles, conflicting module/language definitions, and a missing or invalid start rule at their references/declarations.

Root grammar files with arbitrary extensions require the grammar signature for automatic recognition. Existing .ngr files remain valid without it. Files explicitly supplied as grammar dependencies or build inputs already have grammar context and can retain legacy headerless content. Compilers must separate language metadata from syntax-module input consistently in the bootstrap and self-hosted grammar paths.

Do not infer unrelated C# helpers by scanning all neighboring files. Declared sources remain explicit. A language can be loaded without helper files.

## Embedded regions

New regions in version 1 are host comment blocks. New string-literal region forms are deferred; existing C# string functionality remains compatible.

```csharp
/* #!nitrogen region "./DateCalc.syntax"
let count = 3;
count fortnights;
#!nitrogen end
*/
```

```html
<!-- #!nitrogen region "./DateCalc.syntax"
let count = 3;
count fortnights;
#!nitrogen end
-->
```

Each region identifies its own language. A region is an independent parser input and binding project in stage 1; symbols do not implicitly cross region boundaries. The body excludes host comment delimiters and directive lines. Preserve indentation and line endings in the extracted text, mapping its locations back to the host. Empty, unterminated, nested, or mismatched regions produce localized configuration/region diagnostics; nesting is not supported in version 1.

An explicit region is sufficient to request analysis of that region without a file-level augmentation signature. A regions augmentation from a signature or workspace rule activates/validates regions selecting that same resolved language; it does not reinterpret every host comment as DSL. Regions for different languages remain independent. Deduplicate the same language/region selected through both mechanisms.

Use a separate host-comment extraction boundary. Initial native-host acceptance is C# // and /* */ and Razor <!-- -->, with extraction tests that distinguish real comments from markers inside host string literals. Using host syntax for reliable comment extraction does not expose a native semantic model to augmentation rules. Other hosts can reuse compatible lexical extraction or add their own extractor without changing language resolution or diagnostic composition. Do not promise correct recognition of every host's strings/comments merely because its comment delimiters resemble C#.

## Workspace augmentation and configuration precedence

Illustrative configuration:

```json
{
  "augmentations": [
    {
      "language": "./rules/ProjectRules.syntax",
      "files": ["**/*.cs"],
      "exclude": ["**/bin/**", "**/obj/**"],
      "input": "document"
    },
    {
      "language": "./rules/PageRules.syntax",
      "files": ["**/*.razor"],
      "input": "regions"
    }
  ]
}
```

Paths and glob patterns here are relative to nitrogen.json. A configuration containing only augmentations is valid; languages is optional. Workspace rules and file signatures are additive. Multiple distinct augmentations can analyze one document. Normalize and deduplicate by resolved language-definition identity, input mode, and region identity where applicable. Intentional different analysis entry points require distinct language definitions rather than silently rerunning an identical augmentation.

A language declaration is authoritative for that self-described language. Legacy JSON language definitions still work. Workspace configuration may reference a self-described definition and supply workspace selection settings, but conflicting attempts to redefine its name, start, namespace, dependencies, sources, or presentation settings are diagnosed. Never silently merge conflicting definitions. An extension association alone does not block an explicit primary signature; an augmentation always preserves the native owner.

Respect repository/workspace boundaries and exclusions when indexing matching host documents. Editor unsaved text wins over disk. Closed matching workspace files also receive Nitrogen diagnostics through the existing workspace diagnostics flow; they must not require opening each file. Removal of a rule, signature, region, or file clears its owned diagnostics.

## Shared runtime pipeline and diagnostics

Separate the following responsibilities:

- Header reader: bootstrap recognition and parsed language references.
- Language-definition loader: declaration/configuration normalization, dependency tracking, and compilation inputs.
- Resolver: explicit local paths now; workspace names next.
- Document classifier: primary owner plus zero or more augmentations.
- Input extractor: whole document or mapped comment regions.
- Analysis runner: existing Nitrogen parse, bind, semantic checks, and diagnostics over source inputs.
- Diagnostic aggregator: combine primary Nitrogen diagnostics, augmentation diagnostics, and loading failures for each host URI.

Use the existing compiler/workspace/semantic pipeline. Do not implement a second handwritten grammar compiler or pretend source text includes native semantic information. Source text is the initial analysis input; future native-provider access attaches through an explicit capability/context boundary.

Tag diagnostics with Nitrogen and the augmentation's language identity. Within the Nitrogen server, aggregate all active contributions before publishing; publishing one augmentation must not replace another augmentation's diagnostics. VS Code's native diagnostic collections remain independent. Keep original diagnostic codes/severities and map related locations. Changes to a document remove obsolete contribution sets, and stale asynchronous results must not overwrite newer versions.

Syntax/helper/configuration changes refresh dependency-linked analyses without an LSP restart. Reuse last-good compiled language behavior when a grammar edit fails: display current compilation diagnostics, retain the previous language for analysis, and make its stale status explicit. Unresolved new references have no previous language to reuse. Removing/changing a signature must detach the old analysis instead of retaining it as last-good.

## VS Code integration

The main extension recognizes signed documents and regions even when no nitrogen.json or generated extension exists. Activate early enough to inspect already-open and newly opened supported text documents, including untitled documents, without eagerly starting a server for every unrelated file. Resolve relative references in untitled documents only when a single workspace root supplies an unambiguous base; otherwise request/save-to-establish-location through a diagnostic rather than guessing among roots.

Primary recognition may assign a Nitrogen document identity. Augmentation preserves the native VS Code language ID and contributes diagnostics only in version 1; it must not install competing completion, formatting, coloring, or navigation providers over the native document. Existing embedded C# functionality keeps its established routing. Generated extensions and the main extension must negotiate exactly one Nitrogen owner per analysis/document; native tooling is a separate owner and remains enabled.

A shared header semantics contract and conformance fixtures must keep client-side lightweight detection consistent with server classification. Watch resolved dependencies/configuration and applicable workspace candidates, rather than restricting notifications to .ngr. Source maps must survive unsaved text and line-ending changes.

## CLI integration

Support a source-driven parse/check flow that can obtain the language and start rule from a program's header:

```text
nitrogen parse example.txt
nitrogen check Page.razor
```

Extend the existing parse command compatibly; check is the proposed diagnostics-only entry point for primary and augmented documents. Explicit existing grammar/start arguments continue to work. Incompatible explicit arguments and a self-described configuration produce a clear conflict diagnostic.

The CLI and LSP use the same resolver, normalization, region extraction, and analysis. Check supports a document and a configured workspace, reports mapped locations and augmentation identities, and exits nonzero on Nitrogen errors. No remote dependency installation or native semantic adapter is implied.

## .NET build integration

Deliver two cooperating pieces:

1. Extend NitrogenGenerator to recognize signed grammar AdditionalFiles regardless of extension and to honor the shared language declaration. Preserve existing .ngr and metadata behavior; diagnose conflicting explicit namespace/configuration rather than silently selecting a different editor/build namespace.
2. Provide a project-level MSBuild integration for input registration and source-text checks. Users enable it once rather than listing every grammar/program individually. It supplies eligible project items as AdditionalFiles, excluding build/generated output, and keeps all dependency files visible to the incremental generator. Resolve dependency contents from supplied compiler inputs; do not hide untracked filesystem reads inside the source generator.

Grammar source generation and document augmentation are separate operations. Run source-text checks through the shared runtime/CLI in an MSBuild target, not by pretending the current netstandard source generator can execute GrammarWorkspace analysis. Make the runtime/tool dependency explicit in the build integration. Execute diagnostics after required compilation inputs/helpers are available, avoid recursive builds, and report errors/warnings against original .cs/.razor/program paths. Do not modify host source files or replace the native compiler/Razor targets. No configured/signed analysis means no augmentation execution.

Build analysis covers declared project inputs and its resolved dependencies. Editor workspace analysis may include additional matching non-project files; differences in input sets must be explicit, while results for the same source/configuration must agree. Changes to a signature, definition, dependency, helper, region, or nitrogen.json must invalidate the appropriate build inputs.

## Immediate next stage: workspace discovery

After explicit-path support is accepted across all three entry points, add language-name lookup as the next delivery stage, not an unrelated redesign:

```text
#!nitrogen language DateCalc
```

Index signed grammar language declarations within configured workspace roots and exclusions. Maintain this index as files are added, changed, renamed, and deleted. Resolve names only when exactly one definition matches; report missing/ambiguous names and candidate paths otherwise. Explicit paths remain available to disambiguate. Build integration uses its registered project/dependency definitions rather than scanning unrelated directories; matching definition sets must resolve identically across hosts.

Use normalized definition identity for caching, dependency graphs, and deduplication from stage 1, so name resolution changes only how a definition is found. No network/catalog package fetching belongs in this stage.

## Future native providers

Native-provider design follows source-text support and workspace discovery. Providers advertise capabilities such as native syntax, symbol lookup, type information, and native diagnostics. Rules explicitly requiring unavailable capabilities receive a capability diagnostic instead of silently executing with incomplete data. Source-text rules need no native provider.

Preserve access to provider-specific models rather than flattening every compiler into a universal AST. Shared operations can be added where their contracts truly agree. Do not assume an editor language server exposes its in-process syntax tree or semantic model through standard LSP. Each adapter must define its actual access mechanism, supported hosts, version/lifetime semantics, and position mapping.

Roslyn is one possible C# provider; the architecture must not assume all providers are Roslyn or .NET objects. Java, Go, TypeScript/JavaScript, C++, Rust, and additional providers remain separate deliverables. Non-.NET build systems are future host integrations, not part of the initial .NET build support.

## Acceptance and verification

Stage 1 must demonstrate:

- An arbitrary-extension grammar and extensionless/signed program work without nitrogen.json or a generated extension.
- Leading BOM, blank lines, and supported comments work consistently; malformed/incomplete signatures and false positives in strings are covered.
- Multiple syntax modules, explicit grammar dependencies, relative paths, helper sources, default namespace, optional presentation, missing starts, duplicate definitions, and dependency cycles are checked.
- VS Code, CLI, and build use the same definition and produce matching mapped diagnostics for equivalent inputs.
- C# and Razor retain native diagnostics while receiving whole-document and region diagnostics; native language ID and providers remain intact.
- Workspace glob rules cover closed matching files and exclusions; overlapping rules/signatures/regions do not duplicate results.
- Region boundaries, marker-like text in native strings, UTF-16 positions, CRLF/LF, unsaved edits, related locations, and malformed regions are tested.
- Header/configuration/dependency edits reclassify or refresh documents, clear stale diagnostics, and reject obsolete analysis results without restarting the LSP.
- Legacy grammar/configuration, generated plugins, C# strings, and duplicate-provider regression tests continue passing.
- .NET incremental builds track registered grammar/dependency inputs and report source-text augmentation diagnostics at original paths.
- README examples demonstrate the minimal language project and native augmentation setup; live VS Code verification checks both native and Nitrogen diagnostics on the same documents.

Use focused tests while developing, then the complete .NET suite, VS Code tests/build, packaging/build integration checks, and real editor verification appropriate to these changed entry points. Native compiler diagnostics and Nitrogen diagnostics must be independently identifiable in the evidence.

## Delivery and review

Stage 1: shared source-driven language definition/resolution, signatures, VS Code/CLI/.NET integration, whole-document and comment-region augmentation, workspace augmentation settings, compatibility, and documentation.

Stage 2: workspace language-name discovery using the stage-1 resolver and identity model.

Later: native-provider contracts/adapters, additional host/build integrations, additional region forms, and package discovery.

This spec records the proposed decisions from brainstorming. Review and approve the written spec before preparing an implementation plan. No product code, dependency installation, release, or new PR is part of this design-only change.
