# Nitrogen as NuGet packages — design

**Date:** 2026-09-30. **Status:** approved conversational design, pending review of this written spec.

## Goal

A project that uses Nitrogen — first the Nitrogen.Concepts catalog, which today builds Nitrogen from a git submodule — consumes released packages instead of source:

| Package | Contents | Consumer use |
| --- | --- | --- |
| `Nitrogen.Runtime` | `lib/net10.0/Nitrogen.Runtime.dll` | `<PackageReference Include="Nitrogen.Runtime" />` for parsing, binding, semantics, HIR |
| `Nitrogen.Generator` | `analyzers/dotnet/cs/Nitrogen.Generator.dll`, `build/` and `buildTransitive/Nitrogen.Generator.props` | `<PackageReference Include="Nitrogen.Generator" PrivateAssets="all" />` plus `<AdditionalFiles Include="*.ngr" Namespace="…" />` |
| `Nitrogen.Cli` | the `nitrogen` .NET tool | `dotnet tool install` or a tool manifest; `dotnet nitrogen package …`, `lsp`, `generate` |

Packages are published to GitHub Packages (`https://nuget.pkg.github.com/TovarishN/index.json`) when a `v*` tag is pushed. Out of scope: nuget.org, signing, and packaging `Nitrogen.Workspace`, `Nitrogen.LanguageService`, or `Nitrogen.Geometry` as libraries (the tool carries what it needs).

## Design

### 1. Package metadata

`Directory.Build.props` sets, for every project: `VersionPrefix` `0.1.0`, `Authors`, `PackageLicenseExpression` `MIT`, `RepositoryUrl` `https://github.com/TovarishN/Nitrogen` (GitHub Packages links a package to its repository through it), `RepositoryType` `git`, and `IsPackable` `false`. The three packaged projects set `IsPackable` `true`, `PackageId`, and `Description`. A release sets the version with `-p:Version=X.Y.Z` from the tag.

### 2. `Nitrogen.Runtime`

A plain library package. It has no project references, so it has no package dependencies.

### 3. `Nitrogen.Generator`

The generator is already one self-contained `netstandard2.0` analyzer (the grammar library is compiled in). The package:

- sets `IncludeBuildOutput=false` and packs the built assembly into `analyzers/dotnet/cs`;
- keeps `Microsoft.CodeAnalysis.CSharp` private (`PrivateAssets=all`), so the package has no dependencies;
- ships `Nitrogen.Generator.props` in `build/` and `buildTransitive/`, which adds `CompilerVisibleItemMetadata` for `AdditionalFiles`' `Namespace` and `CompilerVisibleProperty` `RootNamespace` (the two inputs the generator reads), and defaults `AllowUnsafeBlocks` to `true` because generated parsers dispatch rules through function pointers.

Generated code needs `Nitrogen.Runtime` at compile time; consumers reference both packages. A `netstandard2.0` analyzer project cannot reference the `net10.0` runtime project to declare that dependency, so it stays explicit.

### 4. `Nitrogen.Cli` as a .NET tool

`PackAsTool=true` with `ToolCommandName=nitrogen`. The tool is a framework-dependent build, so `nitrogen package` finds `nitrogen.dll` and `nitrogen.runtimeconfig.json` in its own directory and bundles it as the plugins' server, as it does from a build output today. The VS Code package files are embedded resources and the Rider templates are code, so no repository files are needed at run time.

### 5. Smoke test

`eng/package-smoke.sh [FEED VERSION]` packs the three projects (or uses a given feed and version), then, in a temporary directory with a `nuget.config` that points at that feed:

1. builds and runs a console project that references `Nitrogen.Runtime` and `Nitrogen.Generator`, compiles a small grammar with a `check`, and prints the check's diagnostic for a failing input;
2. installs the tool with `--tool-path` and runs `nitrogen generate vscode` on that grammar's `nitrogen.json`, then checks that the generated bundle contains `server/nitrogen.dll`.

CI runs it on every push and pull request (a new `packages` job in `ci.yml`), so packaging cannot silently break.

### 6. Publishing

`.github/workflows/packages.yml` runs on `v*` tags with `packages: write`: build with warnings as errors, test, pack the three projects with the tag's version, run the smoke test against those packages, then `dotnet nuget push` to GitHub Packages with `GITHUB_TOKEN` (`--skip-duplicate`). The first release is `v0.1.0`, tagged by the repository owner.

### 7. Consumer: the catalog (after the first release)

In `TovarishN/Nitrogen.Concepts`:

- remove the `external/Nitrogen` submodule and `.gitmodules`;
- add `nuget.config` with nuget.org and a `nitrogen` source for GitHub Packages, using package source mapping so only `Nitrogen.*` comes from it;
- reference `Nitrogen.Runtime` and `Nitrogen.Generator` in `tools/Catalog/Catalog.csproj` and `Nitrogen.Runtime` in its tests, dropping the project references and hand-written generator items; one `NitrogenVersion` property in a root `Directory.Build.props` pins the version;
- add `.config/dotnet-tools.json` pinning `nitrogen.cli`, and make `tools/editors/build.sh` run `dotnet tool restore` and `dotnet nitrogen package …`;
- in CI, drop the deploy key and submodule steps, grant `packages: read`, and pass `NuGetPackageSourceCredentials_nitrogen: Username=${{ github.actor }};Password=${{ secrets.GITHUB_TOKEN }}`; the owner grants the catalog repository read access to the three packages in their package settings;
- document local setup: a classic personal access token with `read:packages`, supplied through the same `NuGetPackageSourceCredentials_nitrogen` environment variable;
- update the README, and the realizations' `runtime` fields to name the package version.

## Trade-offs

Changes a consumer needs from Nitrogen now require a release. Everyone building a consumer locally needs a token for the private feed. In return, consumers build without Nitrogen's source or the Rider/VS Code line-ending pitfalls of a source checkout, and pin a version rather than a commit.

## Testing

- The smoke test (section 5), locally and in CI.
- `dotnet build Nitrogen.slnx -warnaserror` and the existing tests stay green; packaging must not change how the solution builds.
- The catalog switch is verified in its own pull request: `dotnet test Catalog.slnx`, `validate`, and `tools/editors/build.sh` against the published `0.1.0` packages.
