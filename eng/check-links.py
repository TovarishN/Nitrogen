#!/usr/bin/env python3
"""Checks the links of the repository's Markdown files.

Every relative link must reach a file in the repository, and a #anchor into a Markdown file must
match one of its headings (as GitHub spells heading anchors). A README that ships inside an editor
plugin is shown outside the repository, so it may not link out of the plugin's folder: such a link
must be an absolute URL.

    eng/check-links.py          check every tracked .md file outside docs/superpowers/; exit 1 and list the broken links
"""
import os
import re
import subprocess
import sys

# Design specs and plans quote other files' links as examples; they aren't links of their own.
SKIPPED = ("docs/superpowers/",)

# READMEs packaged into a plugin, and the folder each plugin is built from.
SHIPPED = {"editors/vscode/README.md": "editors/vscode"}


def slug(heading: str) -> str:
    """GitHub's anchor for a heading: lower case, punctuation other than - and _ dropped, spaces as hyphens."""
    text = re.sub(r"[^\w\- ]", "", heading.strip().lower())
    return text.replace(" ", "-")


def anchors(path: str) -> set[str]:
    with open(path, encoding="utf-8") as file:
        return {slug(h) for h in re.findall(r"^#+ (.*)$", strip_code(file.read()), re.M)}


def strip_code(text: str) -> str:
    """The text without fenced code blocks and inline code: links there are examples."""
    text = re.sub(r"^(`{3,}|~{3,}).*?^\1", "", text, flags=re.S | re.M)
    return re.sub(r"`[^`\n]*`", "", text)


def problems(path: str) -> list[str]:
    with open(path, encoding="utf-8") as file:
        text = strip_code(file.read())
    found = []
    for target in re.findall(r"\]\(<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\)", text):
        if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I):  # https:, mailto:, …
            continue
        link, _, anchor = target.partition("#")
        resolved = os.path.normpath(os.path.join(os.path.dirname(path), link)) if link else path
        if path in SHIPPED and not resolved.startswith(SHIPPED[path] + os.sep) and resolved != path:
            found.append(f"{path}: {target}: this README ships in a plugin, so link outside {SHIPPED[path]}/ by URL")
            continue
        if not os.path.exists(resolved):
            found.append(f"{path}: {target}: no {resolved}")
        elif anchor and resolved.endswith(".md") and anchor not in anchors(resolved):
            found.append(f"{path}: {target}: no heading #{anchor} in {resolved}")
    return found


def main() -> int:
    root = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
    os.chdir(root)
    files = [f for f in subprocess.run(["git", "ls-files", "*.md"], capture_output=True, text=True, check=True).stdout.split()
             if not f.startswith(SKIPPED)]
    found = [problem for path in files for problem in problems(path)]
    print("\n".join(found) if found else f"{len(files)} Markdown files: every link resolves")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
