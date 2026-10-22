# TextMate palette for Nitrogen semantic tokens

## Intent

In VS Code, a `.ngr` file is first painted by its TextMate grammar and then repainted when
`nitrogen lsp` answers with semantic tokens. The two disagree, so colours visibly change. The
TextMate colours are better. The theme gives TextMate scopes a richer palette (storage keywords,
control keywords, PEG operators, rule names, labels…), while the server sorts constructs into a few
standard LSP token types (every word literal is `keyword`, every rule name `type`). As a result,
constructs that TextMate tells apart get the same colour from the server.

Goal: **the language server is the single source of truth for colour, and it speaks TextMate's richer
palette.** The editor shows the server's classification through the same TextMate scopes a grammar
would use, so the theme's full palette appears. Every Nitrogen language also gets a TextMate grammar
that paints the first frame the way the server will, so the switch to semantic tokens is invisible in
the common case.

Owner's words: "the lsps colors must be the single source of truth - i just like the colors text mate
uses its reacher palette"; "no flash rule for everything".

## Decisions made with the owner

- **Server wins.** Where the server and a TextMate grammar disagree (a contextual keyword used as a
  name, say), the server's colour stands. A rare visible change there is accepted.
- **The server may fill in.** Names TextMate cannot classify from text alone (references resolved
  through binding) get the server's colour when it arrives.
- **More precise criteria win; grammar position is the fallback.** Owner: "when there is more
  precise coloring criteria - it should win - like in this case lowering should win, when there is
  no other criteria - nitrogen should use grammar position". Precedence, highest first:
  1. language assist tokens (`LanguageAssist`);
  2. declared and resolved names (binding + `Presentation` styles);
  3. lowering: an operation's own words are `function`, and type styles and scalar constants colour
     value tokens (today's `Lowered()` in `SemanticTokenBuilder`). In `getup-r11.policy`, `policy`
     stays a function because its rule `lowers Policy.Document(...)`;
  4. grammar-shape categories (the table below);
  5. token-rule lexical classes and comments.
- **Categories are inferred from grammar shape.** Grammar authors write no colour annotations for
  keywords and punctuation. The existing `Presentation` styles (symbol kind → token type) stay the
  author's way to colour declared and referenced names.
- **Scope is everything:** `.ngr`, every language served by an extension that `nitrogen package` /
  `nitrogen vscode` generates, and Gravity's MotionDSL languages (`.motion`/`.skill`,
  `.policy`/`.compose`, `.route`), which are served by `nitrogen-motion lsp` (in the Gravity repository,
  built on `Nitrogen.LanguageService`).

## Goals

1. A fixed, language-independent vocabulary of fine **colour categories** that `SemanticTokenBuilder`
   emits instead of the coarse lexical classification.
2. Every Nitrogen VS Code client maps each category to the TextMate scope the theme colours, so the
   server's tokens show the TextMate palette under any theme.
3. A **TextMate grammar generated from each Nitrogen grammar**, built on the same inference as the
   server, shipped by generated extensions and available to MotionDSL's build.
4. `.ngr`: the server's tokens agree with the hand-written
   [ngr.tmLanguage.json](../../../editors/vscode/syntaxes/ngr.tmLanguage.json) for every construct
   that grammar paints.

## Non-goals

- Changing any theme or shipping colour customizations. Colours come from the user's theme via
  scopes.
- Replacing the hand-written `.ngr` grammar. It stays, because GitHub/Linguist will use it (see
  [github-highlighting.md](../../github-highlighting.md)).
- Contextual TextMate precision beyond what the inference table below gives. The server corrects
  the rest.
- First-frame TextMate grammars for languages served only through the generic extension's
  `nitrogen.json` workspace support: VS Code grammars are static `package.json` contributions. Those
  languages still get the richer palette from the server (Goal 2), just no first frame.

## Anti-goals (failure even if it works)

- The server and the generated TextMate grammar drift apart, so the flash comes back for some
  construct. They must share one inference, not two hand-kept copies.
- Clients that don't know the new categories (Neovim, Helix, older extensions) lose colour.
  Categories must degrade to today's standard token types.
- Grammar authors have to annotate keywords to get decent colour.

## The categories (inference from grammar shape)

Classification looks at a literal's place in the rule that matched it.

| Grammar shape | Category | TextMate scope |
|---|---|---|
| Word literal that opens a rule with a `declares` clause (`goal`, `syntax`, `token`) | declaration keyword | `storage.type` |
| Word literal that opens any other rule (`reward`, `train`, `start`) | section keyword | `keyword.other` |
| Word literal after an earlier child of its rule (`sharpness`, `every`, `hold`, `then`, `at`) | modifier word | `keyword.operator.word` |
| Word literal that is one of several bare alternatives with nothing after it (`rms`, `xz`, `uniform`) | constant word | `constant.language` |
| `{` `}` `(` `)` `[` `]` | bracket | `punctuation.section` (`.block`/`.parens`/`.brackets`) |
| `;` | terminator | `punctuation.terminator` |
| `,` | separator | `punctuation.separator` |
| Other symbol literals (`>`, `..`, `=`, `->`) | operator | `keyword.operator` |
| Declared names | per the language's `Presentation` style, `declaration` modifier | `entity.name.*` by token type |
| Resolved references | the declared symbol's style | as today's standard mapping |
| Number-, string-, char-like token rules; `//` and `/* */` comments | as today | `constant.numeric`, `string.quoted`, `comment` |

