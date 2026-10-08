# README restructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `README.md` becomes a pitch: building a DSL with IDE support (with slots for screenshots), the agentic direction and the semantic direction. The grammar-language tutorial, editor setup and agent-skill setup move to `docs/`, de-duplicated and current for 0.7.0.

**Architecture:**
- Moved sections are extracted by heading from the README as it is at the start of this plan (`git show HEAD:README.md`, commit `119d547`), so no text is retyped.
- New prose is written out below.
- A heading-mapping check and a link checker confirm nothing was lost and every relative link resolves.

**Tech Stack:** Markdown, Python 3 (extraction and checks), C# / xUnit (generated-README test).

**Spec:** `docs/superpowers/specs/2026-10-08-readme-restructure-design.md`

**Conventions:**
- The original README is `git show 119d547:README.md`; the scripts below read it from there, so the extractions stay valid while `README.md` changes.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Work on branch `readme-restructure`.

**Facts gathered while planning:**
- **Inbound links:** the only link into the README is `editors/vscode/README.md` line 22 ("see the repository README"). `docs/roadmap.md` has none.
- **File sizes:** `DateCalc.ngr` 84 lines, `DateCalcLanguage.cs` 81, `MathModule.cs` 58, `DateCalcEvaluator.cs` 112, `DateCalcFixes.cs` 54.
- **Real values:** the values below were observed from the real server earlier in this work. The sample hints are in `ValueHintTests`. `3 * sprint` hovers as `` `DateCalc.Duration` · `DateCalc.Times` · `DateCalc.Weekday` `` then `= 42 days`. The `Snippets.cs` values are `= 2026-12-25 Fri`, `= 81`, `= Friday` and `= 2026-11-16 Mon`.

## File structure

| File | Responsibility | Task |
| --- | --- | --- |
| `docs/language-guide.md` | grammar-language tutorial (moved) | 1 |
| `docs/agent-skill.md` | agent-skill setup (moved) | 2 |
| `docs/editor-support.md` | feature matrix; editor setup and internals (moved, de-duplicated, updated) | 3 |
| `docs/media.md`, `docs/images/.gitkeep` | capture checklist | 4 |
| `README.md` | the pitch | 5 |
| `Nitrogen.Cli/VsCode/VsCodeRenderer.cs`, `Nitrogen.Tests/Cli/VsCodeGenerationTests.cs` | generated README names hover values and quick fixes | 6 |
| `editors/vscode/README.md` | link to the new editor guide | 6 |
| `docs/superpowers/specs/…` | status | 7 |

---

### Task 1: `docs/language-guide.md`

**Files:**
- Create: `docs/language-guide.md`

- [ ] **Step 1: Generate it from the original README.** Run from the repo root:

```bash
python3 - <<'EOF'
import re, subprocess
old = subprocess.run(["git", "show", "119d547:README.md"], capture_output=True, text=True, check=True).stdout.split("\n")

def section(title, occurrence=1):
    """Lines from the `occurrence`-th heading named `title` up to the next heading of the same or a higher level."""
    seen = 0
    for i, line in enumerate(old):
        m = re.match(r"^(#+) (.*)$", line)
        if m and m.group(2) == title:
            seen += 1
            if seen == occurrence:
                level = len(m.group(1))
                j = i + 1
                while j < len(old):
                    n = re.match(r"^(#+) ", old[j])
                    if n and len(n.group(1)) <= level: break
                    j += 1
                return old[i:j]
    raise SystemExit(f"no section {title!r} #{occurrence}")

parts = ["# The Nitrogen grammar language",
         "",
         "How `.ngr` grammars declare scopes, pass typed arguments to operations, repeat and expand typed templates, and how the editor uses what a document lowers to. For an overview of Nitrogen, see the [README](../README.md); for editor setup, [editor support](editor-support.md).",
         ""]
for title in ["Declaration scopes", "Inferred sequence arguments", "Derived declarations", "Typed repetition", "Templates", "Editor support for lowered languages"]:
    parts += section(title)
text = "\n".join(parts)
# The roadmap line inside "Typed repetition" duplicates the README's documentation index.
text = text.replace("The [roadmap](docs/roadmap.md) and [milestone issue records](issues/) document Nitrogen's development.\n\n", "")
text = text.replace("](docs/superpowers/", "](superpowers/")
open("docs/language-guide.md", "w").write(text.rstrip("\n") + "\n")
EOF
grep -n '^#' docs/language-guide.md
```

