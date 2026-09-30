# Installable Language Plugins Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `nitrogen package --config nitrogen.json --output DIR` builds a self-contained `.vsix` and Rider plugin ZIP for a Nitrogen language, each carrying the grammar and a portable server run with `dotnet`.

**Architecture:** `nitrogen lsp --config` pins the server to a bundled `nitrogen.json`. A shared `LanguageBundle` stages config, grammars, helper sources, and the framework-dependent server. A new `generate vscode` renders an extension project around the bundle; `generate rider --self-contained` does the same for Rider. `package` checks tools, generates, runs npm and Gradle, and collects the installables.

**Tech Stack:** .NET 10 / C# (Nitrogen.Cli, Nitrogen.LanguageService), xUnit, TypeScript + vscode-languageclient + esbuild + vsce, Kotlin + IntelliJ Platform Gradle Plugin 2.19 (Rider 2026.2).

**Spec:** [2026-09-30-language-plugin-packaging-design.md](../specs/2026-09-30-language-plugin-packaging-design.md)

---

## Background for the implementer

- Work in the worktree `../Nitrogen-packaging` (branch `language-plugin-packaging`, based on `origin/main` at b56475c). Its files are checked out with LF; keep it that way (`core.autocrlf=false`, `core.eol=lf` are set locally).
- Build and test: `dotnet build Nitrogen.slnx -warnaserror` and `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj`. Focused runs: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~ClassName`.
- `Nitrogen.Cli` exposes internals to `Nitrogen.Tests`. Its commands dispatch in `Nitrogen.Cli/NitrogenCli.cs`; `lsp` is special-cased in `Program.cs` because it owns stdin/stdout.
- `nitrogen.json` languages: `name`, `extensions` (with dots), `start` (`Module.Rule`), `grammars` and optional `sources` (paths or `dir/*.ext` patterns relative to the file), optional `usings`, optional `tokens` (presentation styles). The language service expands patterns in `GrammarLanguages.cs` (`Matching`): `Path.Combine(root, pattern)`, then `Directory.GetFiles(directory, fileName)`.
- The Rider generator (`Nitrogen.Cli/Rider/*`) renders files from C# raw strings; `InPackage` moves Kotlin shared verbatim with `editors/rider` into a per-plugin package. Never change the shared templates (`SettingsKt`, `ConfigurableKt`, `BundlesKt`) without changing `editors/rider` identically; a test compares them.

## File structure

| Path | Responsibility |
| --- | --- |
| `Nitrogen.LanguageService/Lsp/LspServer.cs` | Optional fixed configuration root. |
| `Nitrogen.Cli/LspCommand.cs`, `Nitrogen.Cli/Program.cs` | `lsp --config PATH`. |
| `Nitrogen.Cli/LanguagePluginModel.cs` (new) | Shared language model + `LanguagePluginConfig.Load` (moved from `RiderPluginInput`). |
| `Nitrogen.Cli/LanguageBundle.cs` (new) | Stage config, grammars, sources, server. |
| `Nitrogen.Cli/VsCode/VsCodeInput.cs`, `VsCodeRenderer.cs`, `VsCodeCommand.cs` (new) | `generate vscode`. |
| `Nitrogen.Cli/Rider/*` | `--self-contained`, `--server`, version in Gradle. |
| `Nitrogen.Cli/Package/PackageInput.cs`, `PackageCommand.cs`, `Tools.cs` (new) | `package`. |
| `Nitrogen.Cli/Nitrogen.Cli.csproj` | Embed `editors/vscode` package.json, lock, tsconfig. |
| `Nitrogen.Tests/Cli/*Tests.cs` | Tests per unit. |
| `README.md`, `editors/vscode/README.md`, `editors/rider/README.md` | Docs. |

---

### Task 1: `nitrogen lsp --config`

**Files:**
- Modify: `Nitrogen.LanguageService/Lsp/LspServer.cs`, `Nitrogen.Cli/LspCommand.cs`, `Nitrogen.Cli/Program.cs`
- Test: `Nitrogen.Tests/LanguageService/GrammarLoopTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `GrammarLoopTests` (it already has `_root`, `Write`, `Uri`, `Config`, and `using Nitrogen.LanguageService.Lsp;`):

```csharp
    [Fact]
    public async Task A_fixed_config_root_serves_its_language_and_ignores_the_workspace_one()
    {
        // The bundled language wants "!"; the workspace's own nitrogen.json would accept "hello bob".
        Write("bundle/nitrogen.json", Config);
        Write("bundle/grammars/greet.ngr", WorkspaceTests.Greet.Replace("Name:Word;", "Name:Word \"!\";"));
        Write("workspace/nitrogen.json", Config);
        Write("workspace/grammars/greet.ngr", WorkspaceTests.Greet);
        string sample = Uri(Path.Combine(_root, "workspace", "a.greet"));
        using var service = new NitrogenLanguageService(LspCommand.Registry());

        string[] bodies =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + Uri(Path.Combine(_root, "workspace")) + "\",\"capabilities\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + sample + "\",\"languageId\":\"greet\",\"version\":1,\"text\":\"hello bob\"}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"shutdown\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}",
        ];
        var input = new MemoryStream(bodies.SelectMany(b => JsonRpcConnectionTests.Frame(b)).ToArray());
        var output = new MemoryStream();
        await new LspServer(new JsonRpcConnection(input, output), service, TextWriter.Null, Path.Combine(_root, "bundle"))
            .RunAsync(CancellationToken.None);

        output.Position = 0;
        var counts = new List<int>();
        var reader = new JsonRpcConnection(output, Stream.Null);
        while (await reader.ReadAsync(CancellationToken.None) is { } message)
            using (message)
                if (message.RootElement.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics"
                    && message.RootElement.GetProperty("params").GetProperty("uri").GetString() == sample)
                    counts.Add(message.RootElement.GetProperty("params").GetProperty("diagnostics").GetArrayLength());
        Assert.Equal(1, counts.Last()); // "!" missing: the bundled grammar, not the workspace's
    }

    [Theory]
    [InlineData("missing/nitrogen.json", "no file")]
    [InlineData("bundle/other.json", "nitrogen.json")]
    public void An_unusable_lsp_config_is_rejected(string relative, string expected)
    {
        Write("bundle/other.json", Config);
        Assert.Null(LspCommand.ConfigRoot(Path.Combine(_root, relative), out string error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_lsp_config_names_its_directory()
    {
        Write("bundle/nitrogen.json", Config);
        Assert.Equal(Path.Combine(_root, "bundle"), LspCommand.ConfigRoot(Path.Combine(_root, "bundle", "nitrogen.json"), out _));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~GrammarLoopTests`
Expected: compile errors — `LspServer` has no 4-argument constructor; `LspCommand.ConfigRoot` does not exist.

- [ ] **Step 3: Implement**

`Nitrogen.LanguageService/Lsp/LspServer.cs` — change the primary constructor and the `initialize` case:

```csharp
/// <param name="fixedRoot">The directory whose <c>nitrogen.json</c> configures the languages, whatever root the client sends (<c>nitrogen lsp --config</c>); null to use the client's root.</param>
public sealed class LspServer(JsonRpcConnection connection, NitrogenLanguageService service, TextWriter log, string? fixedRoot = null)
```

```csharp
            case "initialize":
                if (fixedRoot is not null) _root = fixedRoot;
                else if (parameters.ValueKind == JsonValueKind.Object)
                {
                    var initialize = parameters.Deserialize(LspJson.Default.InitializeParams);
                    string? root = initialize?.RootUri ?? initialize?.WorkspaceFolders?.FirstOrDefault()?.Uri;
                    if (root is not null && System.Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.IsFile) _root = uri.LocalPath;
                }
```

`Nitrogen.Cli/LspCommand.cs` — replace `RunAsync` and add `ConfigRoot`:

```csharp
    /// <param name="configRoot">The directory of a <c>--config</c> nitrogen.json; null serves the client's workspace.</param>
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log, CancellationToken cancel, string? configRoot = null)
    {
        using var service = new NitrogenLanguageService(Registry());
        return await new LspServer(new JsonRpcConnection(input, output), service, log, configRoot).RunAsync(cancel);
    }

    /// <summary>The directory of <paramref name="path"/>, which must be an existing file named nitrogen.json; null with an error otherwise.</summary>
    public static string? ConfigRoot(string path, out string error)
    {
        string full = Path.GetFullPath(path);
        error = Path.GetFileName(full) != "nitrogen.json" ? $"--config must name a nitrogen.json file, not '{path}'"
            : !File.Exists(full) ? $"no file '{path}'" : "";
        return error.Length == 0 ? Path.GetDirectoryName(full) : null;
    }
```

`Nitrogen.Cli/Program.cs` — replace the `lsp` branch:

```csharp
if (args is ["lsp"])
    return await LspCommand.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, cancel.Token);
if (args is ["lsp", "--config", var config])
{
    if (LspCommand.ConfigRoot(config, out string error) is not { } root)
    {
        Console.Error.WriteLine($"nitrogen lsp: {error}");
        return 2;
    }
    return await LspCommand.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, cancel.Token, root);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~GrammarLoopTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.LanguageService/Lsp/LspServer.cs Nitrogen.Cli/LspCommand.cs Nitrogen.Cli/Program.cs Nitrogen.Tests/LanguageService/GrammarLoopTests.cs
git commit -m "Serve a fixed nitrogen.json with nitrogen lsp --config"
```

---

### Task 2: Shared language model

**Files:**
- Create: `Nitrogen.Cli/LanguagePluginModel.cs`
- Modify: `Nitrogen.Cli/Rider/RiderPluginModel.cs`, `Nitrogen.Cli/Rider/RiderPluginInput.cs`, every `RiderPluginModel` use in `Nitrogen.Cli` and `Nitrogen.Tests`
- Test: `Nitrogen.Tests/Cli/LanguagePluginConfigTests.cs`

- [ ] **Step 1: Write the failing tests**

`Nitrogen.Tests/Cli/LanguagePluginConfigTests.cs`:

```csharp
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguagePluginConfigTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-config-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Sources_usings_tokens_and_version_are_read()
    {
        string config = Write("nitrogen.json", """
            { "languages": [ { "name": "Catalog", "extensions": [".ncat"], "grammars": ["language/*.ngr"],
              "start": "Catalog.Record", "sources": ["language/Checks.cs"], "usings": ["My.Checks"],
              "tokens": { "capability": "type" }, "version": "1.2.3" } ] }
            """);
        var model = LanguagePluginConfig.Load(config, null, out string error);

        Assert.Equal("", error);
        Assert.Equal(new[] { Path.Combine(_root, "language", "*.ngr") }, model!.GrammarPaths);
        Assert.Equal(new[] { Path.Combine(_root, "language", "Checks.cs") }, model.SourcePaths);
        Assert.Equal(new[] { "My.Checks" }, model.Usings);
        Assert.Equal("""{ "capability": "type" }""", model.TokensJson);
        Assert.Equal("1.2.3", model.Version);
    }

    [Fact]
    public void Optional_fields_default()
    {
        string config = Write("nitrogen.json", """
            { "languages": [ { "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" } ] }
            """);
        var model = LanguagePluginConfig.Load(config, null, out _)!;

        Assert.Empty(model.SourcePaths);
        Assert.Empty(model.Usings);
        Assert.Null(model.TokensJson);
        Assert.Equal("0.1.0", model.Version);
    }

    [Theory]
    [InlineData("\"1.2\"")]
    [InlineData("\"v1.2.3\"")]
    [InlineData("3")]
    public void A_bad_version_is_rejected(string version)
    {
        string config = Write("nitrogen.json", $$"""
            { "languages": [ { "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program", "version": {{version}} } ] }
            """);
        Assert.Null(LanguagePluginConfig.Load(config, null, out string error));
        Assert.Equal("version must be MAJOR.MINOR.PATCH", error);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~LanguagePluginConfigTests`
Expected: compile error — `LanguagePluginConfig` does not exist.

- [ ] **Step 3: Move and extend the model**

Remove the `RiderPluginModel` record from `Nitrogen.Cli/Rider/RiderPluginModel.cs` (keep `RiderPluginRequest` and `RiderBundleInput`), then rename every remaining use:

```bash
grep -rl RiderPluginModel Nitrogen.Cli Nitrogen.Tests --include=*.cs | xargs sed -i '' 's/RiderPluginModel/LanguagePluginModel/g'
```

(On Linux use `sed -i` without `''`.)

Create `Nitrogen.Cli/LanguagePluginModel.cs`, moving `LoadConfig`, `CreateModel`, `GetString`, and `FailModel` out of `RiderPluginInput` and extending them:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nitrogen.Cli;

/// <summary>
/// One language of a nitrogen.json, as the editor plugin generators need it. Grammar and source
/// paths are absolute and may end in a file pattern (<c>dir/*.ngr</c>), as the language service reads them.
/// </summary>
internal sealed record LanguagePluginModel(
    string PluginId,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> GrammarPaths,
    string StartRule)
{
    public IReadOnlyList<string> SourcePaths { get; init; } = [];
    public IReadOnlyList<string> Usings { get; init; } = [];

    /// <summary>The language's <c>tokens</c> object as written, or null.</summary>
    public string? TokensJson { get; init; }

    public string Version { get; init; } = "0.1.0";
}

internal static class LanguagePluginConfig
{
    static readonly Regex s_version = new(@"^\d+\.\d+\.\d+$");

    /// <summary>The language of <paramref name="path"/> named <paramref name="language"/> (or its only one); null with an error otherwise.</summary>
    public static LanguagePluginModel? Load(string path, string? language, out string error)
    {
        error = "";
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return Fail($"no config file '{path}'", out error);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
            if (!document.RootElement.TryGetProperty("languages", out JsonElement languages) || languages.ValueKind != JsonValueKind.Array || languages.GetArrayLength() == 0)
                return Fail("no languages", out error);
            JsonElement? selected = null;
            foreach (JsonElement entry in languages.EnumerateArray())
            {
                string? name = entry.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() : null;
                if (language is null || string.Equals(name, language, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected is not null && language is null) return Fail("multiple languages; specify --language", out error);
                    selected = entry;
                }
            }
            if (selected is null) return Fail($"no language '{language}'", out error);
            JsonElement entryValue = selected.Value;
            string? nameText = GetString(entryValue, "name");
            string? start = GetString(entryValue, "start");
            if (string.IsNullOrWhiteSpace(nameText)) return Fail("language name is required", out error);
            if (string.IsNullOrWhiteSpace(start)) return Fail("language start rule is required", out error);
            if (!entryValue.TryGetProperty("extensions", out JsonElement extensionValues) || extensionValues.ValueKind != JsonValueKind.Array)
                return Fail("language extensions are required", out error);
            var extensions = extensionValues.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (extensions.Any(x => string.IsNullOrWhiteSpace(x) || !x.StartsWith('.')))
                return Fail("extension must start with '.'", out error);
            if (!entryValue.TryGetProperty("grammars", out JsonElement grammarValues) || grammarValues.ValueKind != JsonValueKind.Array || grammarValues.GetArrayLength() == 0)
                return Fail("language grammars are required", out error);
            string version = "0.1.0";
            if (entryValue.TryGetProperty("version", out JsonElement versionValue))
            {
                if (versionValue.ValueKind != JsonValueKind.String || !s_version.IsMatch(versionValue.GetString()!))
                    return Fail("version must be MAJOR.MINOR.PATCH", out error);
                version = versionValue.GetString()!;
            }
            string baseDirectory = Path.GetDirectoryName(fullPath)!;
            string[] Paths(string property) => entryValue.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(x => Path.GetFullPath(Path.Combine(baseDirectory, x.GetString() ?? ""))).ToArray()
                : [];
            var model = Create(nameText, Paths("grammars"), start, extensions, out error);
            return model is null ? null : model with
            {
                SourcePaths = Paths("sources"),
                Usings = entryValue.TryGetProperty("usings", out JsonElement usings) && usings.ValueKind == JsonValueKind.Array
                    ? usings.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
                TokensJson = entryValue.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Object
                    ? tokens.GetRawText() : null,
                Version = version,
            };
        }
        catch (JsonException exception)
        {
            return Fail($"invalid JSON: {exception.Message}", out error);
        }
    }

    public static LanguagePluginModel? Create(string name, IReadOnlyList<string> grammars, string start, IEnumerable<string> extensions, out string error)
    {
        error = "";
        string pluginId = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (pluginId.Length == 0) return Fail("language name is invalid", out error);
        return new LanguagePluginModel(pluginId, name.Trim(), extensions.Select(x => x.ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(), grammars.OrderBy(x => x, StringComparer.Ordinal).ToArray(), start);
    }

    static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static LanguagePluginModel? Fail(string message, out string error)
    {
        error = message;
        return null;
    }
}
```

In `RiderPluginInput.ParseRequest`, replace `LoadConfig(config, language, out error)` with `LanguagePluginConfig.Load(config, language, out error)` and `CreateModel(...)` with `LanguagePluginConfig.Create(...)`; delete the moved methods and `FailModel`.

- [ ] **Step 4: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter "FullyQualifiedName~LanguagePluginConfigTests|FullyQualifiedName~RiderPluginGenerationTests"`
Expected: PASS (the Rider tests are unchanged in behaviour).

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Cli Nitrogen.Tests
git commit -m "Share the plugin language model and read sources, usings, tokens, and version"
```

---

### Task 3: Language bundle

**Files:**
- Create: `Nitrogen.Cli/LanguageBundle.cs`
- Test: `Nitrogen.Tests/Cli/LanguageBundleTests.cs`

- [ ] **Step 1: Write the failing tests**

`Nitrogen.Tests/Cli/LanguageBundleTests.cs`:

```csharp
using System.Text.Json;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class LanguageBundleTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-bundle-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A catalog-like language and a fake framework-dependent server.</summary>
    (LanguagePluginModel Model, string Server) Language(string grammars = "\"language/*.ngr\"")
    {
        Write("project/language/Catalog.ngr", "syntax module Catalog { }");
        Write("project/language/Extra.ngr", "syntax module Extra { }");
        Write("project/language/Checks.cs", "class Checks { }");
        string config = Write("project/nitrogen.json", $$"""
            { "languages": [ { "name": "Catalog", "extensions": [".ncat"], "grammars": [{{grammars}}],
              "start": "Catalog.Record", "sources": ["language/Checks.cs"], "usings": ["My.Checks"],
              "tokens": { "capability": "type" } } ] }
            """);
        Write("server/nitrogen.dll", "dll");
        Write("server/nitrogen.runtimeconfig.json", "{}");
        Write("server/runtimes/any/dep.dll", "dep");
        return (LanguagePluginConfig.Load(config, null, out _)!, Path.Combine(_root, "server"));
    }

    [Fact]
    public void Stage_writes_a_rewritten_config_the_files_and_the_server()
    {
        var (model, server) = Language();
        string bundle = Path.Combine(_root, "bundle");
        LanguageBundle.Stage(model, server, bundle);

        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "language", "nitrogen.json")));
        var entry = config.RootElement.GetProperty("languages")[0];
        Assert.Equal("Catalog", entry.GetProperty("name").GetString());
        Assert.Equal("Catalog.Record", entry.GetProperty("start").GetString());
        Assert.Equal(new[] { "grammars/Catalog.ngr", "grammars/Extra.ngr" }, entry.GetProperty("grammars").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "sources/Checks.cs" }, entry.GetProperty("sources").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "My.Checks" }, entry.GetProperty("usings").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("type", entry.GetProperty("tokens").GetProperty("capability").GetString());
        Assert.Equal("class Checks { }", File.ReadAllText(Path.Combine(bundle, "language", "sources", "Checks.cs")));
        Assert.True(File.Exists(Path.Combine(bundle, "language", "grammars", "Extra.ngr")));
        Assert.Equal("dep", File.ReadAllText(Path.Combine(bundle, "server", "runtimes", "any", "dep.dll")));
    }

    [Fact]
    public void Staging_twice_is_byte_identical()
    {
        var (model, server) = Language();
        LanguageBundle.Stage(model, server, Path.Combine(_root, "a"));
        LanguageBundle.Stage(model, server, Path.Combine(_root, "b"));
        Assert.Equal(Snapshot(Path.Combine(_root, "a")), Snapshot(Path.Combine(_root, "b")));
    }

    [Fact]
    public void A_pattern_that_matches_nothing_is_an_error()
    {
        var (model, server) = Language("\"missing/*.ngr\"");
        var error = Assert.Throws<ArgumentException>(() => LanguageBundle.Stage(model, server, Path.Combine(_root, "bundle")));
        Assert.Contains("no grammar file matches", error.Message);
    }

    [Fact]
    public void Two_files_with_one_name_are_an_error()
    {
        Write("project/other/Catalog.ngr", "syntax module Other { }");
        var (model, server) = Language("\"language/Catalog.ngr\", \"other/Catalog.ngr\"");
        var error = Assert.Throws<ArgumentException>(() => LanguageBundle.Stage(model, server, Path.Combine(_root, "bundle")));
        Assert.Contains("two grammar files named 'Catalog.ngr'", error.Message);
    }

    [Fact]
    public void A_server_without_nitrogen_dll_is_an_error()
    {
        var (model, _) = Language();
        string empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var error = Assert.Throws<ArgumentException>(() => LanguageBundle.Stage(model, empty, Path.Combine(_root, "bundle")));
        Assert.Contains("--server", error.Message);
    }

    static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToBase64String(File.ReadAllBytes(path))));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~LanguageBundleTests`
Expected: compile error — `LanguageBundle` does not exist.

- [ ] **Step 3: Implement**

`Nitrogen.Cli/LanguageBundle.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace Nitrogen.Cli;

/// <summary>
/// A self-contained language for an editor plugin: <c>language/nitrogen.json</c> with its grammars and
/// helper sources, and <c>server/</c>, a framework-dependent Nitrogen build run as <c>dotnet server/nitrogen.dll lsp --config language/nitrogen.json</c>.
/// </summary>
internal static class LanguageBundle
{
    public const string ConfigPath = "language/nitrogen.json";
    public const string ServerPath = "server/nitrogen.dll";

    static readonly UTF8Encoding s_utf8 = new(false);

    /// <summary>The running nitrogen's directory when it is a framework-dependent build; null for a single-file one.</summary>
    public static string? DefaultServer() => IsServer(AppContext.BaseDirectory) ? Path.GetFullPath(AppContext.BaseDirectory) : null;

    public static bool IsServer(string directory) =>
        File.Exists(Path.Combine(directory, "nitrogen.dll")) && File.Exists(Path.Combine(directory, "nitrogen.runtimeconfig.json"));

    /// <summary>Replaces <paramref name="destination"/> with the bundle. Throws <see cref="ArgumentException"/> for unusable input.</summary>
    public static void Stage(LanguagePluginModel model, string serverDirectory, string destination)
    {
        serverDirectory = Path.GetFullPath(serverDirectory);
        destination = Path.GetFullPath(destination);
        if (!IsServer(serverDirectory))
            throw new ArgumentException($"'{serverDirectory}' is not a framework-dependent Nitrogen build (nitrogen.dll and nitrogen.runtimeconfig.json); pass --server");
        if (destination.StartsWith(serverDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("the bundle cannot be written inside the server directory");
        var grammars = Match(model.GrammarPaths, "grammar");
        var sources = Match(model.SourcePaths, "source");

        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        string language = Directory.CreateDirectory(Path.Combine(destination, "language")).FullName;
        Copy(grammars, Path.Combine(language, "grammars"));
        Copy(sources, Path.Combine(language, "sources"));
        File.WriteAllText(Path.Combine(destination, ConfigPath), Config(model, grammars, sources), s_utf8);
        CopyDirectory(serverDirectory, Path.Combine(destination, "server"));
    }

    /// <summary>The files the patterns name, as the language service expands them; every pattern must match and names must be unique.</summary>
    static List<string> Match(IReadOnlyList<string> patterns, string kind)
    {
        var files = new List<string>();
        foreach (string pattern in patterns)
        {
            string directory = Path.GetDirectoryName(pattern)!;
            string[] matched = Directory.Exists(directory) ? Directory.GetFiles(directory, Path.GetFileName(pattern)) : [];
            if (matched.Length == 0) throw new ArgumentException($"no {kind} file matches '{pattern}'");
            files.AddRange(matched.Select(Path.GetFullPath));
        }
        files = files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var duplicate = files.GroupBy(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"two {kind} files named '{duplicate.Key}': {string.Join(", ", duplicate)}");
        return files;
    }

    static void Copy(IReadOnlyList<string> files, string directory)
    {
        if (files.Count == 0) return;
        Directory.CreateDirectory(directory);
        foreach (string file in files) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source).Order(StringComparer.Ordinal))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source).Order(StringComparer.Ordinal))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    static string Config(LanguagePluginModel model, IReadOnlyList<string> grammars, IReadOnlyList<string> sources)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("languages");
            writer.WriteStartObject();
            writer.WriteString("name", model.DisplayName);
            Strings(writer, "extensions", model.Extensions);
            writer.WriteString("start", model.StartRule);
            Strings(writer, "grammars", grammars.Select(f => "grammars/" + Path.GetFileName(f)));
            Strings(writer, "sources", sources.Select(f => "sources/" + Path.GetFileName(f)));
            Strings(writer, "usings", model.Usings);
            if (model.TokensJson is not null)
            {
                writer.WritePropertyName("tokens");
                using var tokens = JsonDocument.Parse(model.TokensJson);
                tokens.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (string value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~LanguageBundleTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Nitrogen.Cli/LanguageBundle.cs Nitrogen.Tests/Cli/LanguageBundleTests.cs
git commit -m "Stage a self-contained language bundle"
```

---

### Task 4: `nitrogen generate vscode`

**Files:**
- Create: `Nitrogen.Cli/VsCode/VsCodeInput.cs`, `Nitrogen.Cli/VsCode/VsCodeRenderer.cs`, `Nitrogen.Cli/VsCode/VsCodeCommand.cs`
- Modify: `Nitrogen.Cli/Nitrogen.Cli.csproj`, `Nitrogen.Cli/NitrogenCli.cs`
- Test: `Nitrogen.Tests/Cli/VsCodeGenerationTests.cs`

- [ ] **Step 1: Write the failing tests**

`Nitrogen.Tests/Cli/VsCodeGenerationTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class VsCodeGenerationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-vscode-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    VsCodeRequest Request()
    {
        Write("project/language/Catalog.ngr", "syntax module Catalog { }");
        string config = Write("project/nitrogen.json", """
            { "languages": [ { "name": "Catalog", "extensions": [".ncat"], "grammars": ["language/Catalog.ngr"], "start": "Catalog.Record", "version": "1.4.0" } ] }
            """);
        Write("server/nitrogen.dll", "dll");
        Write("server/nitrogen.runtimeconfig.json", "{}");
        var request = VsCodeInput.ParseRequest(
            ["generate", "vscode", "--config", config, "--output", Path.Combine(_root, "out"), "--server", Path.Combine(_root, "server")], out string error);
        Assert.Equal("", error);
        return request!;
    }

    string Read(VsCodeRequest request, string path) => File.ReadAllText(Path.Combine(request.OutputDirectory, path));

    [Fact]
    public void Package_json_contributes_the_language_and_its_settings()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        var package = JsonNode.Parse(Read(request, "package.json"))!;

        Assert.Equal("nitrogen-catalog", (string?)package["name"]);
        Assert.Equal("1.4.0", (string?)package["version"]);
        Assert.Equal("nitrogen", (string?)package["publisher"]);
        Assert.Equal("onLanguage:nitrogen-catalog", (string?)package["activationEvents"]![0]);
        var language = package["contributes"]!["languages"]![0]!;
        Assert.Equal("nitrogen-catalog", (string?)language["id"]);
        Assert.Equal(".ncat", (string?)language["extensions"]![0]);
        var properties = package["contributes"]!["configuration"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "nitrogen-catalog.dotnetPath", "nitrogen-catalog.server.path" }, properties.Select(p => p.Key));
        Assert.NotNull(package["dependencies"]!["vscode-languageclient"]);
    }

    [Fact]
    public void Lockfile_is_the_extension_lock_with_this_package_name()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        var lockfile = JsonNode.Parse(Read(request, "package-lock.json"))!;

        Assert.Equal("nitrogen-catalog", (string?)lockfile["name"]);
        Assert.Equal("1.4.0", (string?)lockfile["version"]);
        Assert.Equal("nitrogen-catalog", (string?)lockfile["packages"]![""]!["name"]);
        Assert.Null(lockfile["packages"]![""]!["license"]);
        Assert.NotNull(lockfile["packages"]!["node_modules/vscode-languageclient"]);
    }

    [Fact]
    public void Extension_starts_the_bundled_server_with_its_config()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        string extension = Read(request, "src/extension.ts");

        Assert.Contains("const LANGUAGE = \"nitrogen-catalog\";", extension);
        Assert.Contains("'lsp', '--config', config", extension);
        Assert.Contains("path.join('bundle', 'server', 'nitrogen.dll')", extension);
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "language", "nitrogen.json")));
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "server", "nitrogen.dll")));
    }

    [Fact]
    public void Rendering_twice_is_byte_identical()
    {
        var request = Request();
        VsCodeRenderer.Render(request, CancellationToken.None);
        string first = Snapshot(request.OutputDirectory);
        VsCodeRenderer.Render(request, CancellationToken.None);
        Assert.Equal(first, Snapshot(request.OutputDirectory));
    }

    [Theory]
    [InlineData("--output out", "no --config")]
    [InlineData("--config nitrogen.json", "no --output")]
    [InlineData("--config nitrogen.json --output out --bundle x=y", "unknown option '--bundle'")]
    public void Missing_or_unknown_options_are_reported(string options, string expected)
    {
        Assert.Null(VsCodeInput.ParseRequest(new[] { "generate", "vscode" }.Concat(options.Split(' ')).ToArray(), out string error));
        Assert.Equal(expected, error);
    }

    static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToBase64String(File.ReadAllBytes(path))));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~VsCodeGenerationTests`
Expected: compile error — `VsCodeRequest` does not exist.

- [ ] **Step 3: Embed the extension's package files**

In `Nitrogen.Cli/Nitrogen.Cli.csproj`, add:

```xml
  <!-- `generate vscode` builds on the checked-in extension's dependencies, lock, and TypeScript settings. -->
  <ItemGroup>
    <EmbeddedResource Include="..\editors\vscode\package.json" LogicalName="vscode/package.json" />
    <EmbeddedResource Include="..\editors\vscode\package-lock.json" LogicalName="vscode/package-lock.json" />
    <EmbeddedResource Include="..\editors\vscode\tsconfig.json" LogicalName="vscode/tsconfig.json" />
  </ItemGroup>
```

- [ ] **Step 4: Implement input, renderer, and command**

`Nitrogen.Cli/VsCode/VsCodeInput.cs`:

```csharp
namespace Nitrogen.Cli;

internal sealed record VsCodeRequest(LanguagePluginModel Model, string OutputDirectory, string ServerDirectory);

internal static class VsCodeInput
{
    public const string Usage = "usage: nitrogen generate vscode --config <nitrogen.json> --output <directory> [--language <name>] [--server <directory>]";

    public static VsCodeRequest? ParseRequest(IReadOnlyList<string> args, out string error)
    {
        error = "";
        if (args.Count < 2 || args[0] != "generate" || args[1] != "vscode") return Fail("expected 'generate vscode'", out error);
        string? config = null, language = null, output = null, server = null;
        for (int i = 2; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg is not ("--config" or "-c" or "--language" or "--output" or "-o" or "--server")) return Fail($"unknown option '{arg}'", out error);
            if (++i >= args.Count) return Fail($"{arg} needs a value", out error);
            switch (arg)
            {
                case "--config": case "-c": config = args[i]; break;
                case "--language": language = args[i]; break;
                case "--output": case "-o": output = args[i]; break;
                case "--server": server = args[i]; break;
            }
        }
        if (config is null) return Fail("no --config", out error);
        if (output is null) return Fail("no --output", out error);
        var model = LanguagePluginConfig.Load(config, language, out error);
        if (model is null) return null;
        string? serverDirectory = server is null ? LanguageBundle.DefaultServer() : Path.GetFullPath(server);
        if (serverDirectory is null) return Fail("this nitrogen is a single-file build with no nitrogen.dll; pass --server", out error);
        return new VsCodeRequest(model, Path.GetFullPath(output), serverDirectory);
    }

    static VsCodeRequest? Fail(string message, out string error)
    {
        error = message;
        return null;
    }
}
```

`Nitrogen.Cli/VsCode/VsCodeRenderer.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nitrogen.Cli;

/// <summary>Renders a self-contained VS Code extension project for one language (spec: installable language plugins).</summary>
internal static class VsCodeRenderer
{
    static readonly UTF8Encoding s_utf8 = new(false);

    public static string ExtensionName(LanguagePluginModel model) => "nitrogen-" + model.PluginId;

    public static void Render(VsCodeRequest request, CancellationToken cancel)
    {
        var model = request.Model;
        string output = request.OutputDirectory;
        string parent = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(output, ".."))).FullName;
        string staging = Path.Combine(parent, $".{Path.GetFileName(output)}.nitrogen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["package.json"] = PackageJson(model),
                ["package-lock.json"] = Lockfile(model),
                ["tsconfig.json"] = Resource("vscode/tsconfig.json"),
                ["language-configuration.json"] = LanguageConfiguration,
                [".vscodeignore"] = VscodeIgnore,
                ["README.md"] = Readme(model),
                ["src/extension.ts"] = ExtensionTs(model),
            };
            foreach (var (relative, content) in files)
            {
                cancel.ThrowIfCancellationRequested();
                string path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content.Replace("\r\n", "\n"), s_utf8);
            }
            LanguageBundle.Stage(model, request.ServerDirectory, Path.Combine(staging, "bundle"));

            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.Move(staging, output);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    static string Resource(string name)
    {
        using var stream = typeof(VsCodeRenderer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded resource '{name}'");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static string Json(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\n" }) + "\n";

    static string PackageJson(LanguagePluginModel model)
    {
        var template = JsonNode.Parse(Resource("vscode/package.json"))!;
        string name = ExtensionName(model);
        var extensions = new JsonArray(model.Extensions.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        return Json(new JsonObject
        {
            ["name"] = name,
            ["displayName"] = model.DisplayName,
            ["description"] = $"{model.DisplayName} language support through nitrogen lsp.",
            ["version"] = model.Version,
            ["publisher"] = "nitrogen",
            ["engines"] = template["engines"]!.DeepClone(),
            ["main"] = "./out/extension.js",
            ["activationEvents"] = new JsonArray("onLanguage:" + name),
            ["contributes"] = new JsonObject
            {
                ["languages"] = new JsonArray(new JsonObject
                {
                    ["id"] = name,
                    ["aliases"] = new JsonArray(model.DisplayName),
                    ["extensions"] = extensions,
                    ["configuration"] = "./language-configuration.json",
                }),
                ["configuration"] = new JsonObject
                {
                    ["title"] = model.DisplayName,
                    ["properties"] = new JsonObject
                    {
                        [name + ".dotnetPath"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["default"] = "",
                            ["description"] = "The dotnet executable that runs the bundled server; empty finds it through DOTNET_ROOT, the standard install locations, and PATH.",
                        },
                        [name + ".server.path"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["default"] = "",
                            ["description"] = "A nitrogen executable to run instead of the bundled server (for development).",
                        },
                    },
                },
            },
            ["scripts"] = new JsonObject
            {
                ["compile"] = template["scripts"]!["compile"]!.DeepClone(),
                ["package"] = "vsce package --no-dependencies --skip-license --allow-missing-repository",
            },
            ["dependencies"] = template["dependencies"]!.DeepClone(),
            ["devDependencies"] = template["devDependencies"]!.DeepClone(),
        });
    }

    /// <summary>The extension's lock, renamed: the dependency tree is the same, so npm ci stays reproducible.</summary>
    static string Lockfile(LanguagePluginModel model)
    {
        var lockfile = JsonNode.Parse(Resource("vscode/package-lock.json"))!.AsObject();
        string name = ExtensionName(model);
        lockfile["name"] = name;
        lockfile["version"] = model.Version;
        var root = lockfile["packages"]![""]!.AsObject();
        root["name"] = name;
        root["version"] = model.Version;
        root.Remove("license");
        return Json(lockfile);
    }

    const string LanguageConfiguration = """
{
  "brackets": [["{", "}"], ["[", "]"], ["(", ")"]],
  "autoClosingPairs": [["{", "}"], ["[", "]"], ["(", ")"], { "open": "\"", "close": "\"" }],
  "surroundingPairs": [["{", "}"], ["[", "]"], ["(", ")"], ["\"", "\""]]
}

""";

    const string VscodeIgnore = """
src/**
node_modules/**
tsconfig.json
**/*.map

