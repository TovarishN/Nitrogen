# Scoped Parsing Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline. Steps use checkbox syntax for tracking. The user approved the concrete design and instructed implementation on2026-10-29; preserve the established inline execution method and continue without another routine plan-permission prompt.

**Goal:** Add transparent Boolean grammar contexts to Nitrogen with correct memoization/reuse, then prove CSharp5 integration without changing existing tree APIs.

**Architecture:** Module-qualified declarations compose into collision-free per-Language bits. Generated syntax tests flags or calls a child under a restored scope; complete context identity keys both caches. Runtime, compiler/self-hosted grammar and editor identity are independently verified before dependency integration.

**Tech Stack:** C#/.NET10 targets, existing Nitrogen grammar/runtime/generator and xUnit. Production clone /tmp/csn-nitrogen-scoped-context-production begins at ed69ac8aaa3bbc7c8d44c97cf969d1b6ddd89ea2.

**Spec:** docs/superpowers/specs/2026-10-29-scoped-parse-context-design.md (CSNitrogen; user approved2026-10-29).

## Global Constraints

Preserve all existing public signatures and CSharp Parameters/Body/signature types; new optional properties/virtuals may be additive. Context wrappers push no nodes/capture slots. ParseMark constructor/deconstruction/equality is unchanged. Start false, restore on success/failure/exception, no global mutable environment. Max64 Boolean flags; reject overflow/missing selected modules. Preserve sibling drafts and CSNitrogen benchmarks. No handwritten C# parser/source rewriting; no semantic/runtime or CSharp5 admission claim from syntax proof. Use actual generated grammars for compiler/integration acceptance. Quotation starts false unless its grammar entry explicitly scopes it.

## Review Focus

- Same rule/position reached under opposite contexts in a speculative alternative must not share success or failure memo entries; Task1 tests both.
- Header edits outside a reused subtree and moved graft descendants must preserve the incoming environment across successive reparses; Task1 tests equal-length edits and carry-over.
- Context-only nullable loops, hidden left recursion and scoped postfix operands must fail closed or retain correct precedence; Task2/3 test those analyses.
- Scope wrappers beneath labels/lists/optionals must preserve typed fields, slots and original UTF16 spans; Task2/4 test generated and CSharp cases.
- Missing flag-owner modules, unknown/ambiguous flags and the65th declaration must produce useful diagnostics before parsing; Task1/2/3 test composition and authored locations.

## Task 1: production runtime contexts and cache identity

**Files (Nitrogen):** new Nitrogen.Runtime/GrammarContextDecl.cs and ParserState.GrammarContext.cs; modify SyntaxModule.cs, Language.cs, LanguageBuilder.cs, MemoTable.cs, ExtensibleRules.cs, ParserState.cs, ReuseRecord.cs, ReuseTable.cs; tests GrammarContextTests.cs plus generated fixture later.

**Interfaces:** new GrammarContextDecl(SyntaxModule owner,string name), SyntaxModule.ContextDeclarations/ContextReferences virtual IReadOnlyList<GrammarContextDecl>; ParserState.TestContext(GrammarContextDecl), ParseWithContext(GrammarContextDecl,bool,delegate*<ref ParserState,bool>). Language privately maps declaration identity to a ulong bit. Internal full environment shared with cache paths. Existing APIs retain signatures.

- [ ] Write production runtime witnesses from the reviewed spike, plus missing-owner/65th/duplicate qualified identity, restoration/parallel/default-context cases. Watch RED against baseline.
- [ ] Implement declaration validation and stable per-Language mask assignment. Promote only reviewed mechanism, preserving old memo overload signatures with delegating context0 methods rather than replacing optional-parameter metadata. Correct stale XML summaries from the experiment.
- [ ] Extend positive/negative memo and all reuse/record/carry paths with context identity. Tests require full/reparse owner/span/diagnostic equality and actual unaffected-node reuse.
- [ ] Run focused runtime tests and full Nitrogen suite; commit independently tested runtime slice.

## Task 2: grammar surface, validation and generated parsing

**Files:** GrammarModel.cs (new init-only ModuleDecl.Contexts, preserving constructor/deconstruction); new GrammarContexts.cs; GrammarParser.cs, GrammarAnalysis.cs, GrammarValidator.cs, EmitModel.cs, ModuleWriter.cs, SyntaxCodeWriter.cs, ViewWriter.cs, GrammarFunctions*.cs, GrammarExtraction*.cs, CommitAnalysis.cs, CharSets.cs, TokenRegex.cs, QuotationWriter.cs and other actual expression walkers. New authored GrammarContext fixture and compiler/runtime tests.

