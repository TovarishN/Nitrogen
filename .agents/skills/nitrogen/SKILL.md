---
name: nitrogen
description: Use when changing Nitrogen grammars, parser generation, runtime semantics, workspace compilation, CLI, language service, or tests in this repository.
---

# Nitrogen development

Nitrogen is a standalone .NET language workbench. The root `Nitrogen.slnx` is its supported build and test boundary. Read the relevant existing grammar, generated API usage, and tests before changing syntax or semantics.

- For `.ngr` changes, check the bootstrap compiler in `Nitrogen.Grammar`, generated modules in `Nitrogen.Generator`, and self-hosting in `Nitrogen.Ngr`. Generated C# is build output; edit the grammar or generator instead.
- For parsing or semantics, use the existing `Nitrogen.Runtime` APIs. Keep syntax and runtime behavior compatible unless a change explicitly calls for a migration.
- For editor changes, trace the path through `Nitrogen.Workspace`, `Nitrogen.LanguageService`, `Nitrogen.Cli`, and `editors/vscode` as applicable.
- Keep Gravity-specific MotionDSL adapters, benchmarks, and tests in the Gravity repository. Do not add a Gravity dependency to the standalone project graph to make an adapter test compile.
- Verify with `dotnet build Nitrogen.slnx` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj` for changes across the core. Report separately if Gravity integration has not been tested.