""";

    static string Readme(LanguagePluginModel model) => $$"""
# {{model.DisplayName}}

{{model.DisplayName}} support for `{{string.Join("`, `", model.Extensions)}}` files: diagnostics, go to definition, references, rename, and semantic colouring, served by the bundled Nitrogen language server.

The server runs on the .NET 10 runtime. If `dotnet` is not found through `DOTNET_ROOT`, the standard install locations, or `PATH`, set `{{ExtensionName(model)}}.dotnetPath`.

Generated by `nitrogen generate vscode`.

""";

    static string ExtensionTs(LanguagePluginModel model) => $$"""
import * as fs from 'fs';
import * as path from 'path';
import { ExtensionContext, window, workspace } from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';

const LANGUAGE = {{JsonSerializer.Serialize(ExtensionName(model))}};
const DISPLAY = {{JsonSerializer.Serialize(model.DisplayName)}};

let client: LanguageClient | undefined;

/** The dotnet host: the setting, then DOTNET_ROOT, then the standard install locations, then PATH. */
function dotnet(): string {
  const configured = workspace.getConfiguration(LANGUAGE).get<string>('dotnetPath');
  if (configured) return configured;
  const exe = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const candidates = [
    process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, exe) : undefined,
    process.platform === 'win32' ? path.join(process.env.ProgramFiles ?? 'C:\\Program Files', 'dotnet', exe) : undefined,
    process.platform === 'darwin' ? '/usr/local/share/dotnet/dotnet' : undefined,
    process.platform === 'linux' ? '/usr/share/dotnet/dotnet' : undefined,
    process.platform === 'linux' ? '/usr/lib/dotnet/dotnet' : undefined,
  ];
  return candidates.find((candidate): candidate is string => candidate !== undefined && fs.existsSync(candidate)) ?? exe;
}

