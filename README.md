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
<PackageReference Include="Nitrogen.Runtime" Version="0.7.1" />
<PackageReference Include="Nitrogen.Generator" Version="0.7.1" PrivateAssets="all" />
<AdditionalFiles Include="MyLanguage.ngr" Namespace="My.Language.Syntax" />
```

and the `nitrogen` tool: `dotnet tool install Nitrogen.Cli --version 0.7.1`. Reading the feed needs a
GitHub token with `read:packages`; NuGet takes it from `NuGetPackageSourceCredentials_<source name>`
(`Username=<user>;Password=<token>`). `eng/package-smoke.sh` builds a consumer and runs the tool from
freshly packed packages; a `v*` tag publishes them and drafts the GitHub release, with the packages and editor
plugins attached, for its notes to be written.

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
