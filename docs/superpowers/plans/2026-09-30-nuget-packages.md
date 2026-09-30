# Nitrogen NuGet Packages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish `Nitrogen.Runtime`, `Nitrogen.Generator`, and the `nitrogen` tool (`Nitrogen.Cli`) to GitHub Packages, verified by a smoke test, and then switch the Nitrogen.Concepts catalog from its submodule to those packages.

**Architecture:** Package metadata in `Directory.Build.props`; the generator packs its own assembly as an analyzer plus a `.props` file with the MSBuild inputs it reads; the CLI packs as a .NET tool. `eng/package-smoke.sh` consumes the packages from a local feed exactly as a user would. A tag-triggered workflow packs, smoke-tests, and pushes. Part B moves the catalog to `PackageReference`s and a tool manifest once `v0.1.0` exists.

**Tech Stack:** .NET 10 SDK packing (`dotnet pack`, `PackAsTool`), Roslyn analyzer packaging, GitHub Packages NuGet registry, GitHub Actions, bash.

**Spec:** [2026-09-30-nuget-packages-design.md](../specs/2026-09-30-nuget-packages-design.md)

---

## Background for the implementer

- Work in `../Nitrogen-nuget` (branch `nuget-packages`, from `origin/main` at 2c77806), checked out with LF.
- `Nitrogen.Generator` is `netstandard2.0`, compiles `Nitrogen.Grammar/*.cs` into itself, references `Microsoft.CodeAnalysis.CSharp` privately, and currently sets `IsPackable=false`. It reads `build_metadata.AdditionalFiles.Namespace` and `build_property.RootNamespace` (`NitrogenGenerator.cs`), which a consumer exposes with `CompilerVisibleItemMetadata` / `CompilerVisibleProperty` (see `Nitrogen.Geometry/Nitrogen.Geometry.csproj`).
- `Nitrogen.Cli` builds `nitrogen.dll` (`AssemblyName` `nitrogen`) and references Workspace, LanguageService, and Ngr.
- The repository gate is `dotnet build Nitrogen.slnx -warnaserror` then `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`.

## File structure

| Path | Change |
| --- | --- |
| `eng/package-smoke.sh` (new) | Pack, then build a consumer and run the tool from a local feed. |
| `Directory.Build.props` | Package metadata defaults; `IsPackable=false` by default. |
| `Nitrogen.Runtime/Nitrogen.Runtime.csproj` | Packable library. |
| `Nitrogen.Generator/Nitrogen.Generator.csproj`, `Nitrogen.Generator/build/Nitrogen.Generator.props` (new) | Analyzer package with consumer props. |
| `Nitrogen.Cli/Nitrogen.Cli.csproj` | .NET tool. |
| `.github/workflows/ci.yml` | `packages` job running the smoke test. |
| `.github/workflows/packages.yml` (new) | Publish on `v*` tags. |
| `README.md` | Using the packages. |

---

## Part A — Nitrogen

### Task 1: The smoke test (fails first)

**Files:**
- Create: `eng/package-smoke.sh`

- [ ] **Step 1: Write the script**

`eng/package-smoke.sh`:

