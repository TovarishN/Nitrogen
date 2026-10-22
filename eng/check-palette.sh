#!/usr/bin/env bash
# Checks that the server's semantic tokens paint what the TextMate grammars paint (the TextMate palette), so the switch
# from the first frame to semantic tokens shows no colour change:
#   - .ngr files against the hand-written editors/vscode/syntaxes/ngr.tmLanguage.json, scope for scope (--exact);
#   - a workspace language (examples/DateCalc) against the grammar `nitrogen generate textmate` writes for it.
# Accepted differences (the server knows better than a TextMate grammar can) are listed in eng/palette/<file>.allow.
# Requires the .NET 10 SDK and Node.js (run `npm ci` in editors/vscode first).
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

dotnet build "$repo/Nitrogen.Cli" -c Release -v quiet -nologo > "$work/build.log" || { cat "$work/build.log"; exit 1; }
nitrogen=(dotnet "$repo/Nitrogen.Cli/bin/Release/net10.0/nitrogen.dll")
compare=(node "$repo/editors/vscode/scripts/compare-scopes.mjs")
failed=0

check() { # <source> <grammar> <scope name> [extra compare-scopes arguments…] -- [extra tokens arguments…]
    local source=$1 grammar=$2 scope=$3; shift 3
    local options=() tokens=()
    while [ $# -gt 0 ] && [ "$1" != "--" ]; do options+=("$1"); shift; done
    [ $# -gt 0 ] && shift
    tokens=("$@")
    local allow="$repo/eng/palette/$(basename "$source").allow"
    [ -f "$allow" ] && options+=(--allow "$allow")
    "${nitrogen[@]}" tokens "$source" ${tokens[@]+"${tokens[@]}"} > "$work/tokens.json"
    if "${compare[@]}" --grammar "$grammar" --scope-name "$scope" --source "$source" --tokens "$work/tokens.json" ${options[@]+"${options[@]}"}; then
        echo "ok   $source"
    else
        failed=1
    fi
}

ngr="$repo/editors/vscode/syntaxes/ngr.tmLanguage.json"
check "$repo/Nitrogen.Ngr/Nitrogen.ngr" "$ngr" source.ngr --exact
check "$repo/examples/DateCalc/DateCalc.ngr" "$ngr" source.ngr --exact

config="$repo/examples/DateCalc/nitrogen.json"
"${nitrogen[@]}" generate textmate --config "$config" --language DateCalc --id datecalc --output "$work/datecalc.tmLanguage.json"
check "$repo/examples/DateCalc/sample.datecalc" "$work/datecalc.tmLanguage.json" source.datecalc -- --config "$config"

exit $failed