export async function activate(context: ExtensionContext): Promise<void> {
  const config = context.asAbsolutePath(path.join('bundle', 'language', 'nitrogen.json'));
  const server = workspace.getConfiguration(LANGUAGE).get<string>('server.path');
  const serverOptions: ServerOptions = server
    ? { command: server, args: ['lsp', '--config', config] }
    : { command: dotnet(), args: [context.asAbsolutePath(path.join('bundle', 'server', 'nitrogen.dll')), 'lsp', '--config', config] };
  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: 'file', language: LANGUAGE }, { scheme: 'untitled', language: LANGUAGE }],
  };
  client = new LanguageClient(LANGUAGE, DISPLAY, serverOptions, clientOptions);
  try {
    await client.start();
  } catch (error) {
    client = undefined;
    void window.showErrorMessage(
      `${DISPLAY}: the language server did not start (${error}). It needs the .NET 10 runtime; set "${LANGUAGE}.dotnetPath" if dotnet is not found.`);
  }
}

export function deactivate(): Thenable<void> | undefined {
  return client?.stop();
}

""";
}
```

`Nitrogen.Cli/VsCode/VsCodeCommand.cs`:

```csharp
namespace Nitrogen.Cli;

internal static class VsCodeCommand
{
    public static Task<int> RunAsync(VsCodeRequest request, TextWriter output, CancellationToken cancel)
    {
        try
        {
            VsCodeRenderer.Render(request, cancel);
            output.WriteLine($"generated VS Code extension '{VsCodeRenderer.ExtensionName(request.Model)}' at {request.OutputDirectory}");
            return Task.FromResult(0);
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("error: VS Code extension generation cancelled");
            return Task.FromResult(1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            output.WriteLine($"error: VS Code extension generation failed: {exception.Message}");
            return Task.FromResult(1);
        }
    }
}
```

In `NitrogenCli.RunAsync`, after the `generate rider` branch:

```csharp
        if (args.Count >= 2 && args[0] == "generate" && args[1] == "vscode")
        {
            var request = VsCodeInput.ParseRequest(args, out string generationError);
            if (request is null)
            {
                output.WriteLine($"error: {generationError}");
                output.WriteLine(VsCodeInput.Usage);
                return 2;
            }
            return await VsCodeCommand.RunAsync(request, output, cancel);
        }
```

- [ ] **Step 5: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~VsCodeGenerationTests`
Expected: PASS. If `JsonSerializerOptions.NewLine` is unavailable in this SDK, write through `Utf8JsonWriter` with `JsonWriterOptions { Indented = true, NewLine = "\n" }` as `LanguageBundle.Config` does.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Cli Nitrogen.Tests/Cli/VsCodeGenerationTests.cs
git commit -m "Generate a self-contained VS Code extension for a language"
```

---

### Task 5: Self-contained Rider plugins

**Files:**
- Modify: `Nitrogen.Cli/Rider/RiderPluginModel.cs`, `Nitrogen.Cli/Rider/RiderPluginInput.cs`, `Nitrogen.Cli/Rider/RiderPluginRenderer.cs`, `Nitrogen.Cli/NitrogenCli.cs` (usage line)
- Test: `Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `RiderPluginGenerationTests`:

```csharp
    static RiderPluginRequest SelfContained(TempDirectory dir)
    {
        dir.Write("project/a.ngr", "syntax module Calc { }");
        string config = dir.Write("project/nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program", "version": "2.0.1" }] }
            """);
        dir.Write("server/nitrogen.dll", "dll");
        dir.Write("server/nitrogen.runtimeconfig.json", "{}");
        var request = RiderPluginInput.ParseRequest(new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out"),
            "--self-contained", "--server", Path.Combine(dir.Path, "server") }, out string error);
        Assert.Equal("", error);
        return request!;
    }

    [Fact]
    public void Self_contained_plugin_ships_the_bundle_and_starts_it_with_dotnet()
    {
        using var dir = new TempDirectory();
        var request = SelfContained(dir);
        RiderPluginRenderer.Render(request, request.OutputDirectory, CancellationToken.None);
        string Read(string path) => File.ReadAllText(Path.Combine(request.OutputDirectory, path));

        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "language", "nitrogen.json")));
        Assert.True(File.Exists(Path.Combine(request.OutputDirectory, "bundle", "server", "nitrogen.dll")));
        Assert.Contains("tasks.prepareSandbox", Read("build.gradle.kts"));
        Assert.Contains("version = \"2.0.1\"", Read("build.gradle.kts"));
        string lsp = Read("src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt");
        Assert.Contains("\"lsp\", \"--config\", config", lsp);
        Assert.Contains("NitrogenLanguageBundle.dotnet()", lsp);
        Assert.Contains("PluginId.getId(\"org.nitrogen.rider.calc\")", Read("src/main/kotlin/org/nitrogen/rider/NitrogenLanguageBundle.kt"));
    }

    [Fact]
    public void Plugins_without_self_contained_are_unchanged_apart_from_the_version()
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var request = RiderPluginInput.ParseRequest(new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }, out _)!;
        RiderPluginRenderer.Render(request, request.OutputDirectory, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(request.OutputDirectory, "bundle")));
        Assert.False(File.Exists(Path.Combine(request.OutputDirectory, "src/main/kotlin/org/nitrogen/rider/NitrogenLanguageBundle.kt")));
        Assert.DoesNotContain("prepareSandbox", File.ReadAllText(Path.Combine(request.OutputDirectory, "build.gradle.kts")));
        Assert.Contains("version = \"0.1.0\"", File.ReadAllText(Path.Combine(request.OutputDirectory, "build.gradle.kts")));
    }

    [Theory]
    [InlineData("--self-contained --bundle macos-x64=nitrogen.json", "use either --self-contained or --bundle")]
    [InlineData("--server server", "--server needs --self-contained")]
    public void Self_contained_option_conflicts_are_reported(string options, string expected)
    {
        using var dir = new TempDirectory();
        string config = dir.Write("nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        var args = new[] { "generate", "rider", "--config", config, "--output", Path.Combine(dir.Path, "out") }
            .Concat(options.Replace("nitrogen.json", config).Split(' ')).ToArray();
        Assert.Null(RiderPluginInput.ParseRequest(args, out string error));
        Assert.Equal(expected, error);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~RiderPluginGenerationTests`
Expected: the three new tests fail (`unknown option '--self-contained'`, missing files, no `version` line).

- [ ] **Step 3: Implement request and input**

`Nitrogen.Cli/Rider/RiderPluginModel.cs` — add the server directory to the request:

```csharp
/// <param name="SelfContainedServer">The framework-dependent server to bundle with the language (<c>--self-contained</c>); null for a plugin that runs an installed nitrogen.</param>
internal sealed record RiderPluginRequest(
    LanguagePluginModel Model,
    string OutputDirectory,
    string NitrogenPath,
    IReadOnlyList<RiderBundleInput> Bundles,
    string? SelfContainedServer = null);
```

`Nitrogen.Cli/Rider/RiderPluginInput.cs` — in `ParseRequest`, declare `bool selfContained = false; string? server = null;`, add `--server` to the value options (`case "--server": server = value; break;`), add a flag branch before the `--bundle` branch:

```csharp
            else if (arg is "--self-contained") selfContained = true;
```

and after the `no --output` check:

```csharp
        if (selfContained && bundles.Count > 0) return FailRequest("use either --self-contained or --bundle", out error);
        if (server is not null && !selfContained) return FailRequest("--server needs --self-contained", out error);
        string? selfContainedServer = null;
        if (selfContained)
        {
            if (config is null) return FailRequest("--self-contained needs --config", out error);
            selfContainedServer = server is null ? LanguageBundle.DefaultServer() : Path.GetFullPath(server);
            if (selfContainedServer is null) return FailRequest("this nitrogen is a single-file build with no nitrogen.dll; pass --server", out error);
        }
```

and construct the request with `new RiderPluginRequest(model, Path.GetFullPath(output), nitrogen, bundles, selfContainedServer)`.

Update the usage line in `NitrogenCli` to:

```csharp
                output.WriteLine("usage: nitrogen generate rider --config <nitrogen.json> --output <directory> [--language <name>] [--self-contained [--server <directory>] | --bundle <target>=<path>]");
```

- [ ] **Step 4: Implement rendering**

In `RiderPluginRenderer.Render`:
- replace `["build.gradle.kts"] = BuildGradle,` with `["build.gradle.kts"] = BuildGradle(request),`;
- replace `["src/main/kotlin/org/nitrogen/rider/NitrogenLspSupport.kt"] = LspKt(request),` with the same key mapped to `request.SelfContainedServer is null ? LspKt(request) : SelfContainedLspKt(request)`;
- after the dictionary initializer, add `if (request.SelfContainedServer is not null) files["src/main/kotlin/org/nitrogen/rider/NitrogenLanguageBundle.kt"] = LanguageBundleKt(request.Model);`
- after the bundles loop and before replacing the output, add `if (request.SelfContainedServer is { } server) LanguageBundle.Stage(request.Model, server, Path.Combine(staging, "bundle"));`

Replace the `BuildGradle` constant with:

```csharp
    static string BuildGradle(RiderPluginRequest request) => $$"""
plugins {
    id("java")
    kotlin("jvm") version "2.4.0"
    id("org.jetbrains.intellij.platform") version "2.19.0"
}

version = "{{request.Model.Version}}"

repositories {
    mavenCentral()
    intellijPlatform { defaultRepositories() }
}

dependencies {
    // The Rider installer is not supported as a target; use the Maven distribution.
    intellijPlatform { rider("2026.2") { useInstaller = false } }
}

intellijPlatform { pluginConfiguration { ideaVersion { sinceBuild = "262" } } }
""" + (request.SelfContainedServer is null ? "" : """

tasks.prepareSandbox {
    // The language bundle (grammar, helper sources, portable server) sits beside lib/ in the installed plugin.
    from(layout.projectDirectory.dir("bundle")) { into(pluginName.map { "$it/bundle" }) }
}
""");
```

Add the self-contained launcher and bundle locator:

```csharp
    static string SelfContainedLspKt(RiderPluginRequest request) => $$"""
package {{KotlinPackage(request.Model)}}

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.LspIntegrationProvider.LspClientStarter
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor

class NitrogenLspSupport : LspIntegrationProvider {
    companion object {
        const val defaultExecutable = "{{EscapeKotlin(request.NitrogenPath)}}"
        val extensions = setOf({{string.Join(", ", request.Model.Extensions.Select(x => "\"" + EscapeKotlin(x.TrimStart('.')) + "\""))}})
    }

    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspClientStarter) {
        if (file.extension in extensions) clientStarter.ensureClientStarted(NitrogenClientDescriptor(project))
    }

    private class NitrogenClientDescriptor(project: Project) : ProjectWideLspClientDescriptor(project, "{{EscapeKotlin(request.Model.DisplayName)}}") {
        override fun isSupportedFile(file: VirtualFile): Boolean = file.extension in extensions

        /** The executable set in Settings when there is one, else the bundled server on the dotnet host; both get the bundled config. */
        override fun createCommandLine(): GeneralCommandLine {
            val bundle = NitrogenLanguageBundle.directory()
            val config = bundle.resolve("language/nitrogen.json").toString()
            val settings = NitrogenSettings.getInstance()
            return if (settings.executable.isNotBlank())
                GeneralCommandLine(settings.resolveExecutable(defaultExecutable), "lsp", "--config", config)
            else
                GeneralCommandLine(NitrogenLanguageBundle.dotnet(), bundle.resolve("server/nitrogen.dll").toString(), "lsp", "--config", config)
        }
    }
}
""";

    static string LanguageBundleKt(LanguagePluginModel model) => $$"""
package {{KotlinPackage(model)}}

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import com.intellij.openapi.util.SystemInfo
import java.io.File
import java.nio.file.Files
import java.nio.file.Path

/** The language bundle installed beside the plugin's lib/, and the dotnet host that runs its server. */
object NitrogenLanguageBundle {
    fun directory(): Path {
        val plugin = PluginManagerCore.getPlugin(PluginId.getId("org.nitrogen.rider.{{model.PluginId}}"))
            ?: throw ExecutionException("The {{EscapeKotlin(model.DisplayName)}} plugin is not installed.")
        val bundle = plugin.pluginPath.resolve("bundle")
        if (!Files.isRegularFile(bundle.resolve("server/nitrogen.dll")))
            throw ExecutionException("The {{EscapeKotlin(model.DisplayName)}} plugin's language bundle is missing; reinstall the plugin.")
        return bundle
    }

    /** dotnet: DOTNET_ROOT, then the standard install locations, then PATH. */
    fun dotnet(): String {
        val exe = if (SystemInfo.isWindows) "dotnet.exe" else "dotnet"
        val candidates = listOfNotNull(
            System.getenv("DOTNET_ROOT")?.let { File(it, exe) },
            if (SystemInfo.isWindows) File(System.getenv("ProgramFiles") ?: "C:\\Program Files", "dotnet\\" + exe) else null,
            if (SystemInfo.isMac) File("/usr/local/share/dotnet/dotnet") else null,
            if (SystemInfo.isLinux) File("/usr/share/dotnet/dotnet") else null,
            if (SystemInfo.isLinux) File("/usr/lib/dotnet/dotnet") else null)
        candidates.firstOrNull { it.canExecute() }?.let { return it.absolutePath }
        PathEnvironmentVariableUtil.findInPath(exe)?.let { return it.absolutePath }
        throw ExecutionException(
            "{{EscapeKotlin(model.DisplayName)}} needs the .NET 10 runtime: install it, set DOTNET_ROOT, or set a Nitrogen executable in Settings | Tools | {{EscapeKotlin(model.DisplayName)}}.")
    }
}
""";
```

In `Readme`, add a paragraph when self-contained: `This plugin carries its language and a portable server in bundle/, run with dotnet (.NET 10); a Nitrogen executable set in Settings replaces the bundled server.` (append `+ (request.SelfContainedServer is null ? "" : "…\n")`).

- [ ] **Step 5: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~RiderPluginGenerationTests`
Expected: PASS, including the existing template-equality tests.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Cli Nitrogen.Tests/Cli/RiderPluginGenerationTests.cs
git commit -m "Generate self-contained Rider plugins that run the bundled server with dotnet"
```

---

### Task 6: `nitrogen package`

**Files:**
- Create: `Nitrogen.Cli/Package/PackageInput.cs`, `Nitrogen.Cli/Package/Tools.cs`, `Nitrogen.Cli/Package/PackageCommand.cs`
- Modify: `Nitrogen.Cli/NitrogenCli.cs`
- Test: `Nitrogen.Tests/Cli/PackageTests.cs`

- [ ] **Step 1: Write the failing tests**

`Nitrogen.Tests/Cli/PackageTests.cs`:

```csharp
using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

public sealed class PackageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("nitrogen-package-").FullName;

    string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    PackageRequest Request(params string[] targets)
    {
        Write("project/a.ngr", "syntax module Calc { }");
        string config = Write("project/nitrogen.json", """
            { "languages": [{ "name": "Calc", "extensions": [".calc"], "grammars": ["a.ngr"], "start": "Calc.Program" }] }
            """);
        Write("server/nitrogen.dll", "dll");
        Write("server/nitrogen.runtimeconfig.json", "{}");
        var request = PackageInput.ParseRequest(new[] { "package", "--config", config, "--output", Path.Combine(_root, "dist"),
            "--server", Path.Combine(_root, "server") }.Concat(targets).ToArray(), out string error);
        Assert.Equal("", error);
        return request!;
    }

    [Fact]
    public void Both_targets_by_default_or_the_named_ones()
    {
        Assert.Equal((true, true), (Request().VsCode, Request().Rider));
        Assert.Equal((true, false), (Request("--vscode").VsCode, Request("--vscode").Rider));
        Assert.Equal((false, true), (Request("--rider").VsCode, Request("--rider").Rider));
    }

    [Theory]
    [InlineData("vscode", "npm")]
    [InlineData("rider", "gradle")]
    public async Task A_missing_tool_is_reported_before_building(string target, string tool)
    {
        var request = Request("--" + target);
        var output = new StringWriter();
        var tools = new Tools(find: name => name == tool ? null : "/bin/" + name, javaHome: () => null);

        int code = await PackageCommand.RunAsync(request, output, CancellationToken.None, tools);

        Assert.Equal(2, code);
        Assert.Contains($"'{tool}'", output.ToString());
        Assert.False(Directory.Exists(Path.Combine(_root, "dist")));
    }

    [Fact]
    public async Task Rider_needs_a_java_runtime()
    {
        var output = new StringWriter();
        var tools = new Tools(find: name => name == "java" ? null : "/bin/" + name, javaHome: () => null);

        Assert.Equal(2, await PackageCommand.RunAsync(Request("--rider"), output, CancellationToken.None, tools));
        Assert.Contains("Java", output.ToString());
    }

    [Theory]
    [InlineData("--output out", "no --config")]
    [InlineData("--config nitrogen.json", "no --output")]
    [InlineData("--config nitrogen.json --output out --self-contained", "unknown option '--self-contained'")]
    public void Missing_or_unknown_options_are_reported(string options, string expected)
    {
        Assert.Null(PackageInput.ParseRequest(new[] { "package" }.Concat(options.Split(' ')).ToArray(), out string error));
        Assert.Equal(expected, error);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~PackageTests`
Expected: compile error — `PackageRequest` does not exist.

- [ ] **Step 3: Implement**

`Nitrogen.Cli/Package/PackageInput.cs`:

```csharp
namespace Nitrogen.Cli;

internal sealed record PackageRequest(LanguagePluginModel Model, string OutputDirectory, string ServerDirectory, bool VsCode, bool Rider);

internal static class PackageInput
{
    public const string Usage = "usage: nitrogen package --config <nitrogen.json> --output <directory> [--language <name>] [--vscode] [--rider] [--server <directory>]";

    public static PackageRequest? ParseRequest(IReadOnlyList<string> args, out string error)
    {
        error = "";
        if (args.Count < 1 || args[0] != "package") return Fail("expected 'package'", out error);
        string? config = null, language = null, output = null, server = null;
        bool vscode = false, rider = false;
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--vscode") { vscode = true; continue; }
            if (arg == "--rider") { rider = true; continue; }
            if (arg is not ("--config" or "-c" or "--language" or "--output" or "-o" or "--server")) return Fail($"unknown option '{arg}'", out error);
            if (++i >= args.Count) return Fail($"{arg} needs a value", out error);
            switch (arg)
            {
                case "--config": case "-c": config = args[i]; break;
                case "--language": language = args[i]; break;
                case "--output": case "-o": output = args[i]; break;
                case "--server": server = args[i]; break;
            }
        }
        if (config is null) return Fail("no --config", out error);
        if (output is null) return Fail("no --output", out error);
        var model = LanguagePluginConfig.Load(config, language, out error);
        if (model is null) return null;
        string? serverDirectory = server is null ? LanguageBundle.DefaultServer() : Path.GetFullPath(server);
        if (serverDirectory is null) return Fail("this nitrogen is a single-file build with no nitrogen.dll; pass --server", out error);
        if (!vscode && !rider) vscode = rider = true;
        return new PackageRequest(model, Path.GetFullPath(output), serverDirectory, vscode, rider);
    }

    static PackageRequest? Fail(string message, out string error)
    {
        error = message;
        return null;
    }
}
```

`Nitrogen.Cli/Package/Tools.cs`:

```csharp
using System.Diagnostics;

namespace Nitrogen.Cli;

/// <summary>Finds and runs the build tools <c>nitrogen package</c> needs; tests replace the lookup.</summary>
internal sealed class Tools(Func<string, string?>? find = null, Func<string?>? javaHome = null)
{
    readonly Func<string, string?> _find = find ?? FindOnPath;
    readonly Func<string?> _javaHome = javaHome ?? (() => Environment.GetEnvironmentVariable("JAVA_HOME"));

    public string? Find(string name) => _find(name);

    /// <summary>JAVA_HOME's java, or java on PATH; null when neither exists.</summary>
    public string? Java()
    {
        if (_javaHome() is { } home)
        {
            string java = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(java)) return java;
        }
        return _find("java");
    }

    static string? FindOnPath(string name)
    {
        string[] suffixes = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (string suffix in suffixes)
            {
                string candidate = Path.Combine(directory, name + suffix);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Runs <paramref name="file"/> in <paramref name="directory"/>, copying its output; its exit code.</summary>
    public async Task<int> RunAsync(string file, IReadOnlyList<string> args, string directory, TextWriter output, CancellationToken cancel)
    {
        output.WriteLine($"> {Path.GetFileName(file)} {string.Join(' ', args)}");
        var start = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"could not start '{file}'");
        var lines = new object();
        void Copy(string? line) { if (line is not null) lock (lines) output.WriteLine(line); }
        process.OutputDataReceived += (_, e) => Copy(e.Data);
        process.ErrorDataReceived += (_, e) => Copy(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancel);
        return process.ExitCode;
    }
}
```

`Nitrogen.Cli/Package/PackageCommand.cs`:

```csharp
namespace Nitrogen.Cli;

/// <summary><c>nitrogen package</c>: generate, build, and collect installable editor plugins (spec: installable language plugins).</summary>
internal static class PackageCommand
{
    /// <returns>0 built, 1 a build step failed, 2 a required tool is missing.</returns>
    public static async Task<int> RunAsync(PackageRequest request, TextWriter output, CancellationToken cancel, Tools? tools = null)
    {
        tools ??= new Tools();
        string? npm = request.VsCode ? tools.Find("npm") : null;
        string? gradle = request.Rider ? tools.Find("gradle") : null;
        if (request.VsCode && npm is null) return Missing(output, "'npm' (Node.js) is needed to build the VS Code extension");
        if (request.Rider && gradle is null) return Missing(output, "'gradle' is needed to build the Rider plugin");
        if (request.Rider && tools.Java() is null) return Missing(output, "a Java runtime (JAVA_HOME or java on PATH, JDK 25 for Rider 2026.2) is needed to build the Rider plugin");

        var model = request.Model;
        string build = Path.Combine(request.OutputDirectory, ".build");
        try
        {
            if (request.VsCode)
            {
                string project = Path.Combine(build, "vscode");
                VsCodeRenderer.Render(new VsCodeRequest(model, project, request.ServerDirectory), cancel);
                string vsix = Path.Combine(request.OutputDirectory, $"{model.PluginId}-{model.Version}.vsix");
                if (await tools.RunAsync(npm!, ["ci"], project, output, cancel) is not 0 and var ci) return Failed(output, "npm ci", ci);
                if (await tools.RunAsync(npm!, ["run", "compile"], project, output, cancel) is not 0 and var compile) return Failed(output, "npm run compile", compile);
                if (await tools.RunAsync(npm!, ["run", "package", "--", "--out", vsix], project, output, cancel) is not 0 and var pack) return Failed(output, "vsce package", pack);
                output.WriteLine($"built {vsix}");
            }
            if (request.Rider)
            {
                string project = Path.Combine(build, "rider");
                RiderPluginRenderer.Render(new RiderPluginRequest(model, project, "nitrogen", [], request.ServerDirectory), project, cancel);
                if (await tools.RunAsync(gradle!, ["buildPlugin", "--console=plain"], project, output, cancel) is not 0 and var code) return Failed(output, "gradle buildPlugin", code);
                string built = Directory.GetFiles(Path.Combine(project, "build", "distributions"), "*.zip").Single();
                string zip = Path.Combine(request.OutputDirectory, $"{model.PluginId}-{model.Version}-rider.zip");
                File.Copy(built, zip, overwrite: true);
                output.WriteLine($"built {zip}");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            output.WriteLine($"error: packaging failed: {exception.Message}");
            return 1;
        }
    }

    static int Missing(TextWriter output, string message)
    {
        output.WriteLine($"error: {message}");
        return 2;
    }

    static int Failed(TextWriter output, string step, int code)
    {
        output.WriteLine($"error: {step} exited with {code}");
        return 1;
    }
}
```

In `NitrogenCli.RunAsync`, after the `generate vscode` branch:

```csharp
        if (args.Count >= 1 && args[0] == "package")
        {
            var request = PackageInput.ParseRequest(args, out string packageError);
            if (request is null)
            {
                output.WriteLine($"error: {packageError}");
                output.WriteLine(PackageInput.Usage);
                return 2;
            }
            return await PackageCommand.RunAsync(request, output, cancel);
        }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --filter FullyQualifiedName~PackageTests`
Expected: PASS.

- [ ] **Step 5: Full gate**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`
Expected: 0 warnings; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add Nitrogen.Cli Nitrogen.Tests/Cli/PackageTests.cs
git commit -m "Add nitrogen package to build installable VS Code and Rider plugins"
```

---

### Task 7: End-to-end check with the catalog language, and docs

**Files:**
- Modify: `README.md`, `editors/vscode/README.md`, `editors/rider/README.md`

- [ ] **Step 1: Package the catalog language**

```bash
dotnet build Nitrogen.Cli/Nitrogen.Cli.csproj -c Release
SCRATCH=$(mktemp -d)
JAVA_HOME=/opt/homebrew/opt/openjdk@25/libexec/openjdk.jdk/Contents/Home \
  dotnet Nitrogen.Cli/bin/Release/net10.0/nitrogen.dll package \
  --config ../Nitrogen.Concepts/nitrogen.json --output "$SCRATCH/dist"
ls "$SCRATCH/dist"
```

Expected: `catalog-0.1.0.vsix` and `catalog-0.1.0-rider.zip`. If Gradle rejects the `prepareSandbox` block, check the IntelliJ Platform Gradle Plugin 2.x `PrepareSandboxTask` API (`pluginName` property, `from(...) { into(...) }`), fix the template and its test, and rerun.

- [ ] **Step 2: Inspect the packages**

```bash
unzip -l "$SCRATCH/dist/catalog-0.1.0.vsix" | grep -E "extension/(package.json|out/extension.js|bundle/language/nitrogen.json|bundle/server/nitrogen.dll)"
unzip -l "$SCRATCH/dist/catalog-0.1.0-rider.zip" | grep -E "catalog-rider/(lib/|bundle/language/nitrogen.json|bundle/server/nitrogen.dll)"
```

Expected: every listed path is present in each package.

- [ ] **Step 3: Run each plugin's server command from a folder without nitrogen.json**

Extract both packages, write a probe that speaks LSP to a command from an empty folder, and run it with each plugin's exact command:

```bash
unzip -q "$SCRATCH/dist/catalog-0.1.0.vsix" -d "$SCRATCH/vsix"
unzip -q "$SCRATCH/dist/catalog-0.1.0-rider.zip" -d "$SCRATCH/rider"
mkdir -p "$SCRATCH/empty"
cat > "$SCRATCH/probe.py" <<'PY'
import json, subprocess, sys, threading, time
empty = sys.argv[1]
p = subprocess.Popen(sys.argv[2:], cwd=empty, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
def send(msg):
    body = json.dumps(msg).encode()
    p.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body) + body); p.stdin.flush()
messages = []
def read():
    while True:
        line = p.stdout.readline()
        if not line: return
        if line.lower().startswith(b"content-length"):
            n = int(line.split(b":")[1]); p.stdout.readline(); messages.append(json.loads(p.stdout.read(n)))
threading.Thread(target=read, daemon=True).start()
uri = "file://" + empty + "/x.ncat"
text = 'concept A.B candidate\n{\n  definition "d";\n  provides Missing.Capability;\n}\n'
send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"rootUri": "file://" + empty, "capabilities": {}}})
time.sleep(2); send({"jsonrpc": "2.0", "method": "initialized", "params": {}})
time.sleep(5); send({"jsonrpc": "2.0", "method": "textDocument/didOpen", "params": {"textDocument": {"uri": uri, "languageId": "ncat", "version": 1, "text": text}}})
time.sleep(4)
print([d.get("code") for m in messages if m.get("method") == "textDocument/publishDiagnostics" and m["params"]["uri"] == uri for d in m["params"]["diagnostics"]])
p.kill()
PY
python3 "$SCRATCH/probe.py" "$SCRATCH/empty" dotnet "$SCRATCH/vsix/extension/bundle/server/nitrogen.dll" lsp --config "$SCRATCH/vsix/extension/bundle/language/nitrogen.json"
python3 "$SCRATCH/probe.py" "$SCRATCH/empty" dotnet "$SCRATCH/rider/catalog-rider/bundle/server/nitrogen.dll" lsp --config "$SCRATCH/rider/catalog-rider/bundle/language/nitrogen.json"
```

Expected for both: `['NB0001', 'CA0001']` (the missing capability, and the missing `name` clause).

- [ ] **Step 4: Document**

In `README.md`, add after "VS Code and generated language support" a section:

````markdown
## Installable plugins for a language

A language project packages its `nitrogen.json` language as editor plugins:

```sh
nitrogen package --config nitrogen.json --output dist            # both
nitrogen package --config nitrogen.json --output dist --vscode   # dist/<id>-<version>.vsix
nitrogen package --config nitrogen.json --output dist --rider    # dist/<id>-<version>-rider.zip
```

Each plugin carries the grammar, helper sources, and a portable Nitrogen server, and runs it with the user's .NET 10 runtime (`dotnet`), so it works in any folder and on any OS. Install the `.vsix` with *Extensions → … → Install from VSIX*, and the ZIP with Rider's *Settings → Plugins → ⚙ → Install Plugin from Disk*. Packaging needs npm for VS Code, and Gradle with JDK 25 for Rider. `generate vscode` and `generate rider --self-contained` write the projects without building them. The optional `"version"` field of a language entry sets the plugin version (default `0.1.0`).
````

In `editors/vscode/README.md`, add one line: `For a single language, nitrogen package builds a self-contained extension instead; see the repository README.` In `editors/rider/README.md`, add: `nitrogen generate rider --self-contained bundles the language and a portable server run with dotnet; nitrogen package builds the ZIP.`

- [ ] **Step 5: Final gate and commit**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`
Expected: 0 warnings, all pass.

```bash
git add README.md editors/vscode/README.md editors/rider/README.md Nitrogen.Cli Nitrogen.Tests
git commit -m "Document installable language plugins"
```

- [ ] **Step 6: Report**

List what was verified (tests, both packages built, both server commands answered over LSP from an empty folder) and what was not (installation in VS Code and Rider). Offer the Nitrogen.Concepts follow-up from spec section 7.
