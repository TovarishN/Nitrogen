# Portable Release Servers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The release VS Code extension and Rider plugin carry the portable (framework-dependent) Nitrogen server and start it with `dotnet`, so they work on macOS, Windows and Linux.

**Architecture:** `generate rider --self-contained` gains a grammar mode that carries only the server; `build.sh` uses it for the Rider plugins, copies the same server into the VS Code extension, and smoke-tests every carried server. The VS Code extension chooses its server through a pure, tested `serverCommand`.

**Tech Stack:** C# / .NET 10 (generator, xUnit), Kotlin templates (generated Rider plugin), TypeScript (`node --test`), bash.

Spec: `docs/superpowers/specs/2026-10-09-portable-release-servers-design.md`.

---

## Files

- Modify `Nitrogen.Cli/Rider/RiderPluginModel.cs` (`CarriesLanguage`), `Nitrogen.Cli/Rider/RiderPluginInput.cs`, `Nitrogen.Cli/Rider/RiderPluginRenderer.cs`, `Nitrogen.Cli/LanguageBundle.cs` (`StageServer`).
- Modify `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`.
- Create `editors/vscode/src/server.ts`, `editors/vscode/src/server.test.ts`; modify `editors/vscode/src/extension.ts`, `editors/vscode/package.json`.
- Modify `build.sh`.
- Modify `docs/editor-support.md`, the spec (status).

Commands (repo root): `dotnet build Nitrogen.slnx -warnaserror`; `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests"`; `(cd editors/vscode && npm test)`; the whole .NET suite with `examples/DateCalc/sample.datecalc` stashed (`git stash push examples/DateCalc/sample.datecalc`, then `git stash pop`; never commit it).

---

### Task 1: Generator: `--self-contained` with `--grammar`

**Files:**
- Modify: `Nitrogen.Cli/Rider/RiderPluginModel.cs`, `Nitrogen.Cli/Rider/RiderPluginInput.cs`, `Nitrogen.Cli/Rider/RiderPluginRenderer.cs`, `Nitrogen.Cli/LanguageBundle.cs`
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `RiderPluginGenerationTests`, after `Self_contained_plugin_ships_the_bundle_and_starts_it_with_dotnet`:

```csharp
    [Fact]
    public void A_self_contained_grammar_plugin_carries_only_the_server()
    {
        using var dir = new TempDirectory();
        string grammar = dir.Write("Calc.ngr", "syntax module Calc { }");
        dir.Write("server/nitrogen.dll", "dll");
        dir.Write("server/nitrogen.runtimeconfig.json", "{}");
        var request = RiderPluginInput.ParseRequest(new[] { "generate", "rider", "--grammar", grammar, "--start", "Calc.File",
            "--output", Path.Combine(dir.Path, "out"), "--self-contained", "--server", Path.Combine(dir.Path, "server") }, out string error);
        Assert.Equal("", error);
        RiderPluginRenderer.Render(request!, request!.OutputDirectory, CancellationToken.None);
        string Read(string path) => File.ReadAllText(Path.Combine(request.OutputDirectory, path));

        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "server", "nitrogen.dll")));
        Assert.False(Directory.Exists(Path.Combine(request.OutputDirectory, "bundle", "language")));
        Assert.Contains("PrepareSandboxTask", Read("build.gradle.kts"));
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "src/main/kotlin/org/nitrogen/rider/NitrogenLanguageBundle.kt")));
        string lsp = Read("src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt");
        Assert.Contains("GeneralCommandLine(NitrogenLanguageBundle.dotnet(), server, \"lsp\")", lsp);
        Assert.Contains("languagesOfOtherPlugins", lsp); // the server reads the workspace's nitrogen.json, as without a bundle
        Assert.DoesNotContain("--config", lsp);
        Assert.Contains("carries a portable server", Read("README.md"));
    }
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~A_self_contained_grammar_plugin"`
Expected: FAIL: `Assert.Equal("", error)` gets `--self-contained needs --config`.

- [ ] **Step 3: The request**