**Interfaces:** grammar `context Flag;`, `context(Flag)` predicate and `with(Flag = true) Child` / false, composable by nesting. AST ContextTestExpr(NameDecl,GrammarSpan), ContextScopeExpr(NameDecl,bool,Expr,GrammarSpan). Resolve module-qualified/local/imported names. Generated declarations/references use Task1 interfaces; scopes emit dedicated static child helpers. Reject contexts in token matchers (which have no ParserState); context predicates in syntax may guard raw-token references.

- [ ] Add bootstrap parse/compile RED tests for declarations, tests, nested scopes and cross-module resolution; assert source spans, duplicate/unknown/ambiguous name errors and useful diagnostics.
- [ ] Parse and bind additive nodes; preserve ModuleDecl constructor. Generate Task1 calls and context declarations/references; preserve selected-module validation.
- [ ] Audit every Expr walker: scopes unwrap, tests are zero-width/no-slot; nullable repetition/left recursion validation sees tests and child; FIRST/commit conservatively inspect child and preserve precedence; token contexts rejected explicitly. Capture/view/semantics/quote analysis does not invent wrapper nodes.
- [ ] Verify generated typed labeled/list/optional fields, lookahead/backtracking, strict/recovery/nested/parallel parses, incremental changes and composition omission; test invalid nullable loops, cyclic rules and scoped expression precedence. Compile and execute real generated fixture.
- [ ] Full tests/build; commit compiler support independently.

## Task 3: self-hosted grammar, tooling identity and docs

**Files:** Nitrogen.Ngr/Nitrogen.ngr, NgrProjector.cs, GrammarSemantics.cs; grammar source/navigation/extraction tests; docs/language-guide.md and representative existing test configuration.

**Interfaces:** self-hosted parser projects exactly Task2 AST. Source context declarations/references share module-qualified identity and original spans; extraction remaps context names without textual replacement.

- [ ] Self-hosted versus bootstrap RED/green parity for valid/invalid context forms, including original ranges; register actual lowering operations used by NgrProjector.
- [ ] Add context symbol/navigation identity using existing Ngr binding patterns; verify completion/hover/definition/reference behavior through language-service tests. Extraction preserves or safely rejects unsupported cross-module context moves with a useful diagnostic.
- [ ] Document supported syntax, default false/quotation behavior, capacity, module omission and scoped tree transparency. No live-editor claim from automated tests.
- [ ] Full solution build/test, standalone generated consumer and scoped recovery/incremental stress cases; record performance/allocation/default-context versus baseline comparisons before core acceptance. Commit verified core/tooling slice and fresh whole-branch review; publish Nitrogen PR under existing authorization.

## Task 4: CSharp5 real-grammar integration proof

**Files (CSNitrogen):** dependency manifest/preparation inputs, isolated CSharp5 proof fixture/modules/tests and evidence docs; production changes only after this mechanism passes. Exact files in2026-10-23-csharp5-async-grammars.md remain authoritative for later stages.

**Interfaces:** consume reviewed Nitrogen context feature, select real30-module baseline plus scoped async-entry grammar. Preserve eight expected owners, exact operand(M()), original fields and UTF16 positions; independently removable modules retain typed fields.

- [ ] Repin reviewed dependency sources without overwriting unrelated work. Prove mixed contexts with actual generated grammar, not the synthetic runtime fixture.
- [ ] Add desired-contract RED then implement transparent async-entry branches plus raw-await spelling; include all eight paired contexts, escaped/verbatim/supplementary boundaries and both previously deferred typed-field tests.
- [ ] If existing owners/fields cannot survive the real composition, stop with concrete API decision; no wrappers/renames silently introduced. Otherwise continue Task2 production of the approved CSharp5 plan, keeping source admission separate.
- [ ] Full CSharp1–4 guards, full solution,125-or-updated dependency pins, deterministic generators, full-versus-incremental cases and measured performance; review before authorized integration. CSharp5 stays partial until independent historical-source closure.

Self-review: runtime/compiler/self-hosted/tooling/integration/performance requirements each map to a task. No task claims whole-source CSharp5 admission. Exact syntax and additive API names above are implementation choices allowed by the approved design. Tasks2–4 consume the named Task1/2 interfaces; any change is ledgered before dependent edits.