Expected headings: `# The Nitrogen grammar language`, then `## Declaration scopes`, `## Inferred sequence arguments`, `## Derived declarations`, `## Typed repetition`, `### Selected declarative roots`, `### Deferred projection arguments`, `## Templates`, `## Editor support for lowered languages`.

The headings keep their `##`/`###` levels: under the new `#` title they sit exactly one level below it, which is the demotion the spec asks for.

- [ ] **Step 2: Check no roadmap line or README-relative link remains.**

Run: `grep -n 'docs/roadmap\|](docs/\|](examples/\|](Nitrogen\.' docs/language-guide.md`
Expected: no output. If a link remains, prefix it with `../`, or drop the `docs/` part for files under `docs/`.

- [ ] **Step 3: Commit**

```bash
git add docs/language-guide.md
git commit -m "Move the grammar-language guide out of the README

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `docs/agent-skill.md`

**Files:**
- Create: `docs/agent-skill.md`

- [ ] **Step 1: Generate it.** Run from the repo root:

```bash
python3 - <<'EOF'
import subprocess
old = subprocess.run(["git", "show", "119d547:README.md"], capture_output=True, text=True, check=True).stdout.split("\n")
start = old.index("### Agent skill and semantic catalog")
end = old.index("## Example: a calculator with dates")
body = old[start + 1:end]
text = "\n".join(["# The Nitrogen agent skill and semantic catalog", ""] + body).rstrip("\n") + "\n"
text = text.replace("](.agents/", "](../.agents/")
open("docs/agent-skill.md", "w").write(text)
EOF
head -5 docs/agent-skill.md; grep -c '```' docs/agent-skill.md
```

Expected: the title, then "The [Nitrogen agent skill](../.agents/skills/nitrogen/SKILL.md) applies this approach…". There are 4 fence lines (two shell blocks).

- [ ] **Step 2: Replace the first sentence's "this approach".** On its own page there's no preceding text, so in `docs/agent-skill.md` replace "applies this approach across projects" with "applies Nitrogen's approach (typed capabilities, checked before they run) across projects".

- [ ] **Step 3: Commit**

```bash
git add docs/agent-skill.md
git commit -m "Move the agent-skill setup out of the README

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `docs/editor-support.md`

**Files:**
- Create: `docs/editor-support.md`

- [ ] **Step 1: Generate the moved sections.** Run from the repo root. The second copy of *VS Code and generated language support* is the one with the workspace-indexing paragraph:

```bash
python3 - <<'EOF'
import re, subprocess
old = subprocess.run(["git", "show", "119d547:README.md"], capture_output=True, text=True, check=True).stdout.split("\n")

def section(title, occurrence=1):
    seen = 0
    for i, line in enumerate(old):
        m = re.match(r"^(#+) (.*)$", line)
        if m and m.group(2) == title:
            seen += 1
            if seen == occurrence:
                level = len(m.group(1)); j = i + 1
                while j < len(old):
                    n = re.match(r"^(#+) ", old[j])
                    if n and len(n.group(1)) <= level: break
                    j += 1
                return old[i:j]
    raise SystemExit(f"no section {title!r} #{occurrence}")

vscode = section("VS Code and generated language support", 2)
vscode[0] = "## VS Code extension"
rider = section("Rider and generated plugin support", 2)
rider[0] = "## Rider plugins"
installable = section("Installable plugins for a language")
# Split "Installable plugins" at its second paragraph, which is about helper sources.
k = next(i for i, l in enumerate(installable) if l.startswith("A language's helper sources can also carry its semantics."))
helpers = ["## Helper sources", ""] + installable[k:]
installable = installable[:k]
strings = section("Languages inside C# strings")
open("/tmp/nitrogen-editor-parts.txt", "w").write("\n".join(
    ["@@VSCODE"] + vscode + ["@@RIDER"] + rider + ["@@INSTALLABLE"] + installable + ["@@HELPERS"] + helpers + ["@@STRINGS"] + strings))
EOF
grep -n '^@@\|^## ' /tmp/nitrogen-editor-parts.txt
```

Expected: the five `@@` markers, each followed by its `##` heading.

- [ ] **Step 2: Assemble the file.** Write `docs/editor-support.md` with the content below. Where a line reads `@@INSERT NAME`, paste that part from `/tmp/nitrogen-editor-parts.txt` (the lines after `@@NAME`, up to the next `@@`):