```bash
#!/usr/bin/env bash
# Consumes Nitrogen's packages as a user would (spec: Nitrogen as NuGet packages).
#   eng/package-smoke.sh                 packs Nitrogen.Runtime, Nitrogen.Generator, and Nitrogen.Cli as 0.0.0-smoke
#   eng/package-smoke.sh FEED VERSION    uses packages already in FEED at VERSION
# Then, in a temporary directory: builds and runs a console project that compiles a grammar with a
# check through the packages, and installs the nitrogen tool and generates a VS Code extension with it.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [[ $# -eq 2 ]]; then
    feed="$(cd "$1" && pwd)"
    version="$2"
else
    feed="$work/feed"
    version="0.0.0-smoke"
    for project in Nitrogen.Runtime Nitrogen.Generator Nitrogen.Cli; do
        dotnet pack "$root/$project/$project.csproj" -c Release -p:Version="$version" -o "$feed"
    done
fi

step() { printf '\n==> %s\n' "$*"; }

consumer="$work/consumer"
mkdir -p "$consumer"
cat > "$work/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="$work/packages" />
  </config>
</configuration>
EOF

cat > "$consumer/Smoke.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Smoke</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Nitrogen.Runtime" Version="$version" />
    <PackageReference Include="Nitrogen.Generator" Version="$version" PrivateAssets="all" />
    <AdditionalFiles Include="Greet.ngr" Namespace="Smoke.Syntax" />
  </ItemGroup>
</Project>
EOF

cat > "$consumer/Greet.ngr" <<'EOF'
syntax module Greet
{
  token Word = ['a'..'z']+;
  syntax Hello = "hello" Name:Word
  {
    check GR0001 Name.Text != "bob" : "bob is not welcome" at Name;
  }
}
EOF

cat > "$consumer/Program.cs" <<'EOF'
using Nitrogen;
using Nitrogen.Binding;
using Nitrogen.Semantics;
using Smoke.Syntax;

var language = new LanguageBuilder().Add(GreetModule.Instance).Build();
using var parsed = language.Parse(args[0], GreetModule.Hello);
var project = new Project(language);
project.Set("sample", parsed.Tree);
var codes = new ProjectSemantics(project)["sample"].Diagnostics().Select(d => d.Code);
Console.WriteLine($"{parsed.Success} [{string.Join(",", codes)}]");
EOF

cat > "$consumer/nitrogen.json" <<'EOF'
{ "languages": [ { "name": "greet", "extensions": [".greet"], "grammars": ["Greet.ngr"], "start": "Greet.Hello" } ] }
EOF

step "Build a consumer of Nitrogen.Runtime and Nitrogen.Generator $version"
dotnet build "$consumer/Smoke.csproj" -c Release --configfile "$work/nuget.config" -warnaserror
bob="$(dotnet "$consumer/bin/Release/net10.0/Smoke.dll" "hello bob")"
ann="$(dotnet "$consumer/bin/Release/net10.0/Smoke.dll" "hello ann")"
echo "hello bob -> $bob"
echo "hello ann -> $ann"
[[ "$bob" == "True [GR0001]" && "$ann" == "True []" ]] || { echo "error: unexpected consumer output" >&2; exit 1; }

step "Install the nitrogen tool $version and generate a VS Code extension"
dotnet tool install Nitrogen.Cli --version "$version" --tool-path "$work/tool" --configfile "$work/nuget.config"
"$work/tool/nitrogen" generate vscode --config "$consumer/nitrogen.json" --output "$work/vscode"
for file in bundle/server/nitrogen.dll bundle/server/nitrogen.runtimeconfig.json bundle/language/nitrogen.json package.json; do
    [[ -f "$work/vscode/$file" ]] || { echo "error: generated extension has no $file" >&2; exit 1; }
done

step "Packages OK"
```

```bash
chmod +x eng/package-smoke.sh
```

- [ ] **Step 2: Run it to verify it fails**

Run: `eng/package-smoke.sh`
Expected: FAIL — `Nitrogen.Generator` is not packable (no `.nupkg`), so the consumer cannot restore it.

- [ ] **Step 3: Commit**

```bash
git add eng/package-smoke.sh
git commit -m "Add a package smoke test"
```

---

### Task 2: Package metadata and `Nitrogen.Runtime`

**Files:**
- Modify: `Directory.Build.props`, `Nitrogen.Runtime/Nitrogen.Runtime.csproj`

- [ ] **Step 1: Add metadata defaults**

In `Directory.Build.props`, add a second property group:

```xml
  <!-- NuGet packages (spec: Nitrogen as NuGet packages). Only projects that opt in are packed; a release
       sets the version with -p:Version from its tag. RepositoryUrl links a package to this repository on
       GitHub Packages. -->
  <PropertyGroup>
    <VersionPrefix>0.1.0</VersionPrefix>
    <Authors>Nitrogen contributors</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/TovarishN/Nitrogen</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
```

- [ ] **Step 2: Make the runtime packable**

In `Nitrogen.Runtime/Nitrogen.Runtime.csproj`, add to its property group:

```xml
    <IsPackable>true</IsPackable>
    <PackageId>Nitrogen.Runtime</PackageId>
    <Description>Nitrogen runtime: parsing with generated grammars, cross-file binding, grammar semantics and checks, and typed HIR.</Description>
```

- [ ] **Step 3: Check the package**

Run: `dotnet pack Nitrogen.Runtime/Nitrogen.Runtime.csproj -c Release -o /tmp/nitrogen-pack && unzip -l /tmp/nitrogen-pack/Nitrogen.Runtime.0.1.0.nupkg | grep -E "lib/net10.0/Nitrogen.Runtime.dll|nuspec"`
Expected: both entries listed.