"Opens a rule" means the literal is the rule's first element, after any optional leading elements
the builder judges equivalent. Where a word fits several rows across different rules, each
occurrence is classified by the rule that matched it. The TextMate grammar has to pick one pattern
per word or per word-in-context, and the server corrects it.

Each fine category degrades to a standard type for unaware clients: declaration/section/modifier
words → `keyword`, constant word → `enumMember`, brackets/terminators/separators → no token (they
were operators before; dropping them lets TextMate or the editor default show through), operator
→ `operator`.

## Constraints

- **The TextMate grammar mirrors the precedence statically.** The generator reads `lowers` clauses
  from grammar source, so a rule's operation words get the function scope (`entity.name.function`)
  in the TextMate grammar too, matching level 3 above. Value colours that depend on lowered HIR at
  runtime (type styles on computed values) are approximated or left plain, and the server fills
  them in.
- **One inference, two consumers.** The server never sees grammar rules at runtime: rules compile
  to function pointers (`Nitrogen.Runtime/Rule.cs`). The classification must therefore be computed
  from grammar source once, wherever grammars compile (`Nitrogen.Grammar` for both the source
  generator and runtime-compiled workspace/bundled grammars). It must reach the server as generated
  metadata on `SyntaxModule`, the way `GetBinding`, `GetSemantics` and `DiagnosticPatterns` already
  do, and reach the TextMate generator from the same computation.
- **Wire format: opt-in palette token types** *(revised while planning: `.ngr` alone needs about
  90 distinct scopes, and LSP token modifiers are a 32-bit set with 2 bits already used, so a
  modifier per category cannot work)*. A client that sends `initializationOptions.paletteTokens:
  true` gets a legend of the standard types followed by one custom token type per palette category,
  and tokens use those types. VS Code maps each type to its scope through
  `contributes.semanticTokenTypes` + `contributes.semanticTokenScopes`. Any other client gets today's
  standard-type legend, with each category reduced to its fallback standard type. Unaware clients
  therefore look exactly as they do today, which meets the degrade anti-goal.
- **Scopes in every VS Code client.** The vocabulary is language-independent, so the generic
  extension (`editors/vscode/package.json`) declares the mapping once for all files, and every
  generated extension (`Nitrogen.Cli/VsCode/VsCodeRenderer.cs`) writes the same block. MotionDSL's
  `vscode/package.json` in the Gravity repo needs it too. Provide it from Nitrogen, as a command
  output or a documented snippet the Gravity build copies, so it can't drift.
- **TextMate generation.** A `nitrogen` CLI entry produces `<language>.tmLanguage.json` from a
  language's grammars. `nitrogen vscode`/`package` uses it and adds `contributes.grammars`;
  MotionDSL's `build-vscode.sh` calls it. Scope names end in the language id (`storage.type.policy`)
  so themes still match on the prefix.
- **`.ngr` is special.** It keeps its hand-written grammar. The server's `.ngr` output matches that
  grammar's **exact scopes**, ignoring the `.ngr` suffix (about 60, e.g. `entity.name.function.rule`,
  `storage.modifier.extensible`, `keyword.operator.peg`, `variable.other.splice`), so colours
  agree under any theme, not just Light+ *(builder-check call by Claude: "no flash for everything"
  rules out colour-only matching)*. This needs `.ngr`-specific modifiers beyond the table above.
  Keep them in the `.ngr` language's own presentation/assist, not in the general inference. Inside
  embedded C# regions the server keeps today's behaviour. This is the largest single step.
- **Rider.** `NitrogenHighlighting.kt` (generic and generated Rider plugins) must map the new
  modifiers to distinct `TextAttributesKey`s through `LspSemanticTokensSupport`, or Rider shows them
  as plain keywords. A first-frame TextMate bundle for Rider is not required here.
- **Gravity is in scope** *(builder-check call by Claude, from "everything")*. The plan includes
  Gravity's `Nitrogen.MotionDsl.Server/build-vscode.sh` (build the Nitrogen CLI from the sibling
  `NitrogenRoot` checkout and run the TextMate generator), its `vscode/package.json`
  (`contributes.grammars`, `semanticTokenModifiers`, `semanticTokenScopes`), and the
  `getup-r11.policy` comparison in Gravity's extension tests. Gravity changes land as a separate
  branch/PR in that repository.
- The semantic-tokens-null fix (TovarishN/Nitrogen#85) is a separate change. This work must not
  depend on it.

## Verification the owner cares about

- For `getup-r11.policy` and a representative `.ngr` file under Light+: every range the TextMate
  grammar paints has the same scope after the server's tokens arrive, except where the server
  corrects a misclassified word. A test can compare the TextMate tokenization (vscode-textmate in
  the extension tests) against the server's tokens mapped through `semanticTokenScopes`.
- Server tests: the categories for each table row, on small grammars.
- An unaware client still sees standard token types.

## Left to the builder

- The precise "opens a rule" / "after a child" / "bare alternative" definitions, and how they treat
  optional prefixes, extensible-syntax extensions and composed modules.
- The metadata shape on `SyntaxModule`, and how it identifies a literal occurrence in the tree
  (parent kind + slot is one option).
- How far the generated TextMate grammar goes beyond word lists (begin/end blocks for strings and
  comments are required; contextual declaration-name patterns like "word after `goal`" →
  `entity.name.*` are desirable where the inference makes them unambiguous).
- Palette token type names and the exact `semanticTokenScopes` block.
- Order of work. Suggested: categories + VS Code scopes (visible gain everywhere), then the
  TextMate generator, then `.ngr` tuning, then Rider.