In `Nitrogen.Cli/Rider/RiderPluginModel.cs`, replace the record with:

```csharp
/// <param name="SelfContainedServer">The framework-dependent server to bundle (<c>--self-contained</c>); null for a plugin that runs an installed nitrogen.</param>
/// <param name="CarriesLanguage">Whether the bundle holds the language too (<c>--config</c>), else only the server, which then serves .ngr itself and the workspace's nitrogen.json (<c>--grammar</c>).</param>
internal sealed record RiderPluginRequest(
    LanguagePluginModel Model,
    string OutputDirectory,
    string NitrogenPath,
    IReadOnlyList<RiderBundleInput> Bundles,
    string? SelfContainedServer = null,
    bool CarriesLanguage = true);
```

In `Nitrogen.Cli/Rider/RiderPluginInput.cs`:
1. Delete the line `if (config is null) return FailRequest("--self-contained needs --config", out error);`.
2. Change the final `return new RiderPluginRequest(model, Path.GetFullPath(output), nitrogen, bundles, selfContainedServer);` to:

```csharp
        return new RiderPluginRequest(model, Path.GetFullPath(output), nitrogen, bundles, selfContainedServer, CarriesLanguage: config is not null);
```

- [ ] **Step 4: Staging only the server**

In `Nitrogen.Cli/LanguageBundle.cs`, replace the start of `Stage` (from `serverDirectory = Path.TrimEndingDirectorySeparator(…)` through the `destination.StartsWith(…)` check) with a call to a shared check, and add `StageServer` and `CheckServer`:

```csharp
    /// <summary>Replaces <paramref name="destination"/> with the bundle. Throws <see cref="ArgumentException"/> for unusable input.</summary>
    public static void Stage(LanguagePluginModel model, string serverDirectory, string destination)
    {
        serverDirectory = CheckServer(serverDirectory, destination);
        destination = Path.GetFullPath(destination);
        var grammars = Match(model.GrammarPaths, "grammar");
        var sources = Match(model.SourcePaths, "source");
        // (the rest of Stage is unchanged)
```

```csharp
    /// <summary>Replaces <paramref name="destination"/> with a bundle holding only the server, for a plugin whose server serves its language itself. Throws <see cref="ArgumentException"/> for unusable input.</summary>
    public static void StageServer(string serverDirectory, string destination)
    {
        serverDirectory = CheckServer(serverDirectory, destination);
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        CopyServer(serverDirectory, Path.Combine(destination, "server"));
    }

    /// <summary>The server directory, full; throws when it is not a framework-dependent build or the bundle would be written inside it.</summary>
    static string CheckServer(string serverDirectory, string destination)
    {
        serverDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDirectory));
        if (!IsServer(serverDirectory))
            throw new ArgumentException($"'{serverDirectory}' is not a framework-dependent Nitrogen build (nitrogen.dll and nitrogen.runtimeconfig.json); pass --server");
        if (Path.GetFullPath(destination).StartsWith(serverDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("the bundle cannot be written inside the server directory");
        return serverDirectory;
    }
```

Update the class summary to: `A self-contained language for an editor plugin: <c>language/nitrogen.json</c> with its grammars and helper sources, and <c>server/</c>, a framework-dependent Nitrogen build run as <c>dotnet server/nitrogen.dll lsp --config language/nitrogen.json</c>; or only <c>server/</c>, run as <c>dotnet server/nitrogen.dll lsp</c>.`

- [ ] **Step 5: Rendering**

In `Nitrogen.Cli/Rider/RiderPluginRenderer.cs`:

1. The file map: `NitrogenLspSupport.kt` becomes

```csharp
                ["src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt"] = request.SelfContainedServer is not null && request.CarriesLanguage ? SelfContainedLspKt(request) : LspKt(request),
```

2. The staging line `if (request.SelfContainedServer is { } server) LanguageBundle.Stage(…);` becomes

```csharp
            if (request.SelfContainedServer is { } server)
            {
                if (request.CarriesLanguage) LanguageBundle.Stage(request.Model, server, Path.Combine(staging, "bundle"));
                else LanguageBundle.StageServer(server, Path.Combine(staging, "bundle"));
            }
```