- [ ] **Step 4: Gate and commit**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`
Expected: 0 warnings, all tests pass.

```bash
git add Directory.Build.props Nitrogen.Runtime/Nitrogen.Runtime.csproj
git commit -m "Package Nitrogen.Runtime"
```

---

### Task 3: `Nitrogen.Generator` analyzer package

**Files:**
- Create: `Nitrogen.Generator/build/Nitrogen.Generator.props`
- Modify: `Nitrogen.Generator/Nitrogen.Generator.csproj`

- [ ] **Step 1: Write the consumer props**

`Nitrogen.Generator/build/Nitrogen.Generator.props`:

```xml
<Project>
  <!-- Consumers list grammars as <AdditionalFiles Include="X.ngr" Namespace="..." />. The generator reads
       that Namespace metadata, falling back to RootNamespace. -->
  <ItemGroup>
    <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="Namespace" />
    <CompilerVisibleProperty Include="RootNamespace" />
  </ItemGroup>
  <!-- Generated parsers dispatch rules through function pointers. A project may still set it to false. -->
  <PropertyGroup>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Pack the analyzer**

In `Nitrogen.Generator/Nitrogen.Generator.csproj`, replace `<IsPackable>false</IsPackable>` with:

```xml
    <IsPackable>true</IsPackable>
    <PackageId>Nitrogen.Generator</PackageId>
    <Description>Nitrogen source generator: compiles .ngr grammars listed as AdditionalFiles into parsers, typed syntax views, binding, and semantics. Use with Nitrogen.Runtime.</Description>
    <!-- The assembly is an analyzer, not a library the consumer compiles against. -->
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <DevelopmentDependency>true</DevelopmentDependency>
    <NoWarn>$(NoWarn);RS1017;RS2008;NU5128</NoWarn>
```

(merge the `NoWarn` into the existing line rather than repeating it), and add:

```xml
  <ItemGroup>
    <None Include="$(OutputPath)$(AssemblyName).dll" Pack="true" PackagePath="analyzers/dotnet/cs" Visible="false" />
    <None Include="build/Nitrogen.Generator.props" Pack="true" PackagePath="build;buildTransitive" />
  </ItemGroup>
```

- [ ] **Step 3: Check the package**

Run: `dotnet pack Nitrogen.Generator/Nitrogen.Generator.csproj -c Release -o /tmp/nitrogen-pack && unzip -l /tmp/nitrogen-pack/Nitrogen.Generator.0.1.0.nupkg`
Expected: `analyzers/dotnet/cs/Nitrogen.Generator.dll`, `build/Nitrogen.Generator.props`, `buildTransitive/Nitrogen.Generator.props`, and no `lib/` folder. If `analyzers/…/Nitrogen.Generator.dll` is missing, `$(OutputPath)` did not include the target framework; use `$(TargetDir)$(TargetFileName)` instead.

- [ ] **Step 4: Gate and commit**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`
Expected: 0 warnings, all tests pass (in-repo projects still use the generator through `ProjectReference`).

```bash
git add Nitrogen.Generator
git commit -m "Package Nitrogen.Generator as an analyzer with consumer props"
```

---

### Task 4: The `nitrogen` tool, and the smoke test passes

**Files:**
- Modify: `Nitrogen.Cli/Nitrogen.Cli.csproj`

- [ ] **Step 1: Pack the CLI as a tool**

In `Nitrogen.Cli/Nitrogen.Cli.csproj`, add to its property group:

```xml
    <IsPackable>true</IsPackable>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>nitrogen</ToolCommandName>
    <PackageId>Nitrogen.Cli</PackageId>
    <Description>The nitrogen command: parse and watch against .ngr grammars, the language server (nitrogen lsp), and editor plugin generation and packaging.</Description>
```

- [ ] **Step 2: Run the smoke test**

Run: `eng/package-smoke.sh`
Expected: `hello bob -> True [GR0001]`, `hello ann -> True []`, the tool installs, the extension is generated, and `==> Packages OK`.

If the consumer build fails on generated code, read the first compiler error:
- a C# language-version error means generated code uses a preview feature; report it and set `LangVersion` in the consumer props only if the feature cannot be avoided in the generator;
- a missing `Namespace` means the props did not load; check that the package contains `build/Nitrogen.Generator.props` named exactly after the package ID.

If the tool's `generate vscode` reports a single-file build, `LanguageBundle.DefaultServer` did not find `nitrogen.dll` next to the tool; list the tool's install directory (`$work/tool/.store/nitrogen.cli/…/tools/net10.0/any`) and adjust.

- [ ] **Step 3: Gate and commit**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build`
Expected: 0 warnings, all pass.

