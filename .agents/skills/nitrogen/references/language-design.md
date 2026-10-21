# Designing a Nitrogen language

Use this reference when designing or extending a DSL. Start with the domain meaning and choose supported Nitrogen mechanisms that express it. In a Nitrogen checkout, consult `docs/language-guide.md` for clause syntax and `README.md` for typed pattern support; the guide's older quote-helper section still lists pattern matching as unsupported. Representative grammars and tests establish executable behavior. Distinguish implemented features from proposed contracts.

## Begin with programs and their meaning

Write a small valid program, its expected typed result or host output, and invalid examples with expected diagnostic locations. State units, identities, effects, and value constraints before choosing keywords. Include a composition or reuse example when reuse is part of the requirement. These examples define the first runnable slice; additional syntax should serve an observed authoring need.

## Choose the abstraction

| Need | Existing mechanism | Boundary to check |
| --- | --- | --- |
| A domain primitive | `OperationSignature` in a `SemanticModule`, declarative `lowers`, and an exact host binding | Typing describes a contract; the host implements its behavior and domain checks. |
| A parameterized definition authored in the DSL | `lowers template Body(Params)` and `lowers expand Name(Args)` | Parameters have declared types; calls resolve through project binding. Check argument types/counts, cycles, and definition/use origins. |
| Syntax sugar for existing language constructs | `rewrites <[ ... ]>` with `$Field` or `..$Items` | Expansion uses syntax structure. Repeated holes can repeat evaluation; verify equivalence for effects and deferred operations. |
| Reusable grammar-authored syntax construction | `syntax function` with typed syntax parameters and a quoted result, called from `rewrites` | This reuses syntax construction, distinct from a user program's bound semantic template. Parameter and return categories must match exactly. |
| Recognizing syntax before constructing a replacement | `Language.CompilePattern` / `QuotePattern.Match`, or ordered `match` arms in a syntax function | Matching is structural, not semantic equivalence. Grammar match helpers require exactly one final `_` fallback; captures are local to their arm and cannot shadow helper parameters. |
| Syntax construction from C# | `Language.CompileQuote`, typed node/list holes, and instantiation | Construction does not bind, check, or execute. Inserted names bind freshly in the destination `Project`. |
| Repeated typed output | `lowers repeat` and structured `HirProjector` projection | Preserve the nested sequence result, iterator scope, count rules, and domain checks. |

Prefer declarative typing/lowering for supported contracts; use semantic properties and checks for additional value constraints. Keep domain behavior in the domain's semantic operations and host adapter. Introduce new language machinery only when existing mechanisms cannot express the required behavior.

For example, DateCalc's `3 fortnights` can rewrite to `3 * 2 weeks`: it introduces convenient spelling over existing meaning. A new domain primitive needs its own typed contract and implementation. A user-defined reusable calculation needs parameters and binding, which declarative templates supply.

## Decide binding and expansion rules

Specify lexical versus file scope, declaration order, shadowing, duplicate handling, and cross-file visibility. `in file` selects file scope; `export` makes a declaration visible across project documents. Use `sequential` when a name should become visible after its declaration and later declarations should replace it. Test initializers, self/forward references, nested scopes, and ambiguous exports as applicable.

Current rewrite templates cannot declare names. Template-authored names resolve against language built-ins; inherited `in` properties are not forwarded into holes. C# quotation also rejects template-authored declarations and does not capture symbols. Treat these as current limits when selecting a mechanism, rather than assuming a general hygienic macro system. Keep quotation results alive while a project uses their trees; retain supplied source origins and remap diagnostics when needed. `Tree.Text` is a coordinate buffer, not a round-trip printer.

Syntax patterns compare exact token spelling and ordered structure, ignoring trivia while retaining parentheses and wrappers. Match arms inspect original selector syntax before expansion or lowering. Multiple variable-length segments are allowed per physical list; matching returns the first complete result with shortest spans tried first. `$_` discards one node and `..$_` discards zero or more list items, with site-inferred categories and no binding. Repeated capture names require structural equality. Match work is bounded by `QuoteMatchOptions` (default 1,000,000 steps); exhaustion is `NQP0006`, not a normal mismatch. Read-only patterns can recognize declarations, but construction quotes cannot author them. C# captures borrow the input tree, so consume them before disposing it and use the same language snapshot for matching and construction. A normal mismatch has no diagnostics or partial captures. Validate every grammar helper pattern and result, including unused arms and helpers.

## Compose explicitly

Keep syntax imports/extensions, semantic module imports, project symbol exports, and host bindings distinct. Verify each affected language includes the required grammar modules, semantic exports, start rule, and exact operation implementations.

Configured languages generate `<LanguageName>Composition.CreateBuilder()` and `.Start`. Prefer those stable entrypoints over manually enumerating syntax modules. Add semantics explicitly; the factory selects syntax membership and does not discover semantic providers. Generator-only consumers must register configuration and selected grammars as `AdditionalFiles`.

Extracting grammar definitions can change generated C# identities and quotation category names. Validate supported consumers and composition after a move; extraction alone does not create an independently usable package.

## Configure the project for IDE use

Creating a project language definition includes its IDE configuration, including for prototypes. Add or extend `nitrogen.json` at the project/workspace root the IDE will open; preserve other language entries. Use the current Nitrogen configuration schema and a working repository example rather than inventing keys.

Register the language name, DSL file extensions, all participating grammar paths and the actual `Module.StartRule`. Include `namespace` and `sources` when helper C# code supplies syntax-dependent semantics, checks or editor services. Use paths relative to the configuration and verify that helper dependencies also compile through the editor loader; a normal `.csproj` build does not establish that. Keep generator-only `AdditionalFiles` registration consistent with the configuration and grammars.

Verify that the configured language loads through Nitrogen's workspace/LSP path and that a representative DSL file is recognized with useful diagnostics. Where available, check completion and navigation against its real bindings. Document the project folder to open and any required Nitrogen extension/server setup; for a generated VS Code extension using bundled grammar, explain its workspace development-mode setting when editing the language. Do not install or change global IDE extensions/settings unless requested. Report configuration/LSP checks separately from live IDE verification, including any unverified features or blocked setup.

## Make diagnostics and editing part of acceptance

Preserve source origins through lowering and expansion. Check that an error identifies the useful authored location, including definition and use context. Completion, navigation, hover, and rename should agree with binding and types. For editor changes, reproduce the actual interaction in the consuming language; distinguish automated checks from live UI verification.

Prove one complete path: compose → parse → bind → semantic/type/value checks → typed HIR → projection or evaluation → expected result. Include meaningful failure cases and verify rejection before effects where applicable. Partial subtree checking and successful quotation construction are intermediate results; complete validation is required before admitting output or invoking effects. Generalize only after the slice demonstrates the intended behavior.
