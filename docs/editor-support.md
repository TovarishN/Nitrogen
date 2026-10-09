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
| Signature help³ | ✓ | ✓ | — (left to Rider) |
| Diagnostics | ✓ | ✓ | ✓ |
| Go to definition, references | ✓ | ✓ | ✓ (find usages) |
| Rename | ✓ | ✓ | — (left to Rider) |
| Outline | ✓ | — (left to C#) | — (left to Rider) |
| Folding | ✓ | — (left to C#) | — (left to Rider) |
| Expand selection | ✓ | — (left to C#) | — (left to Rider) |
| Workspace symbols | ✓ (open and closed files, and the `nitrogen.json` grammars) | ✓ (open C# files) | — (Rider's C# client keeps it off) |
| Hover, with the expression's value¹ | ✓ | ✓ | ✓ |
| Each statement's value at its end¹ | ✓ | ✓ | ✓ |
| Quick fixes² | ✓ | ✓ | — (Rider's C# client keeps code actions off, so its Alt+Enter menu holds only Rider's own) |

¹ When the language's helper sources export an `EvaluationProfile` (see [Helper sources](#helper-sources)).
² When they export `DiagnosticFixes`.
³ For `name(…)` calls, when the helper sources export `CallSignatures`, or for templates (`lowers template`).

## VS Code extension

The [VS Code extension](../editors/vscode/README.md) starts `nitrogen lsp`. The server serves `.ngr` files and compiles grammars declared in a workspace `nitrogen.json`, so a custom DSL can use the same extension. What a language gets is in the [feature matrix](#feature-matrix); it depends on the grammar, semantic rules and helper sources the language supplies.

For a `nitrogen.json` language, the server also reads the language's files in the workspace folder that are not open (skipping `bin`, `obj`, `node_modules`, and hidden folders), so references into closed files resolve and rename edits them. Diagnostics are reported for open files. Clients that support dynamic registration are asked to report changes to those files.

1. Build the language server from the repository root:

   ```sh
   dotnet build Nitrogen.slnx -c Release
   ```

2. Build and package the extension:

   ```sh
   cd editors/vscode
   npm ci
   npm run compile
   npm run package
   ```

3. Install `editors/vscode/nitrogen-0.8.0.vsix` using VS Code's **Extensions: Install from VSIX...** command, or run `code --install-extension nitrogen-0.8.0.vsix` from `editors/vscode` if the `code` command is available.
4. In VS Code settings, set `nitrogen.server.path` to the absolute path of the built CLI executable. For a Release build on macOS or Linux, this is `<repo>/Nitrogen.Cli/bin/Release/net10.0/nitrogen` (replace `<repo>` with this repository's absolute path). The extension passes `lsp` to that executable automatically. If `nitrogen` is already on `PATH`, the default setting works.
5. Open the repository folder in VS Code. To enable a custom language, put `nitrogen.json` at the workspace root. For the included Calc grammar:

   ```json
   {
     "languages": [
       {
         "name": "calc",
         "extensions": [".calc"],
         "grammars": ["Nitrogen.Tests/Grammars/Calc.ngr"],
         "start": "Calc.Program"
       }
     ]
   }
   ```

Open a `.calc` file such as `sample.calc` containing `1 + 2;`. The server recompiles the declared grammar when it changes and updates diagnostics for its files. Adjust the grammar path and start rule for another language.

## Rider plugins

The generic Rider plugin is in `editors/rider` and uses the same `nitrogen lsp`
server as VS Code. Build it with `gradle buildPlugin`, then install the ZIP in
Rider. The default executable is `nitrogen` on `PATH`.

To generate a grammar-specific plugin from a workspace configuration:

```sh
dotnet run --project Nitrogen.Cli -- generate rider \
  --config nitrogen.json --output generated/rider
```

Use `--language NAME` for a configuration containing multiple languages, or
use `--grammar FILE --start Module.Rule` when no `nitrogen.json` exists. The
plugin runs the executable set in Rider's Settings | Tools page, else a bundled
server for the current platform, else `nitrogen` (or `--nitrogen PATH`). To bundle
a server, publish it as a self-contained single file and pass it per platform,
for example `--bundle macos-aarch64=publish/nitrogen` after
`dotnet publish Nitrogen.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish`.
Bundles are copied from local files and never downloaded.

## Installable plugins for a language

A language project packages its `nitrogen.json` language as editor plugins:

```sh
nitrogen package --config nitrogen.json --output dist            # both
nitrogen package --config nitrogen.json --output dist --vscode   # dist/<id>-<version>.vsix
nitrogen package --config nitrogen.json --output dist --rider    # dist/<id>-<version>-rider.zip
```

Each plugin carries the grammar, helper sources, and a portable Nitrogen server, and runs it with the user's .NET 10 runtime (`dotnet`), so it works in any folder and on any OS. The server is started as `nitrogen lsp --config <bundled nitrogen.json>`, which ignores any `nitrogen.json` in the opened folder. Install the `.vsix` with **Extensions: Install from VSIX...**, and the ZIP with Rider's **Settings → Plugins → ⚙ → Install Plugin from Disk**. Packaging needs npm for VS Code, and Gradle with JDK 25 for Rider; the bundled server is the `nitrogen` that runs `package`, or `--server <directory>` for another framework-dependent build. `nitrogen generate vscode` and `nitrogen generate rider --self-contained` write the projects without building them. The optional `"version"` field of a language entry sets the plugin version (default: the Nitrogen CLI release version).

## Helper sources

A language's helper sources can also carry its semantics. Every public static `ModuleDescriptor` or
`SemanticModule` field or property in them is added to the language, so its declarative `lowers` and
`declares … type` clauses and C# lowerers run in the editor: hover shows typed HIR, and lowering errors
(such as a cyclic template expansion, `NH0007`, or `NH0004` from a lowerer that throws) appear in the file
they point into, even when found while lowering another open file. A lowering that is only blocked
(`NH0001`–`NH0003`: by recovered syntax, an unresolved name, or invalid semantics) is not shown: its cause
is reported on its own, or is no error at all, such as a name an open (`dynamic`) scope accepts. Composition errors such as `NC0001` or `NM0008` are reported on the
first grammar file. Sources compile with the .NET SDK's implicit usings. The optional `"namespace"` field
sets the C# namespace the grammars are generated into (default `Nitrogen.Workspace.Grammar`), so sources
written against a project's generated syntax compile unchanged. [Nitrogen.Geometry/nitrogen.json](../Nitrogen.Geometry/nitrogen.json)
packages Geometry this way:

```sh
nitrogen package --config Nitrogen.Geometry/nitrogen.json --output dist --vscode
```

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

A public static `CallSignatures` lists a language's callable names and their overloads, with named
parameters, for signature help: inside a `name(…)` call the editor shows the callee's overloads and
highlights the parameter the cursor is in. A template call needs none; its parameters come from the
template's declaration. A language has at most one; two are warning `NGR0005`.

## Languages inside C# strings

A C# string literal tagged with a language is served as a document of that language:

```csharp
var due = DateCalcEvaluator.Run(/*lang=datecalc*/ "2026-10-05 + 6 weeks;");

// language=datecalc
const string Deadline = "2026-10-05 + 6 weeks;";
```

A tag is a `/*lang=NAME*/` or `/* language=NAME */` comment right before the literal, or a
`// language=NAME` line comment before the statement whose first literal it tags (the conventions of
Rider and Visual Studio). `NAME` is a language's name or one of its extensions without the dot,
ignoring case. Regular, verbatim and raw literals are supported, including escapes and the indentation
of raw literals; interpolated literals are not. Coloring, diagnostics, completion, hover, go to
definition, references, rename, statement values and quick fixes work inside the string, with positions mapped to the C# file. The
strings of a language share its project with its files, so they can use names those files export.

VS Code shows one semantic-token provider per document, so the extension keeps the C# extension's
coloring and paints the server's tokens in C# files as decorations. Rename and outline in C# files stay
with C# outside the tagged strings. Generated VS Code plugins (`nitrogen package`) include the same
support and also activate for C# files; the generic extension leaves the strings of languages an
installed generated extension carries to that extension (`skipLanguages`, described below). Other LSP clients
get the server's results directly.

In Rider, C# files get a second Nitrogen client of their own (the platform switches features per
client, not per file). Its colors are added to Rider's, and diagnostics, completion, hover, go to
definition, find usages and statement values work in the strings; rename, structure view, formatting,
quick fixes and the rest stay with Rider. The generic plugin starts it when the project has a `nitrogen.json`; a generated plugin
always does, for its own language. A client whose server reads the workspace's `nitrogen.json` leaves
the strings of languages that another installed Nitrogen plugin carries in its bundle to that plugin
(it passes them as the `skipLanguages` initialization option, which any client can send), so they are
not served twice. Each Rider plugin also gives its language a minimal parser
definition, because Rider itself injects the language named by a `language=` comment, and asks the
server for semantic tokens in every file it serves (the platform asks only in plain-text and TextMate
files by default, so Rider showed no Nitrogen coloring before).
