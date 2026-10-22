# TextMate Palette Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Nitrogen language servers colour code with TextMate's richer scope palette. Every Nitrogen language also gets a generated TextMate grammar that paints the first frame the same way, so the switch to semantic tokens is invisible.

**Architecture:** One static analysis in `Nitrogen.Grammar` (`LiteralRoles`) classifies every literal by its place in its rule. The generator emits it as `SyntaxModule.GetLiteralRole`, and the TextMate writer reads the same analysis. The language service maps roles (and its existing name/HIR colours) to a fixed `Palette` of scope-named token types. Clients that opt in through `initializationOptions.paletteTokens` receive those types; every other client keeps today's standard types. VS Code extensions map palette types to scopes in `package.json`, and Rider maps them to `TextAttributesKey`s.

**Tech Stack:** C#/.NET 10 (Nitrogen.Grammar, Nitrogen.Runtime, Nitrogen.LanguageService, Nitrogen.Cli), xUnit; TypeScript VS Code extensions with `node --test`; `vscode-textmate` + `vscode-oniguruma` for scope comparison; Kotlin Rider plugin; Gravity repo (bash build, MotionDSL extension).

**Spec:** [docs/superpowers/specs/2026-10-22-textmate-palette-design.md](../specs/2026-10-22-textmate-palette-design.md)

## Global Constraints

- The server is the source of truth. Colour precedence, highest first: language assist tokens → declared/resolved names → lowering (operation words `function`, type styles, scalar constants) → grammar-shape roles → token-rule lexical classes and comments.
- Clients that do not send `paletteTokens: true` get byte-identical legends and the same token types as today. The one exception is that brackets, terminators and separators are no longer sent as `operator`, because they have no fallback type.
- Palette scope names have no language suffix in `semanticTokenScopes`. Generated TextMate scopes end in `.<languageId>` (e.g. `storage.type.policy`).
- `.ngr` keeps its hand-written [ngr.tmLanguage.json](../../../editors/vscode/syntaxes/ngr.tmLanguage.json). The server matches its exact scopes (ignoring `.ngr` and `meta.*`).
- No theme or colour customizations are shipped.
- Gravity work lands on its own branch in `~/work/Gravity`, built against this Nitrogen checkout through `NitrogenRoot`.
- Run the focused tests for each task. Run the full `dotnet test Nitrogen.Tests` before each commit that touches C#.

## Review Focus

1. **A word with different roles in different rules** (`hold` as a modifier in one rule and a section keyword in another): the server colours each occurrence by its own rule. Test in Task 3.
2. **Literals inside lists and alias rules:** a literal whose tree parent is a `List` node, or that comes from an inlined alias rule, takes the role of the nearest rule node. Tests in Tasks 1 and 3.
3. **Unaware clients:** an `initialize` without `paletteTokens` gets today's legend, and the tokens it receives use standard types only. Test in Task 2.
4. **Syntax errors:** missing/recovered leaves are not coloured, and a role lookup on a node with no rule ancestor returns null without throwing. Test in Task 3.
5. **Regex safety in generated grammars:** literal words and operators containing regex metacharacters (`..`, `?`, `*`, `(`) are escaped, and every generated pattern compiles with Oniguruma. Test in Task 4.

---

### Task 1: Literal roles from grammar shape

**Files:**
- Create: `Nitrogen.Runtime/LiteralRole.cs`
- Modify: `Nitrogen.Runtime/SyntaxModule.cs` (new virtual), `Nitrogen.Runtime/Language.cs` (routing method)
- Create: `Nitrogen.Grammar/LiteralRoles.cs`
- Modify: `Nitrogen.Grammar/ModuleWriter.cs:58-64` (emit override), `Nitrogen.Grammar/EmitModel.cs:76` (add `GetLiteralRole` to `ReservedNames`)
- Test: `Nitrogen.Tests/Grammar/LiteralRolesTests.cs`, `Nitrogen.Tests/Grammars/Palette.ngr` (new test grammar)