3. In `LspKt`, replace

```
        fun commandLine(): GeneralCommandLine =
            GeneralCommandLine(NitrogenSettings.getInstance().resolveExecutable(defaultExecutable), "lsp")
```

with a line holding only `{{(request.SelfContainedServer is null ? InstalledCommandLine : BundledCommandLine)}}`, and add beside `LspKt`:

```csharp
    const string InstalledCommandLine = """
        fun commandLine(): GeneralCommandLine =
            GeneralCommandLine(NitrogenSettings.getInstance().resolveExecutable(defaultExecutable), "lsp")
""";

    const string BundledCommandLine = """
        /** The executable set in Settings when there is one, else the bundled server on the dotnet host. */
        fun commandLine(): GeneralCommandLine {
            val settings = NitrogenSettings.getInstance()
            if (settings.executable.isNotBlank()) return GeneralCommandLine(settings.resolveExecutable(defaultExecutable), "lsp")
            val server = NitrogenLanguageBundle.directory().resolve("server/nitrogen.dll").toString()
            return GeneralCommandLine(NitrogenLanguageBundle.dotnet(), server, "lsp")
        }
""";
```

4. The README tail: replace

```csharp
""" + (request.SelfContainedServer is null ? "" : """

This plugin carries its language and a portable server in `bundle/`, run with `dotnet` (.NET 10); a Nitrogen executable set in Settings replaces the bundled server.
""");
```

with

```csharp
""" + (request.SelfContainedServer is null ? "" : request.CarriesLanguage ? """

This plugin carries its language and a portable server in `bundle/`, run with `dotnet` (.NET 10); a Nitrogen executable set in Settings replaces the bundled server.
""" : """

This plugin carries a portable server in `bundle/server/`, run with `dotnet` (.NET 10); a Nitrogen executable set in Settings replaces it.
""");
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~RiderPluginGenerationTests|FullyQualifiedName~LanguageBundleTests|FullyQualifiedName~PackageTests"`
Expected: all pass. Then `dotnet build Nitrogen.slnx -warnaserror` and the whole suite (stashed).

- [ ] **Step 7: Commit**

```bash
git add Nitrogen.Cli/Rider/RiderPluginModel.cs Nitrogen.Cli/Rider/RiderPluginInput.cs Nitrogen.Cli/Rider/RiderPluginRenderer.cs Nitrogen.Cli/LanguageBundle.cs Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "Carry a portable server in a grammar's Rider plugin

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: VS Code: choosing the server

**Files:**
- Create: `editors/vscode/src/server.ts`, `editors/vscode/src/server.test.ts`
- Modify: `editors/vscode/src/extension.ts`, `editors/vscode/package.json`

- [ ] **Step 1: Write the failing tests**

Create `editors/vscode/src/server.test.ts`:

```ts
import * as assert from 'node:assert/strict';
import * as path from 'node:path';
import { test } from 'node:test';
import { serverCommand, ServerInputs } from './server';

const extension = path.join(path.sep, 'ext');
const dll = path.join(extension, 'server', 'nitrogen.dll');

function inputs(overrides: Partial<ServerInputs>, files: string[] = [dll]): ServerInputs {
  return { serverPath: '', dotnetPath: '', extensionPath: extension, env: {}, platform: 'linux', exists: file => files.includes(file), ...overrides };
}

test('a server path set in the settings wins', () => {
  assert.deepEqual(serverCommand(inputs({ serverPath: '/opt/nitrogen' })), { command: '/opt/nitrogen', args: ['lsp'] });
});

test('the bundled server runs on the dotnet setting', () => {
  assert.deepEqual(serverCommand(inputs({ dotnetPath: '/x/dotnet' })), { command: '/x/dotnet', args: [dll, 'lsp'] });
});

