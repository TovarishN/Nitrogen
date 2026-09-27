# Rider Plugin Generation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable Rider LSP plugin and a deterministic `nitrogen generate rider` command that produces grammar-specific Rider plugins, with configurable global or optional bundled Nitrogen executables.

**Architecture:** Keep `nitrogen lsp` as the only language implementation. Add a small, testable C# generator model/renderer in `Nitrogen.Cli`, a CLI dispatch path, shared Rider plugin templates under `editors/rider`, and generated plugin metadata/resources rendered from normalized `nitrogen.json` language entries. The Rider launcher resolves an explicit user executable first, then a matching bundled executable, then the `nitrogen` PATH default.

**Tech Stack:** .NET 10/C#, xUnit, `System.Text.Json`, Kotlin/JVM IntelliJ Platform Gradle plugin, JetBrains LSP integration API, deterministic text templates.

**Spec:** `docs/superpowers/specs/2026-09-27-rider-plugin-generation-design.md`

## Global Constraints

- Keep `Nitrogen.LanguageService` and `Nitrogen.Cli lsp` as the shared language service boundary.
- The default server mode uses a user-configured global `nitrogen` executable.
- An optional bundled-server mode may package a platform-specific executable.
- Bundling is an explicit generation option, not an implicit download.
- Generated output must be deterministic and safe to overwrite only within an explicitly selected output directory.
- Existing VS Code behavior and `nitrogen lsp` protocol behavior remain unchanged.
- No generated plugin may execute arbitrary grammar-authored code during generation.

## Review Focus

- A malformed or incomplete `nitrogen.json` must fail before creating output: test invalid JSON, missing languages, missing start, and malformed extensions in Task 1.
- A configured executable path must not silently fall through to another binary: test executable precedence and invalid explicit paths in Task 4.
- A failed render must not publish a partial plugin: test staging cleanup/preservation in Task 3.
- Generated metadata must remain stable across equivalent input ordering: test canonical ordering and repeated rendering in Task 3.
- Bundled binaries must be platform-specific and never downloaded or inferred: test bundle manifest validation and copy behavior in Task 4.

## File Map

- Create `Nitrogen.Cli/Rider/RiderPluginModel.cs`: immutable normalized input/output model and bundle target records.
- Create `Nitrogen.Cli/Rider/RiderPluginInput.cs`: `nitrogen.json` and explicit-input parsing/validation.
- Create `Nitrogen.Cli/Rider/RiderPluginRenderer.cs`: deterministic template rendering and staged publication.
- Create `Nitrogen.Cli/Rider/RiderPluginCommand.cs`: command execution, filesystem diagnostics, and exit codes.
- Modify `Nitrogen.Cli/CliOptions.cs`: recognize `generate rider` arguments and expose parsed generation options.
- Modify `Nitrogen.Cli/NitrogenCli.cs`: dispatch the generation command without changing parse/watch behavior.
- Modify `Nitrogen.Cli/Program.cs`: route `lsp` and generated-plugin commands through the existing CLI boundary.
- Create `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`: red/green coverage for model, renderer, and command behavior.
- Create `editors/rider/build.gradle.kts`: generic and generated-plugin Gradle build configuration.
- Create `editors/rider/settings.gradle.kts`: Gradle project identity.
- Create `editors/rider/gradle.properties`: pinned IntelliJ platform/plugin versions and JVM settings.
- Create `editors/rider/src/main/resources/META-INF/plugin.xml`: generic plugin metadata and LSP extension registration.
- Create `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenSettings.kt`: persistent executable/bundle settings.
- Create `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt`: executable resolution and LSP server launch.
- Create `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenFileType.kt`: `.ngr` file type and generic language metadata.
- Create `editors/rider/src/test/kotlin/org/nitrogen/rider/NitrogenLspSupportTest.kt`: launcher precedence tests where the Rider SDK is available.
- Create `editors/rider/README.md`: generic plugin build/install and bundling instructions.
- Modify `README.md`: link Rider build and generated-plugin workflows from the root documentation.

### Task 1: Add the generation input model and CLI parsing

**Files:**
- Create: `Nitrogen.Cli/Rider/RiderPluginModel.cs`
- Create: `Nitrogen.Cli/Rider/RiderPluginInput.cs`
- Modify: `Nitrogen.Cli/CliOptions.cs`
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`

**Interfaces:**
- `RiderPluginRequest ParseRequest(IReadOnlyList<string> args, out string error)` returns a normalized request or a diagnostic string.
- `RiderPluginModel Load(string configPath, string? grammarPath, string? startRule, string pluginName, IReadOnlyList<RiderBundleInput> bundles)` returns validated language metadata.
- `RiderPluginRequest` carries `ConfigPath`, `OutputDirectory`, `PluginName`, `NitrogenPath`, and bundle inputs.
- `RiderPluginModel` carries canonical plugin ID, display name, extensions, grammar paths, start rule, and sorted bundle targets.

- [ ] **Step 1: Write the failing tests** for `generate rider --config ... --output ...`, explicit `--grammar/--start`, missing output/config, invalid JSON, missing language entries, duplicate extensions, extensions without a leading dot, and canonical ordering.
- [ ] **Step 2: Run the focused test filter to verify the failures are feature failures.**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~RiderPluginGenerationTests`