**Interfaces:**
- Produces:
  - `public enum LiteralRole { Operation, DeclarationKeyword, SectionKeyword, ModifierWord, ConstantWord, BlockBracket, Parenthesis, SquareBracket, Terminator, Separator, Operator }` (namespace `Nitrogen`)
  - `SyntaxModule`: `public virtual LiteralRole? GetLiteralRole(int localKind, string literal) => null;`
  - `Language`: `public LiteralRole? GetLiteralRole(int kind, string literal)`. It routes to the owning module the way `GetKindName` does and returns null for `SyntaxKinds.List`/`Literal`/`Empty`.
  - `Nitrogen.Grammar`: `internal static class LiteralRoles { public static IReadOnlyDictionary<(string Kind, string Literal), LiteralRole> Of(EmitModel model, ModuleInfo info); }`. Task 4 also uses it.

Classification of one literal occurrence in kind K (alias rules inlined into K, extensible alternatives are their own K). Apply the first matching rule:
1. Non-word literal: `{`/`}` → BlockBracket; `(`/`)` → Parenthesis; `[`/`]` → SquareBracket; `;` → Terminator; `,` → Separator; anything else → Operator.
2. K has a `Lowers` clause, and the literal is not inside a labeled element whose label is one of that clause's `Arguments` → Operation.
3. The literal is an entire alternative of a `ChoiceExpr` with at least two alternatives that are all single word literals, and that choice is not in K's first position → ConstantWord.
4. The literal can be the first token of K (it is in the FIRST set of K's body, through optional/repeated leading elements) → DeclarationKeyword if K has a `Declares` clause, otherwise SectionKeyword.
5. Otherwise → ModifierWord.

The same (K, literal) occurring twice keeps the role of its earliest occurrence. "Word" means `SemanticTokenBuilder.IsWord` (move that predicate into `Nitrogen.Runtime` as `public static bool SyntaxKinds.IsWordText(ReadOnlySpan<char>)` and use it from both places).

- [ ] **Step 1: Write `Nitrogen.Tests/Grammars/Palette.ngr`** with one rule per row: a declaring rule `syntax Goal = "goal" Name:Word "{" Items:Item* "}" declares goal Name;`, a section rule `"train" "{" ... "}"`, a modifier word after a child (`Num "sharpness" Num`), a constant choice not first (`"anchored" Frame:("xz" / "xyz")`), a lowered rule (`"policy" Name:Word lowers P.Doc(text Name)`, with the operation declared the way the existing test grammars declare one), `;`, `,`, `..`, a literal inside a list (`Items:("hold" Num)*`) and one in an alias rule. Check how an existing grammar in `Nitrogen.Tests/Grammars` declares lowering operations before writing it.
- [ ] **Step 2: Write failing tests** in `LiteralRolesTests`:
  - `Roles_follow_grammar_shape`: for each row, assert `PaletteModule.Instance.GetLiteralRole(PaletteModule.L<Kind>, "<literal>")` equals the expected role (`"goal"`→DeclarationKeyword, `"train"`→SectionKeyword, `"sharpness"`→ModifierWord, `"xz"`→ConstantWord, `"policy"`→Operation, `"{"`→BlockBracket, `";"`→Terminator, `","`→Separator, `".."`→Operator, list `"hold"`→ModifierWord, the alias rule's word under the referencing kind).
  - `Argument_words_of_a_lowered_rule_are_not_operations`: `"xz"` in a lowered rule whose `Frame` is a `text Frame` argument is ConstantWord.
  - `Unknown_literal_has_no_role`: `GetLiteralRole(L<Kind>, "nope")` is null.
- [ ] **Step 3: Run** `dotnet test Nitrogen.Tests --filter LiteralRolesTests`. Expected: build fails (`GetLiteralRole` is not defined).
- [ ] **Step 4: Implement** `LiteralRole`, the runtime virtual and routing, `LiteralRoles.Of`, and `ModuleWriter` emitting `public override global::Nitrogen.LiteralRole? GetLiteralRole(int localKind, string literal) => (localKind, literal) switch { ... _ => null };`.
- [ ] **Step 5: Run** the focused tests (PASS), then `dotnet test Nitrogen.Tests`. `GeneratedSnapshotTests` will show the new override: review the snapshot diff and update it.
- [ ] **Step 6: Commit** `Classify grammar literals by rule position (LiteralRole)`.

### Task 2: Palette and the opt-in wire format

**Files:**
- Create: `Nitrogen.LanguageService/Palette.cs`
- Modify: `Nitrogen.LanguageService/ServiceTypes.cs:20` (`SemanticToken`), `Nitrogen.LanguageService/LanguageAssist.cs:29` (`AssistToken`), `Nitrogen.LanguageService/Presentation.cs` (`SymbolStyle`), `Nitrogen.LanguageService/Lsp/SemanticTokenEncoding.cs`, `Nitrogen.LanguageService/Lsp/LspServer.cs` (initialize + semanticTokens/full), `Nitrogen.LanguageService/Lsp/LspMessages.cs` (initialization options)
- Test: `Nitrogen.Tests/LanguageService/PaletteTests.cs`, `Nitrogen.Tests/LanguageService/LspServerTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PaletteEntry(string Name, string Scope, TokenType? Fallback);`
  - `public static class Palette`:
    - `IReadOnlyList<PaletteEntry> Entries` (general entries first, `.ngr` entries appended in Task 6)
    - `PaletteEntry Named(string name)`
    - `PaletteEntry Of(LiteralRole role)`
    - `string StandardScope(TokenType type)`
  - `SemanticToken(DocumentPosition Start, int Length, TokenType Type, TokenModifiers Modifiers, string? Palette = null)`
  - `AssistToken(TextSpan Span, TokenType Type, string? Palette = null)`
  - `SymbolStyle(TokenType Token, OutlineKind Outline, string? Palette = null)`
  - `SemanticTokenEncoding.Legend(bool palette)` replaces the `Legend` property. It returns the standard names, followed by `Palette.Entries` names when `palette` is true.
  - `SemanticTokenEncoding.Encode(IReadOnlyList<SemanticToken> tokens, bool palette)`:
    - palette clients: a token with `Palette` encodes as `22 + index in Entries`;
    - non-palette clients: it encodes as `Fallback`, or is dropped when `Fallback` is null.

General entries, exactly (name → scope → fallback):

| Name | Scope | Fallback |
|---|---|---|
| `operationWord` | `entity.name.function` | Function |
| `declarationKeyword` | `storage.type` | Keyword |
| `sectionKeyword` | `keyword.other` | Keyword |
| `modifierWord` | `keyword.operator.word` | Keyword |
| `constantWord` | `constant.language` | EnumMember |
| `blockBracket` | `punctuation.section.block` | null |
| `parenthesis` | `punctuation.section.parens` | null |
| `squareBracket` | `punctuation.section.brackets` | null |
| `terminator` | `punctuation.terminator` | null |
| `separator` | `punctuation.separator` | null |
| `operator` | `keyword.operator` | Operator |

`StandardScope` uses VS Code's default semantic-token → TextMate fallbacks:

| Token type | Scope |
|---|---|
| namespace | `entity.name.namespace` |
| type | `entity.name.type` |
| class | `entity.name.type.class` |
| enum | `entity.name.type.enum` |
| interface | `entity.name.type.interface` |
| struct | `entity.name.type.struct` |
| typeParameter | `entity.name.type.parameter` |
| parameter | `variable.parameter` |
| variable | `variable.other.readwrite` |
| property | `variable.other.property` |
| enumMember | `variable.other.enummember` |
| event | `variable.other.event` |
| function | `entity.name.function` |
| method | `entity.name.function.member` |
| macro | `entity.name.function.preprocessor` |
| keyword | `keyword` |
| modifier | `storage.modifier` |
| comment | `comment` |
| string | `string` |
| number | `constant.numeric` |
| regexp | `constant.regexp` |
| operator | `keyword.operator` |

- [ ] **Step 1: Write failing tests:**
  - `PaletteTests.Every_role_has_an_entry`: every `LiteralRole` value maps through `Palette.Of`.
  - `PaletteTests.Names_are_valid_vscode_token_type_ids`: each name matches `^\w+[-_\w+]*$` and is unique.
  - `LspServerTests.Palette_legend_only_for_opted_in_clients`: an initialize with `"initializationOptions":{"paletteTokens":true}` returns a legend longer than 22 whose element 22 is `operationWord`. A plain initialize returns exactly today's 22 names.
  - `LspServerTests.Unaware_client_gets_fallback_types`: encode a token list containing a `blockBracket` token and a `declarationKeyword` token with `palette: false`. The bracket is dropped, and the keyword is encoded as `(int)TokenType.Keyword`.
- [ ] **Step 2: Run** `dotnet test Nitrogen.Tests --filter "PaletteTests|LspServerTests"`. Expected: build fails.
- [ ] **Step 3: Implement** `Palette`, the record extensions and the encoding. In `LspServer`, read `initializationOptions.paletteTokens` (absent = false) at `initialize`, keep it in a field, and pass it to `Legend` and `Encode`.
- [ ] **Step 4: Run** the focused tests (PASS), then the full suite.
- [ ] **Step 5: Commit** `Add the TextMate palette and opt-in palette token types`.

### Task 3: Token builder precedence with roles

**Files:**
- Modify: `Nitrogen.LanguageService/SemanticTokenBuilder.cs` (`Build`, `Classify`, `Lowered`)
- Test: `Nitrogen.Tests/LanguageService/PaletteTokenTests.cs`

**Interfaces:**
- Consumes: `Language.GetLiteralRole` (Task 1), `Palette.Of`, `SemanticToken.Palette`, `SymbolStyle.Palette`, `AssistToken.Palette` (Task 2).
- Produces: tokens carrying `Palette` names. Task 6's comparison and every client rely on them.

The rules:
- **Literal leaves** not covered by a name or HIR meaning: find the nearest ancestor (via `SyntaxTree.Parent`) that is not `SyntaxKinds.List`, look up `GetLiteralRole(ancestorKind, text)`, and emit `Palette.Of(role)` with `Type = entry.Fallback ?? TokenType.Operator`.
- **No role found** (null): keep today's lexical classification, without a palette name.
- **HIR operation words** carry `Palette = "operationWord"`.
- **Names** carry `SymbolStyle.Palette`.
- **Assist tokens** carry `AssistToken.Palette`.

- [ ] **Step 1: Write failing tests** against a `LanguageRegistry` holding `Palette.ngr`'s language (mirror `LanguageServiceTests.ScopesRegistry`):
  - `Literal_roles_reach_tokens`: open a sample covering every row, and assert the palette name at each literal's position.
  - `Same_word_takes_each_rules_role`: one word that is a section keyword in one rule and a modifier in another gets both palette names at its two positions.
  - `Lowering_beats_grammar_position`: `policy` in the lowered rule is `operationWord`.
  - `List_and_alias_literals_use_the_rule_role`.
  - `Recovered_document_still_colours`: a document with a missing `}` produces no token on the missing leaf and does not throw.
- [ ] **Step 2: Run** `dotnet test Nitrogen.Tests --filter PaletteTokenTests`. Expected: FAIL (no palette names).
- [ ] **Step 3: Implement** the changes in `SemanticTokenBuilder`.
- [ ] **Step 4: Run** the focused tests, then the full suite. Existing token tests that asserted `operator` on brackets will move to palette names: update them only when the new value is what the table says.
- [ ] **Step 5: Commit** `Colour literals by grammar role under lowering and names`.

### Task 4: TextMate grammar generator and CLI

**Files:**
- Create: `Nitrogen.Grammar/TextMateWriter.cs`, `Nitrogen.Grammar/TokenRegex.cs`
- Modify: `Nitrogen.Grammar/GrammarCompiler.cs` (public entry)
- Create: `Nitrogen.Cli/TextMate/TextMateCommand.cs`
- Modify: `Nitrogen.Cli/NitrogenCli.cs` (dispatch `generate textmate`)
- Test: `Nitrogen.Tests/Grammar/TextMateWriterTests.cs`, `Nitrogen.Tests/Cli/TextMateCommandTests.cs`, `editors/vscode/src/textmate.test.ts`

**Interfaces:**
- Consumes: `LiteralRoles.Of` (Task 1), `Palette.Of`/`StandardScope` (Task 2, in the CLI only; `Nitrogen.Grammar` does not reference `Nitrogen.LanguageService`).
- Produces:
  - `public sealed record TextMateOptions(string LanguageId, string ScopeName, string StartRule, IReadOnlyDictionary<LiteralRole, string> RoleScopes, IReadOnlyDictionary<string, string> DeclarationScopes);` (DeclarationScopes: symbol kind → scope)
  - `public sealed record TextMateResult(string? Json, EquatableArray<CompiledDiagnostic> Diagnostics);`
  - `GrammarCompiler.TextMate(IEnumerable<GrammarInput> inputs, TextMateOptions options) → TextMateResult`
  - `internal static class TokenRegex { public static string? TryConvert(Expr body, Func<string, Expr?> resolve); }` converts a token rule body into an Oniguruma regex, or returns null when it can't (predicates, grammar functions).
  - CLI: `nitrogen generate textmate --config <nitrogen.json> --language <name> --output <file>`. The CLI:
    - builds `RoleScopes` from `Palette.Of(role).Scope`;
    - builds `DeclarationScopes` from the language's `tokens` styles in nitrogen.json via `Palette.StandardScope`;
    - uses scope name `source.<language>`.

The `patterns` order in the generated grammar (all scopes suffixed `.<LanguageId>`):
1. Comments: `//…` → `comment.line.double-slash`, `/* … */` (begin/end) → `comment.block`.
2. Strings: each token rule whose name contains `String` or `Char`. Use the `TokenRegex` output when it is a single-line match. Otherwise fall back to begin `"` / end `"` with `\\.` escapes → `string.quoted.double`.
3. Numbers: rules whose name contains `Number`, `Digit` or `Integer`, converted the same way. Fallback `\b\d+(?:\.\d+)?\b` → `constant.numeric`.
4. Declaration names: a declaring rule whose body starts with literal L followed directly by the declared labeled field becomes `\b(L)\s+(<field regex>)` with captures `RoleScopes[DeclarationKeyword]` and `DeclarationScopes[kind]`. The field regex is the field token's `TokenRegex`, or `[A-Za-z_][A-Za-z0-9_]*`.
5. Words: one `\b(?:w1|w2|…)\b` pattern per role. A word with several roles across rules takes the first of Operation, DeclarationKeyword, SectionKeyword, ConstantWord, ModifierWord.
6. Punctuation and operators: escaped, longest first, one pattern per role.

- [ ] **Step 1: Write failing tests:**
  - `TextMateWriterTests.Generates_role_patterns`: from `Palette.ngr`'s source, the JSON has `scopeName` `source.palette`, a pattern whose `match` contains `goal` with name `storage.type.palette`, `xz` under `constant.language.palette`, `policy` under `entity.name.function.palette`, and `\.\.` (escaped) under `keyword.operator.palette`.
  - `TextMateWriterTests.Declaration_name_pattern`: `goal` + name gets captures `1` → `storage.type.palette` and `2` → the given declaration scope.
  - `TextMateWriterTests.Metacharacters_are_escaped`: a grammar with literals `?`, `*`, `(` produces patterns matching those literally (check with .NET `Regex` as a proxy).
  - `TextMateCommandTests.Writes_grammar_for_a_config_language`: run the command on `examples/DateCalc/nitrogen.json` into a temp file; exit code 0, and the file parses as JSON with `scopeName` `source.<name>`.
  - `editors/vscode/src/textmate.test.ts`: load a checked-in generated fixture (`editors/vscode/test-fixtures/palette.tmLanguage.json`, written by the CLI in step 4) with `vscode-textmate` + `vscode-oniguruma`, tokenize a sample, and assert `goal` gets `storage.type.palette`. This proves every regex compiles in Oniguruma. Add both packages as devDependencies and add the test to the `test` script.
- [ ] **Step 2: Run** `dotnet test Nitrogen.Tests --filter "TextMateWriterTests|TextMateCommandTests"`. Expected: build fails.
- [ ] **Step 3: Implement** `TokenRegex`, `TextMateWriter`, `GrammarCompiler.TextMate`, the CLI command and the dispatch (usage line next to `VsCodeInput.Usage`).
- [ ] **Step 4: Run** the C# tests (PASS). Generate the fixture with the CLI, then run `cd editors/vscode && npm test` (PASS).
- [ ] **Step 5: Commit** `Generate TextMate grammars from Nitrogen grammars`.

### Task 5: VS Code clients use the palette

**Files:**
- Modify: `Nitrogen.LanguageService/Palette.cs` (contributions builder)
- Create: `Nitrogen.Cli/TextMate/PaletteCommand.cs`; modify `Nitrogen.Cli/NitrogenCli.cs`
- Modify: `editors/vscode/package.json` (`contributes.semanticTokenTypes`, `contributes.semanticTokenScopes`), `editors/vscode/src/extension.ts:51-53` (`paletteTokens: true`)
- Modify: `Nitrogen.Cli/VsCode/VsCodeRenderer.cs`: palette contributions; `contributes.grammars` and the `syntaxes/<name>.tmLanguage.json` file from Task 4; `paletteTokens: true` in the extension template at line 253
- Test: `Nitrogen.Tests/LanguageService/PaletteTests.cs`, `Nitrogen.Tests/Cli/VsCodeRendererTests.cs` (or the existing test class for the renderer)

**Interfaces:**
- Produces:
  - `Palette.VsCodeContributions() → System.Text.Json.Nodes.JsonObject` with:
    - `semanticTokenTypes`: `[{ id, superType: (Fallback ?? Operator) as legend name, description: scope }]`
    - `semanticTokenScopes`: `[{ scopes: { <name>: [<scope>] } }]` (no `language` key, so it applies to every language)
  - CLI `nitrogen generate palette --merge <package.json>` replaces those two keys under `contributes` in place and leaves everything else unchanged. Gravity uses this in Task 8.

- [ ] **Step 1: Write failing tests:**
  - `PaletteTests.Generic_extension_declares_the_palette`: parse `editors/vscode/package.json` (from the repository root, located the way other tests find repo files) and assert its two `contributes` keys equal `Palette.VsCodeContributions()`'s.
  - The renderer test: a generated extension has the same keys, a `grammars` entry `{ language: <name>, scopeName: "source.<name>", path: "./syntaxes/<name>.tmLanguage.json" }`, that file, and `paletteTokens: true` in its `extension.ts`.
  - `PaletteCommandTests.Merge_is_idempotent`: merging twice into a copy of a package.json gives identical bytes.
- [ ] **Step 2: Run** them. Expected: FAIL.
- [ ] **Step 3: Implement.** Update `editors/vscode/package.json` by running `nitrogen generate palette --merge editors/vscode/package.json`.
- [ ] **Step 4: Run** the focused tests, `dotnet test Nitrogen.Tests`, and `cd editors/vscode && npm run compile && npm test`.
- [ ] **Step 5: Manual check:** build and install the generic extension (docs/editor-support.md, "build from source"), open a `.ngr` file, and run "Developer: Inspect Editor Tokens and Scopes" on a bracket. It should show semantic token type `blockBracket` and its scope.
- [ ] **Step 6: Commit** `Map palette token types to TextMate scopes in VS Code extensions`.

### Task 6: Scope comparison and `.ngr` tuning

**Files:**
- Create: `Nitrogen.Cli/TokensCommand.cs` (`nitrogen tokens`); modify `Nitrogen.Cli/NitrogenCli.cs`
- Create: `editors/vscode/scripts/compare-scopes.mjs`, `eng/check-palette.sh`
- Create: `Nitrogen.LanguageService/Palette.Ngr.cs` (the `.ngr` entries, appended to `Entries`)
- Modify: `Nitrogen.Cli/NgrAssist.cs` (assist tokens carry `.ngr` palette names), `Nitrogen.Cli/LspCommand.cs:22-25` (`NgrStyles` palette names)
- Test: `Nitrogen.Tests/Cli/TokensCommandTests.cs`

**Interfaces:**
- Produces:
  - CLI `nitrogen tokens <file> [--config <nitrogen.json>]`. It prints a JSON array of `{ "line": int, "character": int, "length": int, "scope": string }`, where `scope` is the palette entry's scope, or `StandardScope(type)` for non-palette tokens.
  - `node editors/vscode/scripts/compare-scopes.mjs --grammar <tmLanguage.json> --scope-name <name> --source <file> --tokens <tokens.json>`. It exits 1 and lists `line:col text  textmate=<scope> server=<scope>` for each server token whose scope differs from TextMate's innermost scope at that range. Before comparing:
    - strip the language suffix from both;
    - skip `meta.*` scopes;
    - skip ranges inside `source.cs` embeddings.

    Ranges the server leaves uncoloured are not compared. "Differs" means neither scope is a prefix of the other.
  - `eng/check-palette.sh` runs `nitrogen tokens` and `compare-scopes` over `Nitrogen.Ngr/Nitrogen.ngr`, `examples/DateCalc/DateCalc.ngr`, and a sample generated-language file (`examples/DateCalc` source with its generated grammar).
  - `.ngr` entries: one per non-`meta` scope in ngr.tmLanguage.json, e.g. `ngrKeywordOtherClause` → `keyword.other.clause`, with the fallback taken from the first scope segment (`keyword`→Keyword, `storage`→Keyword, `entity.name.function`→Function, `entity.name.type`→Type, `variable.parameter`→Parameter, `variable`→Variable, `constant.numeric`→Number, `constant`→EnumMember, `string`→String, `comment`→Comment, `support.function`→Function, `support.type`→Type, `punctuation`→null). Write them as a table in `Palette.Ngr.cs`, not derived at runtime.

- [ ] **Step 1: Write failing test** `TokensCommandTests.Prints_scopes_for_a_ngr_file`: on a small `.ngr` text in a temp file, the output contains `syntax` with scope `storage.type.syntax` and a rule name with `entity.name.function.rule` (the hand-written grammar's scopes).
- [ ] **Step 2: Run** it. Expected: FAIL.
- [ ] **Step 3: Implement** `TokensCommand`, `compare-scopes.mjs` and `check-palette.sh`. Run `eng/check-palette.sh`; it fails on `.ngr` and lists the mismatches.
- [ ] **Step 4: Tune `.ngr`:** add `Palette.Ngr.cs` entries and set palette names in `NgrAssist`/`NgrStyles` until `eng/check-palette.sh` passes on all three files. Where the server is right and the hand-written grammar is wrong (a word the server resolves differently), fix the grammar only if its scope is a context TextMate can see. Otherwise leave it and add the range to an explicit allow-list in `check-palette.sh`, with a comment saying why.
- [ ] **Step 5: Run** `TokensCommandTests`, the full suite and `eng/check-palette.sh`. All pass.
- [ ] **Step 6: Commit** `Match .ngr semantic tokens to its TextMate scopes; add palette check`.

### Task 7: Rider maps palette types

**Files:**
- Modify: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenHighlighting.kt` (shared with generated plugins through `RiderPluginRenderer`'s resource copy, `Nitrogen.Cli/Rider/RiderPluginRenderer.cs:30`)
- Modify: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenCSharpStrings.kt:70` (`createInitializationOptions` adds `"paletteTokens" to true`), and the generated plugin's equivalent descriptor
- Test: `Nitrogen.Tests/Cli/RiderPluginRendererTests.cs` (or the existing renderer test class)

**Interfaces:**
- Consumes: palette names (Tasks 2 and 6).
- Produces: `NitrogenSemanticTokens.getTextAttributesKey(tokenType: String, modifiers: List<String>): TextAttributesKey?` mapping, using `DefaultLanguageHighlighterColors`:

  | Palette names | Rider key |
  |---|---|
  | `operationWord` | `FUNCTION_CALL` |
  | `declarationKeyword`, `sectionKeyword` | `KEYWORD` |
  | `modifierWord` | `METADATA` |
  | `constantWord` | `CONSTANT` |
  | `blockBracket` | `BRACES` |
  | `parenthesis` | `PARENTHESES` |
  | `squareBracket` | `BRACKETS` |
  | `terminator` | `SEMICOLON` |
  | `separator` | `COMMA` |
  | `operator` | `OPERATION_SIGN` |
  | `ngr*` | by the same first-segment rule as their fallback |
  | anything else | `super` |

- [ ] **Step 1: Write failing test:** the rendered plugin's `NitrogenHighlighting.kt` contains `getTextAttributesKey` and every general palette name, and its initialization options include `paletteTokens`.
- [ ] **Step 2: Run** it. Expected: FAIL.
- [ ] **Step 3: Implement** the Kotlin changes.
- [ ] **Step 4: Run** the test, then the Rider plugin build described in docs/editor-support.md. Both succeed.
- [ ] **Step 5: Commit** `Map palette token types to Rider colours`.

### Task 8: Gravity MotionDSL (in `~/work/Gravity`, new branch)

**Files (Gravity repo):**
- Create: `Nitrogen.MotionDsl.Server/textmate/nitrogen.json`. It declares languages `motion`, `policy` and `route` with their grammars (`../../Nitrogen.MotionDsl/*.ngr`), start rules, and `tokens` styles equal to `MotionLanguageServer`'s `MotionStyles`/`PolicyStyles`/`RouteStyles`. It is not at the repo root, so the generic Nitrogen extension does not pick it up.
- Modify: `Nitrogen.MotionDsl.Server/build-vscode.sh`. Add a step before packaging that, for each language:
  - runs `dotnet run --project "$NitrogenRoot/Nitrogen.Cli" -c Release -- generate textmate --config textmate/nitrogen.json --language <id> --output vscode/syntaxes/<id>.tmLanguage.json`;
  - then runs `generate palette --merge vscode/package.json`.

  `NitrogenRoot` defaults as in `Directory.Build.props`.
- Modify: `Nitrogen.MotionDsl.Server/vscode/package.json` (three `contributes.grammars` entries, `source.<id>`), `vscode/src/extension.ts` (`initializationOptions: { paletteTokens: true }`)
- Create: `Nitrogen.MotionDsl.Tests/PaletteConfigTests.cs` and the fixture `Nitrogen.MotionDsl.Tests/Fixtures/getup-r11.policy` (copied from `~/work/GravityAssets/Policies/getup-r11.policy`)
- Create: `Nitrogen.MotionDsl.Server/check-palette.sh`. It runs Nitrogen's `nitrogen tokens --config textmate/nitrogen.json` and `compare-scopes.mjs` on the fixture against `vscode/syntaxes/policy.tmLanguage.json`.

- [ ] **Step 1: Write failing test** `PaletteConfigTests.Textmate_config_styles_match_the_server`: each `tokens` entry in `textmate/nitrogen.json` equals `MotionLanguageServer.Registry()`'s presentation style for that language and kind, compared by token type name.
- [ ] **Step 2: Run** `dotnet test Nitrogen.MotionDsl.Tests --filter PaletteConfigTests`. Expected: FAIL (no config).
- [ ] **Step 3: Implement** the config, script, package.json and extension changes.
- [ ] **Step 4: Run** the test (PASS), `build-vscode.sh` (produces a `.vsix` with three grammars), and `check-palette.sh` (PASS; any allow-listed range carries a comment).
- [ ] **Step 5: Manual check:** install the `.vsix`, open `GravityAssets/Policies/getup-r11.policy` with a Nitrogen extension built from Task 5 installed, reload, and watch the file open. It should show no colour change when the server's tokens arrive. This also confirms the TovarishN/Nitrogen#85 interplay: if #85 is not merged and installed, the generic extension's empty tokens still hide the MotionDSL ones, so check with #85's build.
- [ ] **Step 6: Commit** in Gravity: `MotionDSL: TextMate grammars and palette tokens`.

### Task 9: Documentation

**Files:**
- Modify: `docs/editor-support.md` (palette, `paletteTokens`, `generate textmate`, `generate palette`, `tokens`, `eng/check-palette.sh`), `docs/github-highlighting.md` (the `.ngr` grammar is now also the server's colour reference), `.agents/skills/nitrogen/references/repository-development.md` (the new commands under repository checks). The skill and README files have uncommitted user edits: only touch `repository-development.md` and stage only that file.

- [ ] **Step 1: Write the doc changes.**
- [ ] **Step 2: Verify** every command in the docs by running it once.
- [ ] **Step 3: Commit** `Document the TextMate palette`.
