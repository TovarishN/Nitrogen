#!/usr/bin/env bash
# Attaches files to the GitHub release of a tag, creating the release as a draft (titled
# "Nitrogen X.Y.Z", with GitHub's generated notes) when there is none yet. For a v* tag, the
# Packages workflow attaches the .nupkg files and the Plugins workflow the editor plugins; their
# release jobs share a concurrency group, so they run one at a time and only one creates the draft.
# A maintainer then edits the notes and publishes it.
#
#   eng/release-draft.sh TAG FILE...      needs GH_TOKEN with contents: write
set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "usage: eng/release-draft.sh TAG FILE..." >&2
    exit 2
fi
tag="$1"
shift

if gh release view "$tag" >/dev/null 2>&1; then
    gh release upload "$tag" "$@" --clobber
    echo "attached $# file(s) to the release of $tag"
else
    gh release create "$tag" "$@" --draft --verify-tag --title "Nitrogen ${tag#v}" --generate-notes
    echo "drafted the release of $tag with $# file(s)"
fi