Expected: compilation/test failures because the generation request and model do not exist.

- [ ] **Step 3: Implement the minimal immutable request/model and JSON validation.** Use `JsonDocument`/`JsonSerializer` with explicit property checks; normalize extension casing and slash separators, preserve grammar paths relative to the config directory, reject absolute or traversal output-relative names, and sort extensions/bundles ordinally.
- [ ] **Step 4: Add `generate rider` parsing without changing existing parse/watch semantics.** Keep existing `CliOptions.Parse` behavior unchanged for `parse` and `watch`; expose a separate internal parser for generation so old usage/error tests remain valid.
- [ ] **Step 5: Run the focused tests and the existing CLI tests.**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests|FullyQualifiedName~CliOptionsTests|FullyQualifiedName~CliTests"`

Expected: PASS.
- [ ] **Step 6: Commit the input/model slice.**

```sh
git add Nitrogen.Cli/Rider/RiderPluginModel.cs Nitrogen.Cli/Rider/RiderPluginInput.cs Nitrogen.Cli/CliOptions.cs Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "feat: add Rider plugin generation input model"
```

### Task 2: Add CLI dispatch and command diagnostics

**Files:**
- Create: `Nitrogen.Cli/Rider/RiderPluginCommand.cs`
- Modify: `Nitrogen.Cli/NitrogenCli.cs`
- Modify: `Nitrogen.Cli/Program.cs`
- Modify: `Nitrogen.Cli/CliOptions.cs`
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`

**Interfaces:**
- `Task<int> RiderPluginCommand.RunAsync(RiderPluginRequest request, TextWriter output, CancellationToken cancel)` returns `0` on publication and `1` on input/render errors.
- `NitrogenCli.RunAsync` continues to return `2` for unusable command arguments and delegates valid generation requests to `RiderPluginCommand`.

- [ ] **Step 1: Add failing command tests** for successful dispatch, missing input diagnostics, output-path diagnostics, cancellation, and the rule that a failed command leaves no newly created output directory.
- [ ] **Step 2: Run only those tests and confirm they fail for missing dispatch/command behavior.**
- [ ] **Step 3: Implement dispatch and command-level error formatting.** Keep `lsp` in `Program.cs` as the existing exact-argument route; generation must run through `NitrogenCli` so tests can invoke it without a process.
- [ ] **Step 4: Run CLI and generation tests.**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests|FullyQualifiedName~CliTests"`

Expected: PASS.
- [ ] **Step 5: Commit the command slice.**

```sh
git add Nitrogen.Cli/Rider/RiderPluginCommand.cs Nitrogen.Cli/NitrogenCli.cs Nitrogen.Cli/Program.cs Nitrogen.Cli/CliOptions.cs Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "feat: expose Rider plugin generation command"
```

### Task 3: Render deterministic generic and grammar-specific plugin files

**Files:**
- Create: `Nitrogen.Cli/Rider/RiderPluginRenderer.cs`
- Create: `editors/rider/build.gradle.kts`
- Create: `editors/rider/settings.gradle.kts`
- Create: `editors/rider/gradle.properties`
- Create: `editors/rider/src/main/resources/META-INF/plugin.xml`
- Create: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenSettings.kt`
- Create: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt`
- Create: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenFileType.kt`
- Create: `editors/rider/src/test/kotlin/org/nitrogen/rider/NitrogenLspSupportTest.kt`
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`

**Interfaces:**
- `RiderPluginRenderer.Render(RiderPluginModel model, string outputDirectory, CancellationToken cancel)` stages and publishes a complete plugin source tree.
- Template substitution accepts only the normalized model fields and writes a fixed, sorted relative-file list.
- The generic plugin source tree is the canonical template; generated plugins render the same launcher/settings code with language metadata injected.

