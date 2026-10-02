#!/usr/bin/env bash
# Builds and tests Nitrogen, then produces the editor plugins:
#   artifacts/nitrogen-*.vsix         the VS Code extension
#   artifacts/rider/*-rider.zip       Rider plugins, each bundling a server for this machine
#   artifacts/server/<target>/        the bundled single-file server
#
# Usage: ./build.sh [--config nitrogen.json [--language NAME]]
#   Always builds the Rider plugin for .ngr grammars; --config also builds one for that language.
#   VERSION=MAJOR.MINOR.PATCH overrides the release version from Directory.Build.props.
# Requires the .NET 10 SDK, Node.js with npm, Gradle, and a JDK 25 (Rider 2026.2's Java version).
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
artifacts="$root/artifacts"
config=""
language=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --config) config="$2"; shift 2 ;;
        --language) language="$2"; shift 2 ;;
        *) echo "usage: ./build.sh [--config nitrogen.json [--language NAME]]" >&2; exit 2 ;;
    esac
done

version="${VERSION:-$(dotnet msbuild "$root/Nitrogen.Cli/Nitrogen.Cli.csproj" -getProperty:Version -nologo)}"
if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
    echo "VERSION must be MAJOR.MINOR.PATCH" >&2
    exit 2
fi

# The .NET runtime identifier and Rider bundle target of this machine.
case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) rid=osx-arm64; target=macos-aarch64 ;;
    Darwin-x86_64) rid=osx-x64; target=macos-x64 ;;
    Linux-x86_64) rid=linux-x64; target=linux-x64 ;;
    MINGW*-x86_64 | MSYS*-x86_64 | CYGWIN*-x86_64) rid=win-x64; target=windows-x64 ;;
    *) rid=""; target="" ;;
esac

step() { printf '\n==> %s\n' "$*"; }

rm -rf "$artifacts"
mkdir -p "$artifacts/rider"

step "Build and test Nitrogen $version"
dotnet build "$root/Nitrogen.slnx" -c Release -warnaserror -p:Version="$version"
dotnet test "$root/Nitrogen.Tests/Nitrogen.Tests.csproj" -c Release --no-build
nitrogen=(dotnet "$root/Nitrogen.Cli/bin/Release/net10.0/nitrogen.dll")

bundle=()
if [[ -n "$rid" ]]; then
    step "Publish a single-file server for $target"
    dotnet publish "$root/Nitrogen.Cli/Nitrogen.Cli.csproj" -c Release -r "$rid" --self-contained \
        -p:Version="$version" -p:PublishSingleFile=true -o "$artifacts/server/$target"
    server="$artifacts/server/$target/nitrogen"
    if [[ "$rid" == win-x64 ]]; then server="$server.exe"; fi
    bundle=(--bundle "$target=$server")
else
    echo "warning: no Rider bundle target for $(uname -s) $(uname -m); plugins will use the Nitrogen setting or PATH" >&2
fi

step "Package the VS Code extension"
vscode="$artifacts/vscode-src"
mkdir -p "$vscode"
(cd "$root/editors/vscode" && tar --exclude=node_modules --exclude=out --exclude='*.vsix' -cf - .) | (cd "$vscode" && tar -xf -)
node - "$vscode" "$version" <<'JS'
const fs = require('node:fs');
const path = require('node:path');
const [directory, version] = process.argv.slice(2);
for (const name of ['package.json', 'package-lock.json']) {
    const file = path.join(directory, name);
    const manifest = JSON.parse(fs.readFileSync(file, 'utf8'));
    manifest.version = version;
    if (manifest.packages) manifest.packages[''].version = version;
    fs.writeFileSync(file, JSON.stringify(manifest, null, 2) + '\n');
}
JS
(cd "$vscode" && npm ci && npm run compile && npm run package)
mv "$vscode"/*.vsix "$artifacts/"

step "Test the shared Rider plugin code"
(cd "$root/editors/rider" && gradle test --console=plain)

# Generates a Rider plugin into artifacts/rider-src/<name>, builds it, and collects its ZIP.
rider_plugin() {
    local out="$artifacts/rider-src/$1"
    shift
    "${nitrogen[@]}" generate rider "$@" ${bundle[@]+"${bundle[@]}"} --output "$out"
    (cd "$out" && gradle buildPlugin --console=plain)
    cp "$out"/build/distributions/*.zip "$artifacts/rider/"
}

step "Build the Rider plugin for .ngr grammars"
rider_plugin ngr --grammar "$root/Nitrogen.Ngr/Nitrogen.ngr" --start Nitrogen.File

if [[ -n "$config" ]]; then
    step "Build the Rider plugin for $config"
    args=(--config "$config")
    if [[ -n "$language" ]]; then args+=(--language "$language"); fi
    rider_plugin language "${args[@]}"
fi

step "Done"
find "$artifacts" -maxdepth 2 \( -name '*.vsix' -o -name '*.zip' \) -print
