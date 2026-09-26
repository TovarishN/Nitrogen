# Nitrogen

Nitrogen is a .NET language workbench for defining parsers and syntax trees with `.ngr` grammars. It includes incremental parsing, binding and semantics, grammar recompilation in a workspace, a command-line parser, a language server, and a VS Code client.

## Requirements

- .NET 10 SDK or newer
- Node.js and npm only if building the VS Code extension

## Build and test

```sh
dotnet build Nitrogen.slnx
dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj
```

The solution contains the standalone Nitrogen projects. `Nitrogen.MotionDsl` and `Nitrogen.Benchmarks` are retained Gravity integrations. They reference Gravity's `MotionDSL` project and its asset corpus, so they are outside the standalone solution. Gravity-specific tests remain in `Nitrogen.Tests` as source but are excluded from its standalone build. The original `Gravity/Nitrogen` tree is still present because Gravity currently consumes it; this repository is an extracted copy, not yet a replacement for that dependency.

The [roadmap](docs/roadmap.md) and [milestone issue records](issues/) document the six Nitrogen milestones and their migration from Gravity.

## Use the CLI

```sh
dotnet run --project Nitrogen.Cli -- parse --grammar Nitrogen.Tests/Grammars/Calc.ngr --start Calc.Program path/to/sample.calc
dotnet run --project Nitrogen.Cli -- lsp
```

`parse` and `watch` compile supplied grammars in-process. `lsp` serves `.ngr` files and languages declared by a workspace `nitrogen.json`. The standalone server does not register Gravity's Motion and Policy languages.

The VS Code client is in [`editors/vscode`](editors/vscode/README.md).

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
