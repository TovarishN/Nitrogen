# README restructure: a pitch, with the guides in `docs/`

Status: implemented (2026-10-08).

## Problem

`README.md` is 452 lines and serves four audiences at once:

- the pitch: the agentic direction and the DateCalc example;
- setup: build, packages, CLI, editors;
- a reference for the grammar language: *Declaration scopes* through *Templates*, and the editor
  support for lowered languages;
- editor internals: Rider's clients, `skipLanguages`, and so on.

Three sections appear twice (*Use the CLI*, *VS Code and generated language support*, *Rider and
generated plugin support*), and the roadmap link appears twice. The editor features added in 0.6.0 and
0.7.0 are barely mentioned:
- values as hints, in C# strings and on hover;
- `today`;
- quick fixes.

The repository has no images.

## Goal

The README shows three things:
1. how easy it is to build a DSL with IDE support (coloring, completion, values, quick fixes), with
   room for screenshots and videos;
2. the agentic direction;
3. the semantic direction.

The grammar-language tutorial and the editor setup move to `docs/`. Nothing is dropped: every section
removed from the README has a home in the new files, and the duplicates are merged.

Out of scope: capturing the screenshots and videos (the user does that from `docs/media.md`), and
changing what any feature does.

## 1. `README.md`

In order:

1. **Intro.** What Nitrogen is (a .NET language workbench: `.ngr` grammars → parsers, binding,
   semantics, typed HIR, a language server and editor plugins), and its Nitra heritage.
2. **Build a language with an IDE.** The DateCalc showcase:
   - **The language:** a short excerpt of `DateCalc.ngr` (a token, a rule with `lowers`, a `check`), and
     one sentence each on the C# semantic module, the evaluator, the evaluation profile and the fixes,
     with file links and their sizes.
   - **What the editor gives you.** A fenced text demo of `sample.datecalc` with its line-end values,
     then short demos of:
     - hover on `3 * sprint` (`= 42 days`);
     - the quick fix on `2026-02-30` ("Change to 2026-02-28") and on `2026-12-25 + 2026-10-05`
       ("Use '-'");
     - `countdown.datecalc` using `today`;
     - values inside `Snippets.cs`'s tagged strings.

     The editor features are listed once: coloring, completion, diagnostics, go to definition,
     references, rename, hover with values, line-end values, quick fixes, and the same inside tagged C#
     strings. A link to the feature matrix in `docs/editor-support.md` follows the list.
   - **Media slots:** an HTML comment marks each place an image or video goes, naming its file under
     `docs/images/`: `datecalc-hints.png`, `datecalc-completion.png`, `datecalc-hover.png`,
     `datecalc-quickfix.gif` and `csharp-strings.png`. Comments render as nothing, so the README never
     shows a broken image. Adding a capture replaces a comment with an image line.
   - **Try it:** open `examples/DateCalc` as a VS Code workspace with the Nitrogen extension, or run
     `nitrogen package --config examples/DateCalc/nitrogen.json --output dist` for installable plugins.
3. **Agentic direction.** The current *Why Nitrogen for agentic work*, tightened:
   - the checked path from a task to a host operation;
   - the Geometry `GE0001` example;
   - one paragraph on the agent skill, with a link.

   The catalog clone and skill-install commands move to `docs/agent-skill.md`.
4. **Semantic direction.** Qualified semantic types and operation signatures, semantic modules and
   their composition, typed HIR with source origins, projection through exact host handlers, and the
   editor-facing edges (`EvaluationProfile`, `DiagnosticFixes`). Then the semantic catalog
   (`Nitrogen.Concepts`): concepts with evidence of reuse and failure, consulted before inventing new
   ones. Links go to the roadmap and the agent skill.
5. **Get started.**
   - Requirements; build and test; `./build.sh`.
   - Packages, showing `0.7.0` and keeping the feed and token note.
   - The CLI: `parse`, `watch`, `lsp`, `package`, `generate`.

   Each is a few lines, with links to the guides.
6. **Documentation.** Links to the language guide, the editor guide, the agent skill, the media
   checklist, the roadmap and the issues.
7. **Project map**, with `Nitrogen.Cli`'s role updated to "`parse`, `watch`, `lsp`, `package` and
   `generate` commands". Then **License**.

## 2. `docs/language-guide.md`

The grammar-language tutorial, moved from the README with headings demoted one level under a short
intro:
- *Declaration scopes*;
- *Inferred sequence arguments*, including `optional text`;
- *Derived declarations*;
- *Typed repetition*, with *Selected declarative roots* and *Deferred projection arguments*;
- *Templates*;
- *Editor support for lowered languages*, including the `ILanguageAssist` paragraph.

Wording changes are limited to links (now relative to `docs/`) and the move itself.

## 3. `docs/editor-support.md`

Setup and internals, de-duplicated and brought up to date:
- **Feature matrix:** a table of features (coloring, completion, diagnostics, go to definition and
  references, rename, hover, hover values, line-end values, quick fixes) against three places:
  language files, C# strings in VS Code, and C# strings in Rider. Quick fixes in Rider's C# strings are
  marked unsupported, with the reason.
- **VS Code extension:** the setup steps, once. They include the workspace-indexing paragraph that only
  the second copy had.
- **Rider plugins:** the generic plugin, generated plugins, and server bundles, once.
- **Installable plugins for a language:** `nitrogen package`.
- **Helper sources:** semantic modules (the current paragraph), `EvaluationProfile` (values and
  `today`'s midnight refresh), and `DiagnosticFixes` (checked before they're offered, and the
  one-document limitation).
- **Languages inside C# strings:** the current section, with values and quick fixes added to the list
  of what works inside strings, and the VS Code-only note for quick fixes.

## 4. `docs/agent-skill.md`

The agent-skill setup moved from the README: the catalog clone commands, installing the skill for
Codex, keeping it updated, and working without the private catalog.

## 5. `docs/media.md` and `docs/images/`

`docs/media.md` lists each capture. For each one it gives:
- the file name;
- the editor (VS Code, or both);
- the file to open and the cursor or menu state;
- what must be visible;
- the size (about 1200 px wide, dark theme) and format (PNG; a GIF or MP4 for the quick fix).

It ends with the one-line README change that shows the capture. `docs/images/` holds a `.gitkeep` until
the first capture.

## 6. Elsewhere

- **Generated VS Code README** (`VsCodeRenderer`): its evaluation sentence also names hover values,
  and a sentence is added for quick fixes, when a helper source exports `DiagnosticFixes`. Its
  generation test asserts both.
- **`editors/vscode/README.md` and `docs/roadmap.md`:** links into README sections that moved are
  pointed at the new files.

## 7. Checks

- **Every old section has a home:** each heading of the old README maps to a section of the new README
  or a doc. The plan lists the mapping, and the final task checks it.
- **No broken relative links:** a script resolves every relative link in the changed Markdown files.
- **Version strings:** none outside the package examples changes.
- **Build and tests:** `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests`, for
  the generated-README test.
