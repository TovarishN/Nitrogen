# Installable language plugins — design

**Date:** 2026-09-30. **Status:** approved conversational design, pending review of this written spec.

## Goal

A language project — a repository with a `nitrogen.json` that declares a Nitrogen language — builds installable editor plugins for that language with one command:

```
nitrogen package --config nitrogen.json [--language NAME] --output DIR [--vscode] [--rider] [--server DIR]
```

It writes `DIR/<id>-<version>.vsix` for VS Code and `DIR/<id>-<version>-rider.zip` for Rider (the IntelliJ plugin distribution format, installed with Settings → Plugins → ⚙ → Install Plugin from Disk). Without `--vscode` or `--rider` it builds both.

Both plugins are **self-contained**: they carry the language's grammar, helper sources, and a portable Nitrogen language server, so a matching file opened in any folder gets diagnostics, navigation, and rename. Both run the server with the user's **.NET 10 runtime** (`dotnet nitrogen.dll`), so one `.vsix` and one ZIP serve macOS, Linux, and Windows.

Out of scope: publishing to the VS Code Marketplace or JetBrains Marketplace, signing, per-platform native servers for VS Code, and TextMate grammars (colouring comes from LSP semantic tokens, as today).

## What exists

- `nitrogen lsp` serves `.ngr` files and the languages of the `nitrogen.json` in the LSP workspace root.
- `editors/vscode` is a generic extension: it starts the executable in `nitrogen.server.path` and selects files by the extensions of the workspace's `nitrogen.json`.
- `nitrogen generate rider --config … --output …` renders a Gradle project for one language. Its plugin registers the file type and starts `nitrogen lsp`; the server still reads the workspace's `nitrogen.json`. `--bundle TARGET=PATH` embeds native single-file servers.

The gap: no language-specific VS Code package, no plugin that works outside the language project's folder, and no single command that builds both.

## Design

### 1. `nitrogen lsp --config PATH`

`PATH` names a file called `nitrogen.json`. The server configures its languages from that file's directory at `initialized`, exactly as it does for a workspace root, and ignores the root the client sends. Without `--config`, behaviour is unchanged. A missing file or another file name exits with code 2 and a message on stderr.

Implementation: `LspServer` takes an optional fixed root; `LspCommand.RunAsync` passes it; `Program.cs` accepts `["lsp", "--config", path]`.

### 2. Shared language model

`RiderPluginModel` becomes `LanguagePluginModel` and gains what a self-contained plugin needs: source patterns, usings, the raw `tokens` object, and a `version` (optional `"version"` field in the language entry, default `0.1.0`, validated as `MAJOR.MINOR.PATCH`). Both generators and `package` read it through the existing `LoadConfig`, which keeps its current diagnostics. The explicit `--grammar/--start` form stays Rider-only.

### 3. Language bundle

`LanguageBundle.Stage(model, serverDirectory, destination)` writes:

- `language/nitrogen.json`: one language entry — name, extensions, start, usings, tokens — with `grammars` and `sources` rewritten to `grammars/<file>` and `sources/<file>`.
- `language/grammars/*`, `language/sources/*`: the files matched by the configured patterns, using the language service's pattern rules (`dir/*.ngr` or a plain path). Two matched files with the same name, or a pattern that matches nothing, is an error.
- `server/`: a recursive copy of `serverDirectory`, which must contain `nitrogen.dll` and `nitrogen.runtimeconfig.json` (a framework-dependent build). The default is the running `nitrogen`'s own directory (`AppContext.BaseDirectory`); a single-file native `nitrogen` has no `nitrogen.dll` there and must be given `--server`.

Output is deterministic: files are written in ordinal order with LF line endings, and nothing depends on time or machine.

### 4. VS Code: `nitrogen generate vscode`

```
nitrogen generate vscode --config nitrogen.json [--language NAME] --output DIR [--server DIR]
```

Renders an extension project and stages the bundle into `DIR/bundle`:

