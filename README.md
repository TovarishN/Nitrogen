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

## Requirements

- .NET 10 SDK or newer
- Node.js and npm only if building the VS Code extension

## Build and test

```sh
dotnet build Nitrogen.slnx
dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj
```

The solution contains the Nitrogen projects listed below.

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

3. Install `editors/vscode/nitrogen-0.1.0.vsix` using VS Code's **Extensions: Install from VSIX...** command, or run `code --install-extension nitrogen-0.1.0.vsix` from `editors/vscode` if the `code` command is available.
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
