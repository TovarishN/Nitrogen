#!/usr/bin/env bash
# Builds and tests Nitrogen, then produces the editor plugins:
#   artifacts/nitrogen-*.vsix         the VS Code extension
#   artifacts/rider/*-rider.zip       Rider plugins
#   Both carry the portable (framework-dependent) server and run it with dotnet (.NET 10) on any OS.
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

step() { printf '\n==> %s\n' "$*"; }

rm -rf "$artifacts"
mkdir -p "$artifacts/rider"

step "Build and test Nitrogen $version"
dotnet build "$root/Nitrogen.slnx" -c Release -warnaserror -p:Version="$version"
dotnet test "$root/Nitrogen.Tests/Nitrogen.Tests.csproj" -c Release --no-build
nitrogen=(dotnet "$root/Nitrogen.Cli/bin/Release/net10.0/nitrogen.dll")

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
copy_server "$server_build" "$vscode/server"
(cd "$vscode" && npm ci && npm run compile && npm run package)
mv "$vscode"/*.vsix "$artifacts/"
smoke_server "$vscode/server"

step "Test the shared Rider plugin code"
(cd "$root/editors/rider" && gradle test --console=plain)

# Generates a Rider plugin into artifacts/rider-src/<name>, builds it, and collects its ZIP.
rider_plugin() {
    local out="$artifacts/rider-src/$1"
    shift
    "${nitrogen[@]}" generate rider "$@" --self-contained --server "$server_build" --output "$out"
    (cd "$out" && gradle buildPlugin --console=plain)
    cp "$out"/build/distributions/*.zip "$artifacts/rider/"
    smoke_server "$out/bundle/server"
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