test('dotnet is found through DOTNET_ROOT, the standard location, then PATH', () => {
  assert.deepEqual(serverCommand(inputs({ env: { DOTNET_ROOT: '/r' } }, [dll, '/r/dotnet'])), { command: '/r/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({}, [dll, '/usr/share/dotnet/dotnet'])), { command: '/usr/share/dotnet/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({ env: { PATH: '/a:/b' } }, [dll, '/b/dotnet'])), { command: '/b/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({ platform: 'darwin' }, [dll, '/usr/local/share/dotnet/dotnet'])),
    { command: '/usr/local/share/dotnet/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({ platform: 'win32', env: { ProgramFiles: 'C:\\PF' } }, [dll, 'C:\\PF\\dotnet\\dotnet.exe'])),
    { command: 'C:\\PF\\dotnet\\dotnet.exe', args: [dll, 'lsp'] });
});

test('without dotnet the bundled server cannot start', () => {
  const result = serverCommand(inputs({ env: { PATH: '/a' } }));
  assert.ok('error' in result);
  assert.match(result.error, /\.NET 10/);
  assert.match(result.error, /nitrogen\.dotnetPath/);
});

test('without a bundled server, nitrogen runs from PATH', () => {
  assert.deepEqual(serverCommand(inputs({}, [])), { command: 'nitrogen', args: ['lsp'] });
});
```

In `editors/vscode/package.json`, change the `test` script to build and run both test files:

```json
    "test": "esbuild src/otherExtensions.test.ts src/server.test.ts --bundle --platform=node --target=node20 --format=cjs --outdir=out/test && node --test out/test/otherExtensions.test.js out/test/server.test.js"
```

- [ ] **Step 2: Run them to see them fail**

Run: `(cd editors/vscode && npm test)`
Expected: esbuild fails: `Could not resolve "./server"`.

- [ ] **Step 3: Implement**

Create `editors/vscode/src/server.ts`:

```ts
import * as path from 'path';

/** What choosing the server depends on: the settings, the extension's folder, the environment and the file system. */
export interface ServerInputs {
  /** nitrogen.server.path */
  serverPath: string;
  /** nitrogen.dotnetPath */
  dotnetPath: string;
  extensionPath: string;
  env: Record<string, string | undefined>;
  platform: string;
  exists: (file: string) => boolean;
}

export type ServerCommand = { command: string; args: string[] } | { error: string };

/**
 * The server to start: the executable set in the settings; else the server bundled in server/, on the
 * dotnet host; else nitrogen from PATH (an extension built from source, without a bundled server).
 */
export function serverCommand(inputs: ServerInputs): ServerCommand {
  if (inputs.serverPath) return { command: inputs.serverPath, args: ['lsp'] };
  const dll = path.join(inputs.extensionPath, 'server', 'nitrogen.dll');
  if (!inputs.exists(dll)) return { command: 'nitrogen', args: ['lsp'] };
  const dotnet = findDotnet(inputs);
  return dotnet
    ? { command: dotnet, args: [dll, 'lsp'] }
    : { error: 'Nitrogen needs the .NET 10 runtime: install it, or set nitrogen.dotnetPath or nitrogen.server.path.' };
}

/**
 * The dotnet host: the setting, then DOTNET_ROOT, then the standard install locations, then PATH.
 * The extensions `nitrogen package` generates look in the same places (VsCodeRenderer).
 */
function findDotnet(inputs: ServerInputs): string | undefined {
  if (inputs.dotnetPath) return inputs.dotnetPath;
  const windows = inputs.platform === 'win32';
  const paths = windows ? path.win32 : path.posix;
  const exe = windows ? 'dotnet.exe' : 'dotnet';
  const candidates: string[] = [];
  if (inputs.env.DOTNET_ROOT) candidates.push(paths.join(inputs.env.DOTNET_ROOT, exe));
  if (windows) candidates.push(paths.join(inputs.env.ProgramFiles ?? 'C:\\Program Files', 'dotnet', exe));
  if (inputs.platform === 'darwin') candidates.push('/usr/local/share/dotnet/dotnet');
  if (inputs.platform === 'linux') candidates.push('/usr/share/dotnet/dotnet', '/usr/lib/dotnet/dotnet');
  for (const directory of (inputs.env.PATH ?? inputs.env.Path ?? '').split(windows ? ';' : ':'))
    if (directory) candidates.push(paths.join(directory, exe));
  return candidates.find(inputs.exists);
}
```

In `editors/vscode/src/extension.ts`:
1. Imports: `import { ExtensionContext, extensions, window, workspace } from 'vscode';` and `import { serverCommand } from './server';`.
2. Replace the two lines starting `const command = workspace.getConfiguration('nitrogen').get<string>('server.path') || 'nitrogen';` with:

```ts
  const settings = workspace.getConfiguration('nitrogen');
  const server = serverCommand({
    serverPath: settings.get<string>('server.path') ?? '',
    dotnetPath: settings.get<string>('dotnetPath') ?? '',
    extensionPath: context.extensionPath,
    env: process.env,
    platform: process.platform,
    exists: file => fs.existsSync(file),
  });
  if ('error' in server) {
    void window.showErrorMessage(server.error);
    return;
  }
  const serverOptions: ServerOptions = { command: server.command, args: server.args };
```

In `editors/vscode/package.json`, `contributes.configuration.properties`:

```json
        "nitrogen.server.path": {
          "type": "string",
          "default": "",
          "description": "The nitrogen executable (runs `nitrogen lsp`); empty runs the server bundled with the extension."
        },
        "nitrogen.dotnetPath": {
          "type": "string",
          "default": "",
          "description": "The dotnet executable that runs the bundled server; empty finds it through DOTNET_ROOT, the standard install locations, and PATH."
        }
```

In `Nitrogen.Cli/VsCode/VsCodeRenderer.cs`, extend the generated `dotnet()` doc comment to: `/** The dotnet host: the setting, then DOTNET_ROOT, then the standard install locations, then PATH. The Nitrogen extension's server.ts looks in the same places. */`.

- [ ] **Step 4: Run the tests and the build**

Run: `(cd editors/vscode && npm test && npm run compile)`
Expected: all tests pass; compile succeeds. Then `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~VsCodeGenerationTests"` (the comment change): all pass.

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/src/server.ts editors/vscode/src/server.test.ts editors/vscode/src/extension.ts editors/vscode/package.json Nitrogen.Cli/VsCode/VsCodeRenderer.cs
git commit -m "Run the VS Code extension's bundled server on dotnet

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `build.sh`

**Files:**
- Modify: `build.sh`

- [ ] **Step 1: Carry the portable server**

1. Header: replace the two lines

```
#   artifacts/rider/*-rider.zip       Rider plugins, each bundling a server for this machine
#   artifacts/server/<target>/        the bundled single-file server
```

   with

```
#   artifacts/rider/*-rider.zip       Rider plugins
#   Both carry the portable (framework-dependent) server and run it with dotnet (.NET 10) on any OS.
```

2. Delete the `# The .NET runtime identifier and Rider bundle target of this machine.` `case … esac` block, and the `bundle=()` … `fi` block that publishes the single-file server.

3. After `nitrogen=(dotnet "$root/Nitrogen.Cli/bin/Release/net10.0/nitrogen.dll")`, add:

```bash
# The portable server the plugins carry: the Release build of Nitrogen.Cli.
server_build="$root/Nitrogen.Cli/bin/Release/net10.0"

# Copies the portable server as LanguageBundle.CopyServer does: the build folder's files and its runtimes folder.
copy_server() {
    mkdir -p "$2"
    find "$1" -maxdepth 1 -type f -exec cp {} "$2/" \;
    if [[ -d "$1/runtimes" ]]; then cp -R "$1/runtimes" "$2/"; fi
}

# Starts a carried server as an editor would and fails unless it answers initialize and shuts down cleanly.
smoke_server() {
    frame() { printf 'Content-Length: %d\r\n\r\n%s' "${#1}" "$1"; }
    { frame '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}'
      frame '{"jsonrpc":"2.0","id":2,"method":"shutdown"}'
      frame '{"jsonrpc":"2.0","method":"exit"}'; } | dotnet "$1/nitrogen.dll" lsp > /dev/null
    echo "the server in $1 started and shut down"
}
```

4. In the VS Code step, before `(cd "$vscode" && npm ci && npm run compile && npm run package)`:

```bash
copy_server "$server_build" "$vscode/server"
```

   and after `mv "$vscode"/*.vsix "$artifacts/"`:

```bash
smoke_server "$vscode/server"
```

5. In `rider_plugin`, replace `"${nitrogen[@]}" generate rider "$@" ${bundle[@]+"${bundle[@]}"} --output "$out"` with

```bash
    "${nitrogen[@]}" generate rider "$@" --self-contained --server "$server_build" --output "$out"
```

   and after `cp "$out"/build/distributions/*.zip "$artifacts/rider/"` add

```bash
    smoke_server "$out/bundle/server"
```

- [ ] **Step 2: Check `.vscodeignore`**

Confirm `editors/vscode/.vscodeignore` doesn't exclude `server/` (it lists `src/**`, `tsconfig.json`, `**/*.map`, `.gitignore`, `out/test/**`). No change expected.

- [ ] **Step 3: Run the build**

Run: `bash -n build.sh` (syntax), then `./build.sh` (minutes: builds, tests, packages; needs npm, Gradle and JDK 25). Expected: it ends with `==> Done`, after two "the server in … started and shut down" lines (the VS Code extension, the `.ngr` Rider plugin). Check the packages:

```bash
unzip -l artifacts/nitrogen-*.vsix | grep 'extension/server/nitrogen.dll'
unzip -l artifacts/rider/*.zip | grep 'bundle/server/nitrogen.dll'
```

If `./build.sh` can't run locally (no Gradle/JDK or network for the Rider SDK), run at least the VS Code part by hand (the `copy_server`, `npm run package`, `smoke_server` lines) and rely on the Plugins workflow, which runs `build.sh` on this PR.

- [ ] **Step 4: Commit**

```bash
git add build.sh
git commit -m "Carry the portable server in the release plugins, and start each one

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Docs

**Files:**
- Modify: `docs/editor-support.md`, `docs/superpowers/specs/2026-10-09-portable-release-servers-design.md`

- [ ] **Step 1: Editor support**

In `docs/editor-support.md`:

1. As the first paragraph of the section holding the numbered VS Code build steps (the section whose step 1 is `dotnet build Nitrogen.slnx -c Release`), add:

```markdown
The `.vsix` and the Rider ZIP attached to a [GitHub release](https://github.com/TovarishN/Nitrogen/releases) carry a portable Nitrogen server and need only the .NET 10 runtime: install them, and they find `dotnet` through `DOTNET_ROOT`, the standard install locations, and `PATH`. In VS Code, `nitrogen.dotnetPath` names the `dotnet` to use and `nitrogen.server.path` replaces the bundled server; in Rider, Settings | Tools | Nitrogen sets an executable that replaces it. The steps below build the extension from source.
```

2. Step 4 of the VS Code steps: replace its text with `An extension built this way carries no server: in VS Code settings, set nitrogen.server.path to the absolute path of the built CLI executable. For a Release build on macOS or Linux, this is <repo>/Nitrogen.Cli/bin/Release/net10.0/nitrogen (replace <repo> with this repository's absolute path). The extension passes lsp to that executable automatically. If nitrogen is already on PATH, leaving the setting empty works.` (keep the code formatting of the paths and names as in the current text).

3. In `## Rider plugins`, after `The default executable is \`nitrogen\` on \`PATH\`.`, add: ``The release plugin is generated with `--self-contained`: it carries the portable server and runs it with `dotnet`.``

- [ ] **Step 2: Spec status**

Change `Status: approved (2026-10-09).` to `Status: implemented (2026-10-09).`

- [ ] **Step 3: Verify and commit**

Run `python3 eng/check-links.py`, `dotnet build Nitrogen.slnx -warnaserror`, and the whole suite (stashed).

```bash
git add docs/editor-support.md docs/superpowers/specs/2026-10-09-portable-release-servers-design.md
git commit -m "Document the portable servers in the release plugins

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
