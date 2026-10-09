# Portable servers in the release plugins

Status: implemented (2026-10-09). Part of the goal of first-class language support.

## Problem

The plugins attached to a GitHub release don't start out of the box on most machines:

- **Rider** (`nitrogen-rider-X.Y.Z.zip`): `build.sh` generates it with `--bundle TARGET=…`, a
  self-contained single-file server for the machine that builds it. The Plugins workflow runs on
  Ubuntu, so the release plugin carries only a `linux-x64` server; on macOS and Windows it falls back to
  a `nitrogen` executable on `PATH`, which most users don't have.
- **VS Code** (`nitrogen-X.Y.Z.vsix`): carries no server at all, and runs `nitrogen.server.path`
  (default `nitrogen`) from `PATH` on every platform.

The plugins `nitrogen package` builds for a language already solve this: they carry a portable,
framework-dependent server (`bundle/server/nitrogen.dll`, about 11 MB) and start it with `dotnet`,
which they find through `DOTNET_ROOT`, the standard install locations and `PATH`.

## Goal

Both release plugins carry that portable server and start it with `dotnet`, so they work on macOS,
Windows and Linux wherever the .NET 10 runtime is installed, with a clear message when it isn't. A
server path set in the editor's settings still replaces the bundled one.

Out of scope: per-platform self-contained servers (about 70 MB each), downloading a server, and
features in Rider's C# strings (quick fixes, signature help).

## 1. Rider

- **Generator.** `nitrogen generate rider --self-contained` now also works with `--grammar` (it
  required `--config`). With `--grammar`, the plugin carries only the portable server in
  `bundle/server/` (no `bundle/language/`), copied as for `--config`: the server folder's files and its
  `runtimes` folder, from `--server DIR` or the running nitrogen's own folder.
- **Starting the server** (grammar mode): the executable set in Settings | Tools when there is one,
  run as `EXE lsp`; else `DOTNET bundle/server/nitrogen.dll lsp`, with `dotnet` found as the
  `--config` plugins find it (`DOTNET_ROOT`, the standard install locations, `PATH`), and the same
  error naming .NET 10 and the setting when it isn't found. No `--config` is passed: the server serves
  `.ngr` itself and the workspace's `nitrogen.json`, as the release plugin does today.
- **Unchanged:** `--self-contained` with `--bundle` is still an error, and `--bundle` stays for
  single-file servers.
- **`build.sh`** generates its Rider plugins with `--self-contained` instead of `--bundle`, and no
  longer publishes a single-file server for its own machine.

## 2. VS Code

- **Packaging.** `build.sh` copies the portable server into the extension's `server/` folder (the
  files of `Nitrogen.Cli/bin/Release/net10.0` and its `runtimes` folder, as the language bundle copies
  them) before `npm run package`. `.vscodeignore` keeps `server/`.
- **Settings.**
  - `nitrogen.server.path`: default `""` (was `"nitrogen"`); "The nitrogen executable (runs `nitrogen
    lsp`); empty runs the server bundled with the extension."
  - `nitrogen.dotnetPath` (new): default `""`; "The dotnet executable that runs the bundled server;
    empty finds it through DOTNET_ROOT, the standard install locations, and PATH."
- **Choosing the server.** `serverCommand` in `src/server.ts`, a pure function of the settings, the
  extension's folder, the environment and a file-exists check, returns a command and arguments or an
  error message:
  1. `nitrogen.server.path` set: `[path, "lsp"]`;
  2. else `server/nitrogen.dll` in the extension: `[dotnet, dll, "lsp"]`, `dotnet` being
     `nitrogen.dotnetPath` when set, else `DOTNET_ROOT/dotnet`, the standard install location
     (`%ProgramFiles%\dotnet\dotnet.exe`, `/usr/local/share/dotnet/dotnet`, `/usr/share/dotnet/dotnet`,
     `/usr/lib/dotnet/dotnet`), else the first `dotnet` on `PATH`; none found is the error "Nitrogen
     needs the .NET 10 runtime: install it, or set nitrogen.dotnetPath or nitrogen.server.path.";
  3. else (an extension built from source without a server): `["nitrogen", "lsp"]`.
- `extension.ts` uses it, and shows the error with `window.showErrorMessage` instead of starting.
- The `dotnet` lookup mirrors the generated extensions' (`VsCodeRenderer`); a comment in each points
  to the other.

## 3. Smoke check

After packaging, `build.sh` starts each server it carried, `dotnet <server>/nitrogen.dll lsp`, sends
`initialize`, `shutdown` and `exit`, and fails unless the server exits with 0. It runs for the VS Code
extension's `server/` and for each Rider plugin's `bundle/server/`, read from their build folders. The
Plugins workflow runs `build.sh` on pull requests touching `editors/`, `build.sh` or `Nitrogen.Cli/`,
on `main`, and on tags.

## 4. Docs

- `docs/editor-support.md`: the release plugins need only the .NET 10 runtime; the settings replace the
  bundled server; building the VS Code extension from source still means pointing
  `nitrogen.server.path` at a built CLI (or having `nitrogen` on `PATH`).
- The generated Rider plugin's README describes the grammar-mode bundle.
- The README, where it tells how to install the editor plugins.

## 5. Tests

- **Generator (C#):**
  - `generate rider --grammar … --start … --self-contained --server DIR` is accepted; the output has
    `bundle/server/nitrogen.dll` and no `bundle/language/`;
  - its `NitrogenLspSupport.kt` starts `bundle/server/nitrogen.dll` with `lsp` and no `--config`;
  - `--self-contained` with `--bundle` is still an error.
- **VS Code (`node --test`):** `serverCommand` with the setting set; with a bundled server and
  `dotnet` from `nitrogen.dotnetPath`, from `DOTNET_ROOT`, and from `PATH`; with a bundled server and no
  `dotnet` (the error); and with no bundled server (`nitrogen`).
- **Smoke check:** in `build.sh`, as above.

Run `dotnet build Nitrogen.slnx -warnaserror`, `dotnet test Nitrogen.Tests`, `npm test` in
`editors/vscode`, and `./build.sh`.
