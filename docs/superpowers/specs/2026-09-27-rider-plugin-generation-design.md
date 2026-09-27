# Nitrogen Rider plugin generation — design

## Goal

Provide both forms of JetBrains Rider integration:

1. A reusable generic Rider plugin for Nitrogen languages.
2. A generator that creates a grammar-specific Rider plugin from a Nitrogen
   workspace configuration or explicit grammar inputs.

Both forms must use the existing `nitrogen lsp` process for language behavior.
The Rider plugins are adapters and packaging targets, not second language
implementations.

## Scope and constraints

- Keep `Nitrogen.LanguageService` and `Nitrogen.Cli lsp` as the shared language
  service boundary.
- Add Rider support beside, not inside, the existing VS Code extension.
- The generic plugin must support `.ngr` and workspace-declared language
  extensions through `nitrogen.json`.
- A generated plugin must contain language-specific metadata such as the
  display name, file extensions, and workspace configuration expectations.
- The default server mode uses a user-configured global `nitrogen` executable.
- An optional bundled-server mode may package a platform-specific executable;
  the plugin must prefer an explicitly configured executable when present and
  report a clear error when neither configured nor bundled server is available.
- Generated output must be deterministic and safe to overwrite only within an
  explicitly selected output directory.
- No generated plugin may execute arbitrary grammar-authored code during
  generation. Grammar validation remains the responsibility of existing
  Nitrogen APIs and the generated plugin continues to use LSP.

## User-facing shape

The repository will expose a generic plugin build under `editors/rider` and a
CLI generation command, tentatively:

```text
nitrogen generate rider --config nitrogen.json --output ./generated/rider
```

The command will also support explicit grammar/start-rule inputs when no
workspace configuration is available. Exact option names should follow the
existing `CliOptions` conventions and should produce actionable diagnostics for
missing files, invalid JSON, missing language entries, unsupported extensions,
and invalid output paths.

The generated plugin will expose a Rider setting for the Nitrogen executable,
with the default value `nitrogen`. Its server launcher will append `lsp` and
will pass the workspace root so the existing workspace-language discovery path
remains authoritative. Bundled executables, when requested, will be placed in
a known plugin resource location and selected only for the matching runtime
platform.

## Architecture

### Generic plugin

`editors/rider` will be a Gradle-based IntelliJ Platform plugin. It will define:

- a file type for `.ngr`;
- a language/file association path for generated extensions;
- an LSP server support/provider registration using the current IntelliJ
  Platform LSP integration API;
- executable selection and process-launch configuration;
- plugin metadata and packaging tasks.

The generic plugin must not hard-code a specific custom grammar beyond `.ngr`.
Workspace-declared languages remain discovered from `nitrogen.json` by the
language server and are associated by generated or configured file patterns.

### Grammar-specific generator

The CLI generator will read a validated input model, normalize names and file
extensions, and render a fixed set of Rider plugin template files. Templates
will contain only data needed by the plugin adapter: plugin identity, language
display metadata, extension associations, server configuration, and optional
bundled-server descriptors.

The generator will reject duplicate language identities, malformed extensions,
path traversal in output-relative resource names, unsupported bundle targets,
and configurations that do not identify a start rule. It will write through a
staging directory and publish the result only after all files are rendered
successfully.

### Bundled executable mode

Bundling is an explicit generation option, not an implicit download. The
generator accepts local executable paths for declared target platforms, copies
them into the plugin resource layout, and records checksums/metadata in the
generated descriptor. It will not fetch binaries or infer a host executable.
The runtime selection order is:

1. Rider user setting, when non-empty;
2. matching bundled executable;
3. configured default `nitrogen` on `PATH`.

If a configured path is invalid, the plugin reports that error rather than
silently falling through to a different binary.

## Error handling and compatibility

- CLI errors use the existing diagnostic/exit-code conventions and do not
  leave partially rendered output.
- Plugin startup errors are visible through Rider's LSP/plugin notification
  path and include the resolved executable source.
- Existing VS Code behavior and `nitrogen lsp` protocol behavior remain
  unchanged.
- The generic plugin and generated plugins share templates and launcher logic
  where practical, but generated output must remain independently buildable.

## Testing and verification

Tests will be added before implementation for:

- command parsing and required-option diagnostics;
- conversion of `nitrogen.json` entries into normalized plugin metadata;
- deterministic rendering and stable file ordering;
- invalid extension/name/path/bundle-input rejection;
- staging publication behavior after a rendering failure;
- global executable, configured executable, and bundled executable selection;
- generated metadata containing the requested language associations;
- generic plugin template smoke validation.

Verification will include the focused CLI/generator tests, the full
`Nitrogen.Tests` suite, and a Rider plugin build/package check when the local
Gradle/IntelliJ toolchain is available. Toolchain availability will be
reported separately from .NET test results.

## Out of scope

- Implementing a second parser, semantic engine, or grammar compiler in the
  Rider plugin.
- Remote binary downloads, automatic updates, marketplace publishing, signing,
  or cross-platform release automation.
- Replacing the existing VS Code extension.
- Making Rider understand arbitrary grammars without either workspace metadata
  or a generated plugin.