- [ ] **Step 1: Write failing renderer tests** for expected files, plugin metadata, `.ngr` and custom extension associations, stable output bytes on repeated renders, stable output with reordered JSON inputs, and no publication when a template write fails.
- [ ] **Step 2: Run the tests and confirm failure because no renderer/templates exist.**
- [ ] **Step 3: Add the minimal Gradle IntelliJ Platform project.** Pin an IntelliJ Platform version compatible with the current Rider LSP API, register the current `com.intellij.platform.lsp.integrationProvider` extension point, and keep the build source-only with no generated binary checked in.
- [ ] **Step 4: Implement the renderer with fixed templates and atomic staging.** Render UTF-8 LF files in sorted order, create the output parent only as needed, write to a sibling staging directory, then move the completed tree into the requested output directory only after every file succeeds.
- [ ] **Step 5: Implement `.ngr` file metadata and generated extension metadata.** The generic plugin always registers `.ngr`; grammar-specific output adds normalized extensions and the language display identity.
- [ ] **Step 6: Run focused C# tests and the Rider Gradle test/package task if the local Gradle toolchain is available.**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~RiderPluginGenerationTests`

Run from `editors/rider`: `./gradlew test buildPlugin`

Expected: C# tests PASS; Gradle either PASS or is reported as unavailable with its exact diagnostic.
- [ ] **Step 7: Commit the renderer and generic plugin.**

```sh
git add Nitrogen.Cli/Rider/RiderPluginRenderer.cs editors/rider Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "feat: add deterministic Rider plugin templates"
```

### Task 4: Add executable resolution and optional bundle support

**Files:**
- Modify: `Nitrogen.Cli/Rider/RiderPluginModel.cs`
- Modify: `Nitrogen.Cli/Rider/RiderPluginInput.cs`
- Modify: `Nitrogen.Cli/Rider/RiderPluginRenderer.cs`
- Modify: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenSettings.kt`
- Modify: `editors/rider/src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt`
- Modify: `editors/rider/src/main/resources/META-INF/plugin.xml`
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`
- Test: `editors/rider/src/test/kotlin/org/nitrogen/rider/NitrogenLspSupportTest.kt`

**Interfaces:**
- `RiderExecutableSelection Select(string configuredPath, string? bundledPath, Func<string, bool> exists, Func<string, string?> which)` implements explicit configured-path, bundled-path, then PATH fallback selection.
- `RiderBundleInput` contains a target platform identifier and an existing local executable path.
- Generated bundle metadata records the target and SHA-256 checksum; no network operation is permitted.

- [ ] **Step 1: Write failing tests** for configured path precedence, invalid configured path rejection, matching bundled target selection, nonmatching bundle rejection, checksum metadata, and `lsp` argument preservation.
- [ ] **Step 2: Run focused tests and confirm the expected failures.**
- [ ] **Step 3: Implement explicit bundle validation/copying and checksum generation.** Accept only declared target identifiers (`macos-aarch64`, `macos-x64`, `linux-x64`, `windows-x64`), require regular local files, copy into a fixed resource directory, and never download or search outside provided paths.
- [ ] **Step 4: Implement Rider settings and launcher resolution.** A non-empty configured path is authoritative; otherwise choose the matching bundled resource; otherwise use `nitrogen` as the process command. Always append `lsp`, and report the source of the selected executable in startup errors.
- [ ] **Step 5: Run all focused tests plus the Gradle plugin test/package task.**
- [ ] **Step 6: Commit bundle support.**

```sh
git add Nitrogen.Cli/Rider editors/rider Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "feat: support configurable and bundled Rider servers"
```

### Task 5: Documentation and full verification

**Files:**
- Create: `editors/rider/README.md`
- Modify: `README.md`
- Modify: `docs/roadmap.md`

- [ ] **Step 1: Add documentation** for building/installing the generic plugin, setting the executable, invoking `nitrogen generate rider`, supplying explicit grammar inputs, and supplying local platform bundles.
- [ ] **Step 2: Add a checked-in example configuration and generated-output command** using the existing Calc grammar without checking in generated build output or binaries.
- [ ] **Step 3: Run formatting and repository checks.**

Run: `git diff --check`

Run: `dotnet build Nitrogen.slnx`

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`

Run from `editors/rider`: `./gradlew test buildPlugin`

Expected: .NET build/test pass; Gradle pass when available; any toolchain limitation is recorded separately.
- [ ] **Step 4: Review the complete diff for unchanged VS Code/LSP behavior, deterministic output, and unrelated file changes.**
- [ ] **Step 5: Commit documentation and final verification record.**

```sh
git add README.md editors/rider/README.md docs/roadmap.md
git commit -m "docs: document Rider plugin generation"
```

## Final verification checklist

- [ ] `dotnet build Nitrogen.slnx`
- [ ] `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`
- [ ] `git diff --check`
- [ ] `editors/rider/./gradlew test buildPlugin` when Gradle dependencies/toolchain are available
- [ ] Existing VS Code package path remains unchanged
- [ ] No generated plugin binary or bundled executable is committed
- [ ] User-visible output names the selected executable source and does not silently fall through after an invalid explicit path

