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
