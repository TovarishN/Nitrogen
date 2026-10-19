# Highlighting `.ngr` on GitHub

GitHub highlights files and fenced code blocks through [Linguist](https://github.com/github-linguist/linguist).
Linguist only knows languages listed in its `languages.yml`, each tied to a TextMate grammar. Nitrogen
ships that grammar at [editors/vscode/syntaxes/ngr.tmLanguage.json](../editors/vscode/syntaxes/ngr.tmLanguage.json)
(scope `source.ngr`). The VS Code extension uses the same file, so `.ngr` files are coloured before
`nitrogen lsp` starts; semantic tokens from the server refine it afterwards.

Until Linguist accepts the language, GitHub shows `.ngr` files and ` ```ngr ` blocks as plain text.
Keep tagging fences ` ```ngr `: they start highlighting as soon as Linguist knows the alias, with no
edits to existing docs.

## What the grammar covers

- `language` declarations and `syntax module` bodies, and bare members, so doc fragments without a
  module wrapper still highlight.
- `token`, `syntax`, `extensible syntax` and `extend syntax` rules: labels, strings, chars, character
  classes, PEG operators, alternative names, `precedence … left|right`.
- Binding and lowering clauses: `declares`, `references?`, `scope`, `lowers …`, `rewrites`.
- Grammar functions and quote helpers: parameters, `let`, `match` arms, `<[ … ]>` templates with
  `$Name` splices, `choice`/`seq` comprehensions.
- Semantics blocks: `out`/`in` properties, `check CODE … : message at Child;`, and assignments, with
  a C# subset (strings, interpolation, keywords, calls) that never runs past the `;` or `}` ending it.

A syntax rule whose semantics block ends it without `;` closes at the next line that starts a member.

## Getting it into Linguist

Linguist adds a language only once GitHub has indexed at least 2000 files with its extension in the
last year, spread across many unrelated `:user/:repo` combinations (see its
[CONTRIBUTING.md](https://github.com/github-linguist/linguist/blob/main/CONTRIBUTING.md#adding-a-language)).
A code search for `path:*.ngr "syntax module"` shows the count. When it is met:

1. Fork `github-linguist/linguist` and, with Docker running, run `script/add-grammar https://github.com/TovarishN/Nitrogen`.
   Linguist vendors this repository as a submodule and reads the grammar from the path above.
2. Add the entry to `lib/linguist/languages.yml` (alphabetical), then run `script/update-ids` to fill
   `language_id`:

   ```yaml
   Nitrogen Grammar:
     type: programming
     color: "#3b6fd4"
     extensions:
     - ".ngr"
     aliases:
     - ngr
     tm_scope: source.ngr
     ace_mode: text
   ```

3. Add real-world samples under `samples/Nitrogen Grammar/`, for example
   [Nitrogen.Ngr/Nitrogen.ngr](../Nitrogen.Ngr/Nitrogen.ngr) and
   [examples/DateCalc/DateCalc.ngr](../examples/DateCalc/DateCalc.ngr). Linguist wants real-world code,
   not tutorial examples, and the pull request must name each sample's licence (MIT here) and source.
4. Run `bundle exec rake test` and open the pull request, linking the usage search.

`.ngr` is not claimed by another Linguist language today; if that changes, a heuristic on
`syntax module` or `token … =` in `heuristics.yml` disambiguates.
