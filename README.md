# Nitrogen

Nitrogen is a .NET language workbench for defining parsers and syntax trees with `.ngr` grammars. It includes incremental parsing, binding and semantics, grammar recompilation in a workspace, a command-line parser, a language server, and a VS Code client.

Nitrogen implements the language-workbench idea explored by [JetBrains' Nitra](https://github.com/JetBrains/Nitra): describe a language once, then derive parsers, syntax models, and editor support from its definition.

## Why Nitrogen for agentic work

Nitrogen is aimed at agents that assemble small, domain-specific programs from capabilities offered by a host. The language workbench gives an agent a machine-checkable path from a task to a host operation:

- Semantic modules and module descriptors declare qualified types, operation signatures, imports, and required host bindings. A host can expose these contracts as a bounded set of capabilities for an agent to compose.
- Parsing, binding, semantic and value checks, typed HIR, and source-located diagnostics let an agent repair an invalid program before it runs.
- Domain types can carry units, reference frames, and entity categories when a language defines them, preserving those distinctions across composed capabilities.
- Preflight or domain-specific checks verify the validated program and its inputs before a host handler is invoked.

For example, Geometry accepts the syntax of `box 0 2 3;` but reports `GE0001` because a box dimension must be positive. An agent can use that diagnostic to correct the program before requesting a mesh. Nitrogen provides composition, validation, diagnostics, and typed HIR today; hosts supply execution for supported slices.

### Agent skill and semantic catalog

The [Nitrogen agent skill](.agents/skills/nitrogen/SKILL.md) applies this approach across projects. It frames a task as typed capability requirements, searches a separate semantic catalog for concepts and evidence, validates a solution before authorized execution, and proposes reusable observations through a reviewable pull request. A catalog concept may guide work in another language; it is not necessarily a runnable Nitrogen module.

The private catalog is `TovarishN/Nitrogen.Concepts`. With access to that repository, clone it beside Nitrogen and point the skill at the checkout:

```sh
git clone git@github.com:TovarishN/Nitrogen.Concepts.git ../Nitrogen.Concepts
export NITROGEN_CONCEPT_CATALOG="$(cd ../Nitrogen.Concepts && pwd)"
```

To make the maintained skill available to Codex from other projects, run the following from this Nitrogen repository after checking that the destination does not already contain another skill:

```sh
mkdir -p "$HOME/.codex/skills"
cp -R .agents/skills/nitrogen "$HOME/.codex/skills/nitrogen"
```

After updating Nitrogen, copy the updated skill files into that installed directory so the personal installation stays current. This environment uses a regular directory because its filesystem sandbox does not support a symlinked skill root.

The skill also checks for an existing sibling catalog checkout. If the private repository is unavailable, the agent can finish the main task and keep catalog findings as an unpublished local draft. Catalog publication does not grant permission to execute a module: a receiving host must validate its exact contract and admission requirements.

## Requirements

- .NET 10 SDK or newer
- Node.js and npm only if building the VS Code extension
- Gradle/JDK only if building the Rider plugin

## Build and test

```sh
dotnet build Nitrogen.slnx
dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj
```

The solution contains the Nitrogen projects listed below.

### Build everything, including the editor plugins

```sh
./build.sh                                     # add --config nitrogen.json [--language NAME] for a language plugin
```

This builds and tests Nitrogen, then writes the plugins to `artifacts/`: the VS Code extension
(`nitrogen-*.vsix`) and Rider plugins (`rider/*-rider.zip`) that bundle a single-file server for the
current machine. It needs the .NET 10 SDK, Node.js with npm, Gradle, and a JDK 25 (Rider 2026.2's Java version). The
[Plugins workflow](.github/workflows/plugins.yml) runs the same script on Linux and publishes the
plugins as build artifacts. Both plugins use the release version in `Directory.Build.props`;
`VERSION=1.2.3 ./build.sh` overrides it, and version tags supply their version automatically.
Generated language plugins default to the CLI release version; an explicit language `"version"`
in `nitrogen.json` takes precedence.

The [roadmap](docs/roadmap.md) and [milestone issue records](issues/) document Nitrogen's development.

## Use the CLI

```sh
dotnet run --project Nitrogen.Cli -- parse --grammar Nitrogen.Tests/Grammars/Calc.ngr --start Calc.Program path/to/sample.calc
dotnet run --project Nitrogen.Cli -- lsp
```

`parse` and `watch` compile supplied grammars in-process. `lsp` serves `.ngr` files and languages declared by a workspace `nitrogen.json`.

## VS Code and generated language support

The [VS Code extension](editors/vscode/README.md) starts `nitrogen lsp`. The server serves `.ngr` files and compiles grammars declared in a workspace `nitrogen.json`, so a custom DSL can use the same extension. Available editor features include diagnostics, semantic coloring, outline, go to definition, references, rename, hover, and completion; the results depend on the grammar and semantic rules supplied by the language.

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

3. Install `editors/vscode/nitrogen-0.3.0.vsix` using VS Code's **Extensions: Install from VSIX...** command, or run `code --install-extension nitrogen-0.3.0.vsix` from `editors/vscode` if the `code` command is available.
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

## Rider and generated plugin support

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

## Declaration scopes

A `scope` clause opens a local scope for a rule's children. A declaration normally belongs to its enclosing scope. Use `in file` to place selected declarations in the file scope:

```text
syntax Body = "body" Name:Identifier "{" Items:Item* "}" declares body Name scope;
syntax Let = "let" Name:Identifier "=" Value:Expr declares value Name;
syntax Part = "part" Name:Identifier declares part Name in file;
```

Here, values are local to each body, while parts are visible throughout the same file. `in file` also places dynamic-name openness and duplicate-name checks in the file scope. It does not export a symbol to other files; add `export` after `in file` when project visibility is required. The existing `type` clause can follow these modifiers.

Use `declares value Name sequential` for ordered declarations: a concrete name becomes visible after its declaring node ends, and later declarations in the same scope replace it. Its initializer can therefore use the previous value or an enclosing value. Self and forward references without such a value are not visible. Lexical completion uses the same ordering. Repeated names are allowed when all declarations in the group are sequential; mixing ordinary and sequential declarations still reports duplicates. Use `sequential` before `in file`, `export`, and `type`. Exported sequential names expose the final declaration from each document; names exported by multiple documents remain ambiguous.

## Inferred sequence arguments

Operation arguments may use `sequence inferred Field` when their element type depends on the selected operation. For example, `lowers operation Selected(sequence inferred Args)` takes the exact element type from the corresponding `Core.Sequence<T>` input of `Selected`. Every item must lower as that type; no casts or unit conversions are inserted. Empty lists keep the signature's element type, and separated lists skip separator nodes. A fixed non-sequence input fails composition; a computed non-sequence input fails semantic checking. The field must still be a repeated or separated list. `inferred` is special in this argument position; a fixed type with that name can be qualified with its module.

## Derived declarations

Names emitted by checked lowering can replace authored file-scope templates using `Project.SetDerivedDeclarations(path, kind, declarations)`. Each `DerivedDeclaration` carries the emitted name, a source node and name span in that document, and an optional export flag. The replacement is the complete index for that kind: it removes template declarations, seals dynamic file-scope openness, and retains local declarations and indexes for other kinds. An empty index means no names were emitted. Normal lookup, definition locations, completion, duplicate checks, and export visibility then use the emitted names.

For staged expansion, clear the template index before lowering the relevant subtrees, collect declarations from successful typed projection, then publish the complete index and validate the whole file again. Do not publish a partial result after projection fails. Updating an index invalidates binding resolution and project semantic caches; replacing or removing the document discards its derived indexes. Invalid node/span metadata is rejected before any project change. This API does not run domain expansion itself or automatically connect a language's editor service to its projector.

## Typed repetition

`lowers repeat ElementType CountField IteratorField TemplateListField` lowers a checked count and a typed template list into `HirRepeat`. For example:

```text
syntax Repeat = Count:Expr "as" Iterator:Iterator "{" Items:(Expr; ",")* "}" scope
                lowers repeat Core.Scalar Count Iterator Items;
syntax Iterator = Name:Identifier declares value Name sequential type Core.Scalar;
```

The count and declared iterator must be `Core.Scalar`; every template item must have `ElementType`. The result is `Core.Sequence<Core.Sequence<ElementType>>`, retaining one group per iteration. Use this value inside a host operation through normal declarative lowering, or obtain it with `HirLowering.LowerNested`.

`HirProjector` checks the template before running handlers, including for zero iterations. During projection, it binds the iterator to indices starting at zero, preserves enclosing bindings for nested loops, and restores them afterward. Positive fractional counts truncate toward zero, nonpositive counts yield no groups, and nonfinite counts or counts outside the supported signed 32-bit index range fail with source diagnostics. Host handlers receive typed projected groups and do not need to inspect source syntax. Numeric `HirEvaluator` is not the structured projection path.

Floating-point projection inputs must be finite; `NP0001` is reported at the reference during preflight before handlers run. Floating-point operation results must also be finite; a nonfinite computed result reports `NP0001` at the producing operation and stops evaluation before downstream handlers. Result validation runs during projection because a handler's computed value is not available to structural preflight. Other domain constraints still require domain checks.

### Selected declarative roots

`HirLowering.LowerSelected(file, syntaxKinds, snapshotId)` lowers selected declarative syntax kinds in source order without collecting registered roots from other language regions. It performs the same recovery, binding, and semantic checks as nested lowering. A selected node with no supported lowering reports `NH0005` instead of disappearing. Consumers can select their language's domain roots without implementing a syntax traversal; callers decide whether any diagnostic invalidates the complete result.

Whole-file semantic checking remains the default. An optional fourth argument, `SemanticCheckScope.SubtreeAndAncestors`, uses `FileSemantics.DiagnosticsForSubtree(node)`: checks in the subtree and its ancestors run once, including ancestor checks that report at a child. Property dependencies still evaluate lazily; missing syntax and unresolved bindings still prevent lowering. Unrelated checks remain pending for `Diagnostics()`, which completes validation without repeating checked nodes. Both modes keep the first reading of ambiguous syntax.

Use scoped checking only when unrelated sibling checks cannot report errors at the selected subtree's source span. It is a partial validation API, not whole-file approval. Consumers must run complete diagnostics before admitting an output or invoking effects. Gravity uses scoped body preflight with pure rig projection, then publishes the generated-part index and checks the complete file before returning any rig.

### Deferred projection arguments

Use `ProjectionHandler.Deferred(signature, get => ...)` for synchronous operations that select which arguments to evaluate. `get(index)` evaluates that typed argument on demand and caches its result for the current invocation. It returns null after an argument failure; the operation cannot hide that failure by returning a value. Argument access expires when the handler returns. The registry requires exactly one eager or deferred implementation per handler, with the same exact catalog signature checks.

Whole-tree preflight still checks all branches, including unselected ones, before any handler runs. Deferred evaluation skips unrequested computed results and their handlers; it does not bypass missing bindings, invalid constant/input values, or signature checks. The runtime supplies argument access, while the host implements its domain's condition and truth conventions. Returned types and finite numeric values use the same validation as eager handlers.

The [roadmap](docs/roadmap.md) and [milestone issue records](issues/) document Nitrogen's development.

## Templates

A declaring rule with `lowers template Body(Params)` is a template, and a referencing rule with
`lowers expand Name(Args)` expands the template its `Name` resolves to, in any file of the project:

```text
syntax Definition = "def" Name:Identifier "(" Params:(Parameter; ",")* ")" "=" Body:Shape
                    declares shape Name export scope lowers template Body(Params);
syntax Parameter  = Name:Identifier ":" Type:Identifier declares parameter Name type Type;
syntax Make       = "make" Name:Identifier "(" Args:(Dimension; ",")* ")" ";"
                    references shape Name lowers expand Name(Args);
```

A template body is never a lowering root. An expansion has its body's type; it lowers the body with
each parameter (by position) replaced by the argument lowered at the call, keeping the parameter
reference's origin, and its result's origins start with the call's. The call is checked for a template
callee (`NT0007`), the argument count (`NT0008`, at the name) and each argument's type against its
parameter's declared type (`NT0001`). An expansion is blocked by errors within the template, reported
there (`NH0001`–`NH0003`), and a cycle reports `NH0007` at the name of the call that closes it.
Geometry's `def`/`make` use these clauses; see the [design](docs/superpowers/specs/2026-10-02-declarative-templates-design.md).

## Use Nitrogen as packages

Releases publish three packages to GitHub Packages (`https://nuget.pkg.github.com/TovarishN/index.json`):

```xml
<PackageReference Include="Nitrogen.Runtime" Version="0.2.0" />
<PackageReference Include="Nitrogen.Generator" Version="0.2.0" PrivateAssets="all" />
<AdditionalFiles Include="MyLanguage.ngr" Namespace="My.Language.Syntax" />
```

and the `nitrogen` tool: `dotnet tool install Nitrogen.Cli --version 0.2.0`. Reading the feed needs a GitHub token with `read:packages`; NuGet takes it from `NuGetPackageSourceCredentials_<source name>` (`Username=<user>;Password=<token>`). `eng/package-smoke.sh` builds a consumer and runs the tool from freshly packed packages; a `v*` tag publishes them.

## Use the CLI

```sh
dotnet run --project Nitrogen.Cli -- parse --grammar Nitrogen.Tests/Grammars/Calc.ngr --start Calc.Program path/to/sample.calc
dotnet run --project Nitrogen.Cli -- lsp
```

`parse` and `watch` compile supplied grammars in-process. `lsp` serves `.ngr` files and languages declared by a workspace `nitrogen.json`.

## VS Code and generated language support

The [VS Code extension](editors/vscode/README.md) starts `nitrogen lsp`. The server serves `.ngr` files and compiles grammars declared in a workspace `nitrogen.json`, so a custom DSL can use the same extension. Available editor features include diagnostics, semantic coloring, outline, go to definition, references, rename, hover, and completion; the results depend on the grammar and semantic rules supplied by the language.

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

3. Install `editors/vscode/nitrogen-0.3.0.vsix` using VS Code's **Extensions: Install from VSIX...** command, or run `code --install-extension nitrogen-0.3.0.vsix` from `editors/vscode` if the `code` command is available.
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

## Rider and generated plugin support

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

A language's helper sources can also carry its semantics. Every public static `ModuleDescriptor` or
`SemanticModule` field or property in them is added to the language, so its declarative `lowers` and
`declares … type` clauses and C# lowerers run in the editor: hover shows typed HIR, and lowering errors
(such as a cyclic template expansion, `NH0007`, or `NH0004` from a lowerer that throws) appear in the file
they point into, even when found while lowering another open file. A lowering that is only blocked
(`NH0001`–`NH0003`: by recovered syntax, an unresolved name, or invalid semantics) is not shown: its cause
is reported on its own, or is no error at all, such as a name an open (`dynamic`) scope accepts. Composition errors such as `NC0001` or `NM0008` are reported on the
first grammar file. Sources compile with the .NET SDK's implicit usings. The optional `"namespace"` field
sets the C# namespace the grammars are generated into (default `Nitrogen.Workspace.Grammar`), so sources
written against a project's generated syntax compile unchanged. [Nitrogen.Geometry/nitrogen.json](Nitrogen.Geometry/nitrogen.json)
packages Geometry this way:

```sh
nitrogen package --config Nitrogen.Geometry/nitrogen.json --output dist --vscode
```

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
| `Nitrogen.Cli` | `parse`, `watch`, and `lsp` commands |
| `Nitrogen.Tests` | Standalone regression suite |

## License

MIT. See [LICENSE](LICENSE).