```bash
git add Nitrogen.Cli/Nitrogen.Cli.csproj
git commit -m "Package the nitrogen command as a .NET tool"
```

---

### Task 5: CI, publishing, and docs

**Files:**
- Modify: `.github/workflows/ci.yml`, `README.md`
- Create: `.github/workflows/packages.yml`

- [ ] **Step 1: Smoke-test packages in CI**

Append a job to `.github/workflows/ci.yml`:

```yaml
  packages:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x
      - run: eng/package-smoke.sh
```

- [ ] **Step 2: Publish on tags**

`.github/workflows/packages.yml`:

```yaml
name: Packages

on:
  push:
    tags: ['v*']

permissions:
  contents: read
  packages: write

jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x
      - run: dotnet build Nitrogen.slnx -warnaserror
      - run: dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build
      - name: Pack
        run: |
          version="${GITHUB_REF_NAME#v}"
          for project in Nitrogen.Runtime Nitrogen.Generator Nitrogen.Cli; do
            dotnet pack "$project/$project.csproj" -c Release -p:Version="$version" -o packages
          done
      - run: eng/package-smoke.sh packages "${GITHUB_REF_NAME#v}"
      - run: dotnet nuget push "packages/*.nupkg" --source "https://nuget.pkg.github.com/TovarishN/index.json" --api-key "${{ secrets.GITHUB_TOKEN }}" --skip-duplicate
```

- [ ] **Step 3: Document**

In `README.md`, after "Build and test", add:

````markdown
## Use Nitrogen as packages

Releases publish three packages to GitHub Packages (`https://nuget.pkg.github.com/TovarishN/index.json`):

```xml
<PackageReference Include="Nitrogen.Runtime" Version="0.1.0" />
<PackageReference Include="Nitrogen.Generator" Version="0.1.0" PrivateAssets="all" />
<AdditionalFiles Include="MyLanguage.ngr" Namespace="My.Language.Syntax" />
```

and the `nitrogen` tool: `dotnet tool install Nitrogen.Cli --version 0.1.0`. Reading the feed needs a GitHub token with `read:packages`; NuGet takes it from `NuGetPackageSourceCredentials_<source name>` (`Username=<user>;Password=<token>`). `eng/package-smoke.sh` builds a consumer and runs the tool from freshly packed packages; a `v*` tag publishes them.
````

- [ ] **Step 4: Gate and commit**

Run: `dotnet build Nitrogen.slnx -warnaserror && dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build && eng/package-smoke.sh`
Expected: 0 warnings, all pass, `==> Packages OK`.

```bash
git add .github/workflows/ci.yml .github/workflows/packages.yml README.md
git commit -m "Smoke-test packages in CI and publish them on version tags"
```

- [ ] **Step 5: Hand-off**

Open the Nitrogen pull request. After it merges, the repository owner pushes the tag `v0.1.0` (for example `git tag v0.1.0 origin/main && git push origin v0.1.0`) and, in each package's settings on GitHub, grants `TovarishN/Nitrogen.Concepts` read access. Part B starts once the Packages workflow has published `0.1.0`.

---

## Part B — Nitrogen.Concepts (after `v0.1.0` is published)

### Task 6: Switch the catalog to the packages

**Files (in `TovarishN/Nitrogen.Concepts`, a new branch from `main`):**
- Delete: `external/Nitrogen` (submodule), `.gitmodules`
- Create: `nuget.config`, `Directory.Build.props`, `.config/dotnet-tools.json`
- Modify: `tools/Catalog/Catalog.csproj`, `tools/Catalog.Tests/Catalog.Tests.csproj`, `tools/editors/build.sh`, `.github/workflows/validate.yml`, `README.md`, `realizations/SemanticCatalog.CatalogTool.ncat`, `realizations/SemanticCatalog.EditorSupport.ncat`

- [ ] **Step 1: Remove the submodule**

```bash
git submodule deinit -f external/Nitrogen
git rm -f external/Nitrogen
rm -rf .git/modules/external/Nitrogen
git rm -f .gitmodules
```

- [ ] **Step 2: Package sources, version, and tool manifest**