````markdown
# Editor support

Nitrogen's language server (`nitrogen lsp`) serves `.ngr` grammars and every language a `nitrogen.json`
declares, in VS Code, Rider, and any LSP client. What a language gets depends on what it supplies: a
grammar gives coloring, outline and navigation; `check` rules give diagnostics; semantic modules give
typed hover and completion; an evaluation profile gives values; fix providers give quick fixes. For an
overview, see the [README](../README.md); for the grammar language, the [language guide](language-guide.md).

## Feature matrix

| Feature | Language files | C# strings in VS Code | C# strings in Rider |
| --- | --- | --- | --- |
| Coloring | ✓ | ✓ (painted as decorations) | ✓ (added to Rider's) |
| Completion | ✓ | ✓ | ✓ |
| Diagnostics | ✓ | ✓ | ✓ |
| Go to definition, references | ✓ | ✓ | ✓ (find usages) |
| Rename | ✓ | ✓ | — (left to Rider) |
| Outline | ✓ | — (left to C#) | — (left to Rider) |
| Hover, with the expression's value¹ | ✓ | ✓ | ✓ |
| Each statement's value at its end¹ | ✓ | ✓ | ✓ |
| Quick fixes² | ✓ | ✓ | — (Rider's C# client keeps code actions off, so its Alt+Enter menu holds only Rider's own) |

¹ When the language's helper sources export an `EvaluationProfile` (see [Helper sources](#helper-sources)).
² When they export `DiagnosticFixes`.

@@INSERT VSCODE

@@INSERT RIDER

@@INSERT INSTALLABLE

@@INSERT HELPERS

@@INSERT STRINGS
````

- [ ] **Step 3: Fix relative links for the file's new place.** In `docs/editor-support.md`:
  - `](editors/` → `](../editors/`
  - `](Nitrogen.Geometry/` → `](../Nitrogen.Geometry/`
  - `](.github/` → `](../.github/`

  Run `grep -n '](' docs/editor-support.md` and check that every relative link now starts with `../`, a file under `docs/`, or `#`.

- [ ] **Step 4: Add the evaluation and fix paragraphs to *Helper sources*.** Append after its last code block (the `nitrogen package --config Nitrogen.Geometry/…` one):

```markdown
A public static `EvaluationProfile` among the helper sources makes the editor show values. It names the
syntax kinds that are statements, builds host handlers from the language's catalog, supplies builtin
values, and formats a value as text. Each statement then shows its value at its end, as an inlay hint,
and hovering an expression adds its value to the hover. Nothing is shown at line ends while the document
has an error; hover shows a value for any expression that lowered. A profile built with
`readsClock: true` receives the host's time (`EvaluationContext.Now`), as DateCalc's `today` does. Its
values are recomputed on a new day, and the server asks the editor to refresh them at local midnight. A
language has at most one profile: two are warning `NGR0003`, and one whose handlers don't match the
catalog is `NGR0002`. [DateCalcEvaluator.cs](../examples/DateCalc/DateCalcEvaluator.cs) is an example.

A public static `DiagnosticFixes` maps diagnostic codes to C# fixers that propose edits. The server
offers a fix only when the edited document, parsed and checked on its own, no longer has that error and
has fewer errors overall. Because the check sees one document, a fix that relies on another file of the
language can be wrongly rejected. Fixes are served as LSP `quickfix` code actions. A language has at
most one fix provider; two are warning `NGR0004`.
[DateCalcFixes.cs](../examples/DateCalc/DateCalcFixes.cs) fixes invalid dates and `date + date`.
```

- [ ] **Step 5: Update *Languages inside C# strings*.**
  - In its paragraph beginning "A tag is a", change "Coloring, diagnostics, completion, hover, go to definition, references and rename work inside the string" to "Coloring, diagnostics, completion, hover, go to definition, references, rename, statement values and quick fixes work inside the string".
  - In the Rider paragraph, change "and diagnostics, completion, hover, go to definition and find usages work in the strings; rename, structure view, formatting and the rest stay with Rider." to "and diagnostics, completion, hover, go to definition, find usages and statement values work in the strings; rename, structure view, formatting, quick fixes and the rest stay with Rider."
  - Change "(`skipLanguages`, see below)" to "(`skipLanguages`, described below)".

- [ ] **Step 6: Update the VS Code feature sentence.** In *VS Code extension*, replace the sentence beginning "Available editor features include" through "the results depend on the grammar and semantic rules supplied by the language." with: "What a language gets is in the [feature matrix](#feature-matrix); it depends on the grammar, semantic rules and helper sources the language supplies."

- [ ] **Step 7: Check and commit**

Run: `grep -n '^## ' docs/editor-support.md`
Expected: `Feature matrix`, `VS Code extension`, `Rider plugins`, `Installable plugins for a language`, `Helper sources`, `Languages inside C# strings`, in that order.

Run: `grep -c 'Available editor features include' docs/editor-support.md`
Expected: `0`.

```bash
git add docs/editor-support.md
git commit -m "Gather editor setup into one guide, with a feature matrix

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `docs/media.md`

**Files:**
- Create: `docs/media.md`, `docs/images/.gitkeep`

- [ ] **Step 1: Write `docs/media.md`**

````markdown
# Screenshots and videos for the README

The README marks each place a capture belongs with an HTML comment naming its file, such as
`<!-- media: docs/images/datecalc-hints.png -->`. To add a capture, save it under `docs/images/` and
replace that comment with an image line, for example:

```markdown
![DateCalc's sample with each statement's value](docs/images/datecalc-hints.png)
```

For every capture: VS Code with a dark theme, the editor about 1200 px wide, the window chrome cropped,
the Nitrogen extension installed, and `examples/DateCalc` opened as the workspace folder. PNG for stills;
GIF (or MP4 under 5 MB) for the quick fix.

| File | Open | Cursor or action | Must be visible |
| --- | --- | --- | --- |
| `datecalc-hints.png` | `sample.datecalc` | none | All eight statements with their values at line ends, and the semantic coloring of dates, durations, functions and constants. |
| `datecalc-completion.png` | `sample.datecalc` | On a new line, type `wee` and wait for completion | The completion list with `weekday` and `weeks`, and the details showing types. |
| `datecalc-hover.png` | `sample.datecalc` | Hover the `*` in `3 * sprint` | The hover with `DateCalc.Duration · DateCalc.Times · DateCalc.Weekday` and `= 42 days`. |
| `datecalc-quickfix.gif` | a new `fix.datecalc` with `let d = 2026-02-30;` | Put the cursor on the date, open quick fixes (Cmd+. / Ctrl+.), apply *Change to 2026-02-28* | The squiggle and DC0001 message, the menu, and the corrected line with its value appearing. |
| `csharp-strings.png` | `Snippets.cs` | none | The tagged raw string with `= 2026-12-25 Fri`, `= 81` and `= Friday`, and `= 2026-11-16 Mon` after the `Deadline` string, with DateCalc's coloring inside the strings. |

Rider captures are welcome too: name them with a `-rider` suffix (`datecalc-hints-rider.png`) and add them beside the VS Code ones.
````

- [ ] **Step 2: Add the images folder.** Run: `mkdir -p docs/images && touch docs/images/.gitkeep`

- [ ] **Step 3: Commit**

```bash
git add docs/media.md docs/images/.gitkeep
git commit -m "Add a capture checklist for the README's screenshots

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The new `README.md`

**Files:**
- Modify: `README.md` (replace it entirely)

- [ ] **Step 1: Write `README.md`** with exactly this content:

`````markdown
# Nitrogen

Nitrogen is a .NET language workbench. Describe a language once, its syntax in an `.ngr` grammar and
its types and operations in a little C#, and Nitrogen derives the rest: an incremental parser, binding
and semantic checks, typed HIR, a language server, and installable VS Code and Rider plugins. It follows
the language-workbench idea of [JetBrains' Nitra](https://github.com/JetBrains/Nitra).

## Build a language with an IDE

[examples/DateCalc](examples/DateCalc) is a complete typed language: a calculator with dates.

### The language

[DateCalc.ngr](examples/DateCalc/DateCalc.ngr) (84 lines) gives the syntax. Each rule says what it
lowers to, and `check` rules report source errors:

```text
token Date = Digit Digit Digit Digit "-" Digit Digit "-" Digit Digit;

| Day = Value:Date  lowers DateCalc.Date(text Value)
  {
    Type = DateCalcLanguage.Date;
    check DC0001 DateCalcLanguage.IsDate(Value.Text) : $"'{Value.Text}' is not a calendar date";
  }
| Add = Left:Expr "+" Right:Expr  precedence 10 left  lowers operation? Selected(Left, Right)
```

The rest is ordinary C#:

- [DateCalcLanguage.cs](examples/DateCalc/DateCalcLanguage.cs) (81 lines) declares the `DateCalc.Date`
  and `DateCalc.Duration` types and the operator overloads. So `date + duration`, `date - date` and
  `duration * number` type-check, and `date + date` is error `DC0002`.
  [MathModule.cs](examples/DateCalc/MathModule.cs) (58 lines) adds the math functions (`abs`, `sqrt`,
  `ln`, `sin`, `round`, `pow`, `min`, `max`, `mod` and more) and the constants `pi`, `e` and `tau` as a
  second semantic module. A call picks its overload by name and argument types, so `sqrt(2026-10-05)`
  is error `DC0003`.
- [DateCalcEvaluator.cs](examples/DateCalc/DateCalcEvaluator.cs) (112 lines) runs a program through one
  handler per operation. Its evaluation profile tells the editor how to show values.
- [DateCalcFixes.cs](examples/DateCalc/DateCalcFixes.cs) (54 lines) proposes quick fixes for DateCalc's
  errors.
- [nitrogen.json](examples/DateCalc/nitrogen.json) declares the language to the editor.

### What the editor gives you

[sample.datecalc](examples/DateCalc/sample.datecalc), as the editor shows it, with each statement
followed by its value:

```text
let start = 2026-10-05;                 = 2026-10-05 Mon
let sprint = 2 weeks;                   = 14 days
start + sprint;                         = 2026-10-19 Mon
weekday(start + 3 * sprint);            = Monday
(2026-12-25 - start) in days;           = 81
sprint / 1 days;                        = 14
round(2 * pi, 2);                       = 6.28
max(start + 10 weeks, 2026-12-25);      = 2026-12-25 Fri
```

<!-- media: docs/images/datecalc-hints.png -->

- **Coloring and completion** come from the grammar and the semantic modules: dates, durations,
  functions and constants are colored by kind, and completion offers names with their types.
  <!-- media: docs/images/datecalc-completion.png -->
- **Hover** shows what an expression lowers to, and its value. Hovering `3 * sprint` shows
  `DateCalc.Duration · DateCalc.Times` and `= 42 days`.
  <!-- media: docs/images/datecalc-hover.png -->
- **Diagnostics and quick fixes:** `2026-02-30` reports `DC0001` and offers *Change to 2026-02-28*,
  and `2026-12-25 + 2026-10-05` reports `DC0002` and offers *Use '-'*. The server offers a fix only
  after checking that it removes the error, so `2 weeks + 2026-10-05` gets none.
  <!-- media: docs/images/datecalc-quickfix.gif -->
- **`today`:** [countdown.datecalc](examples/DateCalc/countdown.datecalc) counts down to Christmas.
  Its values follow the clock and refresh at midnight.
- **Go to definition, references and rename** work across the language's files.
- **Inside C# strings:** a string tagged with the language gets the same support, values included.
  This is [Snippets.cs](examples/DateCalc/Snippets.cs):

```csharp
public static IReadOnlyList<DateCalcLine> Countdown() => DateCalcEvaluator.Run(/*lang=datecalc*/ """
    let christmas = 2026-12-25;          = 2026-12-25 Fri
    (christmas - 2026-10-05) in days;    = 81
    weekday(christmas);                  = Friday
    """);

// language=datecalc
const string Deadline = "2026-10-05 + 6 weeks;"  = 2026-11-16 Mon
```

<!-- media: docs/images/csharp-strings.png -->

The [feature matrix](docs/editor-support.md#feature-matrix) shows what works where in VS Code and Rider.

### Try it

- **In VS Code:** install the Nitrogen extension ([setup](docs/editor-support.md#vs-code-extension))
  and open `examples/DateCalc` as the workspace folder.
- **As installable plugins** for VS Code and Rider, carrying the language and a server:
  `nitrogen package --config examples/DateCalc/nitrogen.json --output dist`
  ([details](docs/editor-support.md#installable-plugins-for-a-language)).

Your own language takes the same pieces: a grammar, a `nitrogen.json`, and as much C# as its semantics
need. The [language guide](docs/language-guide.md) covers the grammar language.

## Agentic direction

Nitrogen is aimed at agents that assemble small, domain-specific programs from capabilities a host
offers. It gives an agent a machine-checkable path from a task to a host operation:

- Semantic modules and module descriptors declare qualified types, operation signatures, imports and
  required host bindings. A host exposes these contracts as a bounded set of capabilities for an agent
  to compose.
- Parsing, binding, semantic and value checks, typed HIR and source-located diagnostics let an agent
  repair an invalid program before it runs.
- Domain types can carry units, reference frames and entity categories when a language defines them,
  so those distinctions survive composition.
- Preflight or domain checks verify the program and its inputs before a host handler is invoked.

For example, Geometry accepts the syntax of `box 0 2 3;` but reports `GE0001`, because a box dimension
must be positive. An agent can use that diagnostic to correct the program before requesting a mesh.
Nitrogen provides composition, validation, diagnostics and typed HIR today; hosts supply execution for
supported slices.

The [Nitrogen agent skill](.agents/skills/nitrogen/SKILL.md) applies this across projects. It frames a
task as typed capability requirements, looks for existing concepts before inventing new ones, validates
a solution before authorized execution, and proposes what it learned as reviewable evidence. Setup is in
[the agent-skill guide](docs/agent-skill.md).

## Semantic direction

Nitrogen separates what a program means from how it is written:

- **Types and operations are semantic, not syntactic.** `SemanticType.Named(module, name)` gives
  module-qualified type identity, and an `OperationSignature` fixes an operation's input and result
  types. Semantic modules export types and operations, import others, and compose with conflict checks.
- **Grammars lower to typed HIR.** `lowers` clauses map syntax to catalog operations, and the result is
  typed HIR whose every node keeps its source origin. Errors, values and fixes all point back into the
  source. The grammar language itself lowers to HIR this way.
- **Execution goes through exact host handlers.** `HirProjector` runs HIR through handlers bound to
  exact signatures. The editor uses the same path: a language's evaluation profile and quick-fix
  provider are what show values and offer fixes.
- **Concepts are gathered across projects.** A separate semantic catalog (`TovarishN/Nitrogen.Concepts`)
  records concepts, capabilities, realizations, and evidence of reuse and of failure. The agent skill
  consults it before inventing an abstraction. A catalog concept guides work; it isn't runnable code
  until a host validates and admits it.

The [roadmap](docs/roadmap.md) tracks where this is going.

## Get started

Requirements: the .NET 10 SDK; Node.js and npm to build the VS Code extension; Gradle and JDK 25 to
build the Rider plugin.

```sh
dotnet build Nitrogen.slnx
dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj
./build.sh        # also builds the editor plugins into artifacts/; add --config nitrogen.json [--language NAME] for a language's plugins
```

The plugins use the release version in `Directory.Build.props`; `VERSION=1.2.3 ./build.sh` overrides
it, and version tags supply theirs. The [Plugins workflow](.github/workflows/plugins.yml) runs the same
script on Linux.

**Packages.** Releases publish three packages to GitHub Packages (`https://nuget.pkg.github.com/TovarishN/index.json`):

```xml
<PackageReference Include="Nitrogen.Runtime" Version="0.7.0" />
<PackageReference Include="Nitrogen.Generator" Version="0.7.0" PrivateAssets="all" />
<AdditionalFiles Include="MyLanguage.ngr" Namespace="My.Language.Syntax" />
```

and the `nitrogen` tool: `dotnet tool install Nitrogen.Cli --version 0.7.0`. Reading the feed needs a
GitHub token with `read:packages`; NuGet takes it from `NuGetPackageSourceCredentials_<source name>`
(`Username=<user>;Password=<token>`). `eng/package-smoke.sh` builds a consumer and runs the tool from
freshly packed packages; a `v*` tag publishes them.

**The CLI:**

```sh
nitrogen parse --grammar Nitrogen.Tests/Grammars/Calc.ngr --start Calc.Program path/to/sample.calc
nitrogen lsp                                                    # the language server
nitrogen package --config nitrogen.json --output dist           # installable plugins for a language
nitrogen generate vscode|rider --config nitrogen.json --output generated
```

`parse` and `watch` compile the given grammars in-process. `lsp` serves `.ngr` files and the languages
a workspace `nitrogen.json` declares. From a checkout, run any of these as
`dotnet run --project Nitrogen.Cli -- <command>`.

## Documentation

- [The grammar language](docs/language-guide.md): scopes, typed arguments, repetition, templates, and
  editor support for lowered languages.
- [Editor support](docs/editor-support.md): the feature matrix, VS Code and Rider setup, installable
  plugins, helper sources, and languages inside C# strings.
- [The agent skill and semantic catalog](docs/agent-skill.md).
- [Screenshots and videos](docs/media.md): how the README's captures are made.
- The [roadmap](docs/roadmap.md) and [milestone issue records](issues/) document Nitrogen's development.

## Project map

| Project | Role |
| --- | --- |
| `Nitrogen.Grammar` | Bootstrap `.ngr` grammar model and compiler |
| `Nitrogen.Generator` | C# source generator for grammar modules |
| `Nitrogen.Runtime` | Parsing, syntax, binding, semantics, and HIR |
| `Nitrogen.Ngr` | Self-hosted grammar language |
| `Nitrogen.Workspace` | Dynamic grammar compilation and workspace state |
| `Nitrogen.LanguageService` | Editor queries and LSP server |
| `Nitrogen.Geometry` | Standalone geometry language example |
| `examples/DateCalc` | A calculator with dates: typed overloads, values, quick fixes, and C# strings |
| `Nitrogen.Cli` | `parse`, `watch`, `lsp`, `package` and `generate` commands |
| `Nitrogen.Tests` | Standalone regression suite |

## License

MIT. See [LICENSE](LICENSE).
`````

- [ ] **Step 2: Check the facts it states.**
  - `grep -n 'watch' Nitrogen.Cli/*.cs | head -3` should show a `watch` command. If it doesn't, drop `watch` from the README in both places.
  - `wc -l examples/DateCalc/DateCalc.ngr examples/DateCalc/DateCalcLanguage.cs examples/DateCalc/MathModule.cs examples/DateCalc/DateCalcEvaluator.cs examples/DateCalc/DateCalcFixes.cs` should show 84, 81, 58, 112 and 54. Update the README's numbers if any differ.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Rewrite the README around building a DSL, and the agentic and semantic directions

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Generated README and the VS Code extension README

**Files:**
- Modify: `Nitrogen.Cli/VsCode/VsCodeRenderer.cs` (the `Readme` template)
- Modify: `editors/vscode/README.md` (line 22)
- Test: `Nitrogen.Tests/Cli/VsCodeGenerationTests.cs` (`Readme_names_statement_values_as_a_feature`)

- [ ] **Step 1: Write the failing assertions.** Rename `Readme_names_statement_values_as_a_feature` to `Readme_names_values_and_quick_fixes_as_features` and make its body:

```csharp
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        string readme = Read(request, "README.md");

        Assert.Contains("each statement's value as an inlay hint", readme, StringComparison.Ordinal);
        Assert.Contains("an expression's value on hover", readme, StringComparison.Ordinal);
        Assert.Contains("quick fixes", readme, StringComparison.Ordinal);
```

- [ ] **Step 2: Run it and check it fails**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~VsCodeGenerationTests"`
Expected: FAIL on "an expression's value on hover".

- [ ] **Step 3: Implement.** In `VsCodeRenderer.cs`'s `Readme`, replace "When the language's helper sources export an evaluation profile, its files also show each statement's value as an inlay hint." with:

"When the language's helper sources export an evaluation profile, its files also show each statement's value as an inlay hint and an expression's value on hover. When they export quick fixes (`DiagnosticFixes`), errors offer quick fixes, each checked by the server before it is offered."

- [ ] **Step 4: Point the extension README at the new guide.** In `editors/vscode/README.md` line 22, change "see the repository README." to "see [installable plugins for a language](../../docs/editor-support.md#installable-plugins-for-a-language)."

- [ ] **Step 5: Run the tests and check they pass**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~VsCodeGenerationTests"`
Expected: all pass. The byte-identical rendering test is unaffected, since both renders use the new text.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Cli/VsCode/VsCodeRenderer.cs Nitrogen.Tests/Cli/VsCodeGenerationTests.cs editors/vscode/README.md
git commit -m "Name hover values and quick fixes in generated VS Code READMEs

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Checks

**Files:**
- Modify: `docs/superpowers/specs/2026-10-08-readme-restructure-design.md` (status)

- [ ] **Step 1: Every old heading has a home.** Run:

```bash
python3 - <<'EOF'
import re, subprocess
old = subprocess.run(["git", "show", "119d547:README.md"], capture_output=True, text=True, check=True).stdout
home = {
    "Nitrogen": ("README.md", "Nitrogen"),
    "Why Nitrogen for agentic work": ("README.md", "Agentic direction"),
    "Agent skill and semantic catalog": ("docs/agent-skill.md", "The Nitrogen agent skill and semantic catalog"),
    "Example: a calculator with dates": ("README.md", "Build a language with an IDE"),
    "Requirements": ("README.md", "Get started"),
    "Build and test": ("README.md", "Get started"),
    "Build everything, including the editor plugins": ("README.md", "Get started"),
    "Use the CLI": ("README.md", "Get started"),
    "VS Code and generated language support": ("docs/editor-support.md", "VS Code extension"),
    "Rider and generated plugin support": ("docs/editor-support.md", "Rider plugins"),
    "Declaration scopes": ("docs/language-guide.md", "Declaration scopes"),
    "Inferred sequence arguments": ("docs/language-guide.md", "Inferred sequence arguments"),
    "Derived declarations": ("docs/language-guide.md", "Derived declarations"),
    "Typed repetition": ("docs/language-guide.md", "Typed repetition"),
    "Selected declarative roots": ("docs/language-guide.md", "Selected declarative roots"),
    "Deferred projection arguments": ("docs/language-guide.md", "Deferred projection arguments"),
    "Templates": ("docs/language-guide.md", "Templates"),
    "Use Nitrogen as packages": ("README.md", "Get started"),
    "Installable plugins for a language": ("docs/editor-support.md", "Installable plugins for a language"),
    "Editor support for lowered languages": ("docs/language-guide.md", "Editor support for lowered languages"),
    "Languages inside C# strings": ("docs/editor-support.md", "Languages inside C# strings"),
    "Project map": ("README.md", "Project map"),
    "License": ("README.md", "License"),
}
missing = []
for title in dict.fromkeys(re.findall(r"^#+ (.*)$", old, re.M)):
    if title not in home: missing.append(f"unmapped old heading: {title}"); continue
    path, new = home[title]
    if not re.search(rf"^#+ {re.escape(new)}$", open(path).read(), re.M): missing.append(f"{title} -> {path}#{new} not found")
print("\n".join(missing) or "every old heading has a home")
EOF
```

Expected: `every old heading has a home`.

- [ ] **Step 2: Every relative link resolves.** Run:

```bash
python3 - <<'EOF'
import os, re
files = ["README.md", "docs/language-guide.md", "docs/editor-support.md", "docs/agent-skill.md", "docs/media.md", "editors/vscode/README.md"]
def slug(heading):
    s = heading.strip().lower()
    s = re.sub(r"[^\w\- ]", "", s)
    return s.replace(" ", "-")
def anchors(path):
    return {slug(h) for h in re.findall(r"^#+ (.*)$", open(path).read(), re.M)}
bad = []
for f in files:
    text = open(f).read()
    text = re.sub(r"```.*?```", "", text, flags=re.S)  # links inside code blocks are examples
    for target in re.findall(r"\]\(([^)\s]+)\)", text):
        if re.match(r"^(https?:|mailto:)", target): continue
        path, _, anchor = target.partition("#")
        resolved = os.path.normpath(os.path.join(os.path.dirname(f), path)) if path else f
        if not os.path.exists(resolved): bad.append(f"{f}: {target} (no {resolved})"); continue
        if anchor and resolved.endswith(".md") and anchor not in anchors(resolved): bad.append(f"{f}: {target} (no #{anchor})")
print("\n".join(bad) or "all relative links resolve")
EOF
```

Expected: `all relative links resolve`. Fix any reported link in the file it names, and rerun.

- [ ] **Step 3: Versions.** Run `git diff 119d547 -- README.md docs editors | grep '^[-+].*0\.[0-9]\.[0-9]' | grep -v 'Version="0.7.0"\|--version 0.7.0'`.
Expected: no output except the `nitrogen-0.7.0.vsix` lines that moved from the README into `docs/editor-support.md` (moved, not changed).

- [ ] **Step 4: Build and tests**

Run: `dotnet build Nitrogen.slnx -warnaserror`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
Expected: all 1,089 pass (the generated-README test was renamed, not added).

- [ ] **Step 5: Spec status, and commit.** Change the spec's status line to `Status: implemented (YYYY-MM-DD).`, using the date of this step.

```bash
git add docs/superpowers/specs/2026-10-08-readme-restructure-design.md
git commit -m "Mark the README restructure implemented

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
