#!/usr/bin/env python3
"""Exercise build numbering against real Git histories, including archive and shallow clones."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent


def run(directory, *args, check=True):
    return subprocess.run(args, cwd=directory, text=True, capture_output=True, check=check)


def identity(directory, version="1.2.3", check=True):
    result = run(directory, "dotnet", "msbuild", "probe.proj", "-nologo",
                 "-t:NitrogenBuildIdentity", f"-p:Version={version}",
                 "-getProperty:InformationalVersion,NitrogenBuildNumber,NitrogenCommit", check=check)
    if not check:
        return result
    # Git-less source archives can emit diagnostics before MSBuild's JSON result.
    return json.loads(result.stdout[result.stdout.index("{"):])["Properties"]


with tempfile.TemporaryDirectory(prefix="nitrogen-build-identity-") as temporary:
    archive = Path(temporary) / "archive"
    archive.mkdir()
    shutil.copy(ROOT / "Directory.Build.targets", archive)
    (archive / "probe.proj").write_text(
        '<Project><Import Project="Directory.Build.targets" /></Project>')
    assert identity(archive)["InformationalVersion"] == "1.2.3+build.0.unknown"

    repo = Path(temporary) / "repo"
    shutil.copytree(archive, repo)
    run(repo, "git", "init", "-q")
    run(repo, "git", "config", "user.email", "build-test@example.invalid")
    run(repo, "git", "config", "user.name", "Build identity test")
    run(repo, "git", "add", ".")
    run(repo, "git", "commit", "-qm", "First")
    first = identity(repo)
    sha = run(repo, "git", "rev-parse", "HEAD").stdout.strip()
    assert first["InformationalVersion"] == f"1.2.3+build.1.{sha}"
    assert identity(repo) == first  # Rebuilds of the same commit are stable.
    run(repo, "git", "commit", "--allow-empty", "-qm", "Second")
    second = identity(repo)
    assert second["NitrogenBuildNumber"] == "2"
    assert second["NitrogenCommit"] != first["NitrogenCommit"]
    assert identity(repo, "9.8.7")["InformationalVersion"].startswith("9.8.7+build.2.")

    shallow = Path(temporary) / "shallow"
    run(Path(temporary), "git", "clone", "-q", "--depth=1", repo.as_uri(), str(shallow))
    failure = identity(shallow, check=False)
    assert failure.returncode != 0
    assert "requires full Git history" in failure.stdout + failure.stderr, failure.stdout + failure.stderr

print("Build identity: commit increment, repeatability, version override, archive fallback, shallow guard passed")