`nuget.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="nitrogen" value="https://nuget.pkg.github.com/TovarishN/index.json" />
  </packageSources>
  <!-- Only Nitrogen's packages come from GitHub Packages. Credentials: NuGetPackageSourceCredentials_nitrogen. -->
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="nitrogen">
      <package pattern="Nitrogen.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

`Directory.Build.props`:

```xml
<Project>
  <!-- The Nitrogen release the catalog builds against; .config/dotnet-tools.json pins the same version of the tool. -->
  <PropertyGroup>
    <NitrogenVersion>0.1.0</NitrogenVersion>
  </PropertyGroup>
</Project>
```

`.config/dotnet-tools.json`:

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "nitrogen.cli": {
      "version": "0.1.0",
      "commands": ["nitrogen"],
      "rollForward": false
    }
  }
}
```

- [ ] **Step 3: Package references**

In `tools/Catalog/Catalog.csproj`, replace the two `ProjectReference`s to `external/Nitrogen` with:

```xml
    <PackageReference Include="Nitrogen.Runtime" Version="$(NitrogenVersion)" />
    <PackageReference Include="Nitrogen.Generator" Version="$(NitrogenVersion)" PrivateAssets="all" />
```

and remove the `CompilerVisibleItemMetadata` and `CompilerVisibleProperty` items and `AllowUnsafeBlocks` (the generator's props supply them). Keep the `AdditionalFiles` and `Compile` items for `language/`.

In `tools/Catalog.Tests/Catalog.Tests.csproj`, replace the `ProjectReference` to `external/Nitrogen/Nitrogen.Runtime` with `<PackageReference Include="Nitrogen.Runtime" Version="$(NitrogenVersion)" />`.

- [ ] **Step 4: Editor build script**

Replace the body of `tools/editors/build.sh` after `set -euo pipefail` with:

```bash
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"
dotnet tool restore
dotnet nitrogen package --config "$root/nitrogen.json" --output "$root/artifacts" "$@"
```

and update its header comment to say it uses the pinned `nitrogen` tool (`.config/dotnet-tools.json`) and needs the `NuGetPackageSourceCredentials_nitrogen` credentials.

- [ ] **Step 5: CI**

In `.github/workflows/validate.yml`: delete the "Fetch Nitrogen submodule" step; add at the top level

```yaml
permissions:
  contents: read
  packages: read
```

and add to the `catalog` job

```yaml
    env:
      NuGetPackageSourceCredentials_nitrogen: Username=${{ github.actor }};Password=${{ secrets.GITHUB_TOKEN }}
```

- [ ] **Step 6: README and records**

In `README.md`: clone without `--recurse-submodules`; before the first `dotnet` command, add

```sh
export NuGetPackageSourceCredentials_nitrogen="Username=<github user>;Password=<classic token with read:packages>"
```

and replace "The validator needs the .NET 10 SDK and the pinned Nitrogen checkout in `external/Nitrogen`" with "The validator needs the .NET 10 SDK and read access to Nitrogen's packages on GitHub Packages (see `nuget.config`)". In the Editor support section, say `build.sh` runs the `nitrogen` tool pinned in `.config/dotnet-tools.json`.

In `realizations/SemanticCatalog.CatalogTool.ncat` and `realizations/SemanticCatalog.EditorSupport.ncat`, change the Nitrogen part of `runtime` to `Nitrogen.Runtime 0.1.0` (CatalogTool) and `nitrogen tool 0.1.0` (EditorSupport), and set `source`/`revision` to the commit that removes the submodule (commit the build changes first, then the records, as in earlier follow-ups).

- [ ] **Step 7: Verify**

```bash
export NuGetPackageSourceCredentials_nitrogen="Username=<user>;Password=<token>"
dotnet test Catalog.slnx
dotnet run --project tools/Catalog -- validate
JAVA_HOME=<jdk 25> tools/editors/build.sh
```

Expected: 51 tests pass, `Catalog valid`, and `artifacts/catalog-0.1.0.vsix` and `artifacts/catalog-0.1.0-rider.zip` are built. Run the LSP probe from the workspace-indexing plan against the rebuilt VS Code package; expect no diagnostics with only `SemanticCatalog.CatalogTool.ncat` open.

- [ ] **Step 8: Commit and propose**

Commit the build changes, then the records, and open the catalog pull request. Its CI passes once the packages grant the catalog repository read access.