- `package.json`: name `nitrogen-<id>`, publisher `nitrogen`, the model's version and display name; one contributed language (id `nitrogen-<id>`, the extensions, `language-configuration.json`); activation on that language; settings `nitrogen-<id>.dotnetPath` and `nitrogen-<id>.server.path`. Dependencies and scripts are those of `editors/vscode`, and `package-lock.json` is `editors/vscode`'s lock with its root name rewritten, so `npm ci` is reproducible.
- `src/extension.ts`: starts `<server.path> lsp --config <bundle>/language/nitrogen.json` when `server.path` is set; otherwise `<dotnet> <bundle>/server/nitrogen.dll lsp --config …`. `dotnet` resolves from `dotnetPath`, then `DOTNET_ROOT`, then the standard install locations, then `PATH`. If the server cannot start, the extension shows an error naming the .NET 10 runtime and the setting.
- `tsconfig.json` (from `editors/vscode`), `language-configuration.json` (brackets and quotes only; the grammar does not describe comments), `README.md`, and `.vscodeignore` excluding `src/` and `node_modules/`.

`editors/vscode/package-lock.json`, `package.json` dependency versions, and `tsconfig.json` are embedded in `nitrogen` as resources at build time, so the generated project always matches the checked-in extension.

### 5. Rider: `nitrogen generate rider --self-contained`

With `--self-contained [--server DIR]`, the generator stages the bundle into `DIR/bundle`, and:

- `build.gradle.kts` copies `bundle/` into the plugin distribution beside `lib/` (`prepareSandbox`), so the files are on disk after installation.
- The launcher finds the bundle through the plugin's install path. It runs the configured executable with `lsp --config …` when the existing Settings | Tools executable is set; otherwise `<dotnet> <bundle>/server/nitrogen.dll lsp --config …`, with `dotnet` resolved as in VS Code. Failure raises an `ExecutionException` naming the .NET 10 runtime.

Without `--self-contained`, the generator is unchanged, including `--bundle` native servers. `--self-contained` and `--bundle` are mutually exclusive.

### 6. `nitrogen package`

Validates the config, then checks for the tools each requested target needs before building anything: `npm` for VS Code; `gradle` and a Java runtime (`JAVA_HOME` or `java` on `PATH`) for Rider. A missing tool is an error naming it. For each target it generates into `DIR/.build/<target>`, runs the build (`npm ci`, `npm run compile`, `npx vsce package --skip-license --allow-missing-repository --out …`; `gradle buildPlugin`), streams the tools' output, and copies the result to `DIR`. A failing step stops with its exit code. Exit codes: 0 success, 1 build failure, 2 unusable arguments or missing tool.

### 7. Language project follow-up

In `TovarishN/Nitrogen.Concepts`, after this merges: bump `external/Nitrogen`, replace `tools/editors/build.sh` with a call to `nitrogen package`, drop `.vscode/settings.json` and the CRLF guard, and update the README and the `SemanticCatalog.EditorSupport` realization.

## Testing

- `lsp --config`: an LSP session whose workspace root has no `nitrogen.json` serves a language from the given config; a root with a different `nitrogen.json` is ignored.
- Config: `version`, `sources`, `usings`, and `tokens` are read; a bad version is rejected.
- Bundle: rewritten config, copied files, glob expansion, duplicate-name and no-match errors, missing `nitrogen.dll`, determinism (two stagings are byte-identical).
- VS Code renderer: `package.json` fields, activation, settings, language ID; lockfile root name; `extension.ts` launch arguments.
- Rider `--self-contained`: Gradle copies `bundle/`, the launcher passes `--config`, mutual exclusion with `--bundle`.
- `package`: argument errors and missing-tool errors, without running npm or Gradle.
- End to end, by hand in the plan: package the Nitrogen.Concepts catalog language, inspect the `.vsix` and ZIP, and run each plugin's exact server command over LSP from a folder without `nitrogen.json`. Installing in VS Code and Rider remains a manual check.
