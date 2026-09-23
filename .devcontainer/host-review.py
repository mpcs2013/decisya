#!/usr/bin/env python3
"""Host-side review of sandbox-written changes (ADR-0010, issue #36, G4-02).

Runs on the HOST, never inside the sandbox (this file is itself read-only there, via the
`.devcontainer` overlay). Per the runbook (G4-03), run this and read its output and
`git diff` before: reopening the VS 2026 solution, any host build/run/test, `git commit`, and
any host Claude session.

Usage:  & $env:DECISYA_PYTHON .devcontainer\\host-review.py [--base <ref>]

Input: `git status --porcelain=v1 --ignored --untracked-files=all` (working-tree changes,
including git-ignored ones), `git diff --name-only <base>...HEAD` (agent commits Marco has
made but not yet built; <base> defaults to "main"), plus a filesystem walk for symlinks,
reparse points and nested `.git` directories that plain `git status` does not descend into.

Flags one line per finding, `<rule> <path>[:<line>]`; for content greps, only the matching
line's text is printed (and never for a path that looks like a secret file). Exit 1 if
anything is flagged, 0 otherwise. Flagged does not mean malicious: it means "read this diff
before VS 2026 or git runs it".
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SKIP_DIR_NAMES = {".git"}

# Same extension list precheck.py refuses on in the repo root; here it is a review flag, not a
# refusal, and applies wherever the file lives.
EXEC_EXTENSIONS = {".exe", ".dll", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".js", ".msi", ".scr"}
WINDOWS_SHELL_ARTEFACTS = {".url", ".lnk", ".library-ms", ".searchconnector-ms"}
SECRET_LIKE = re.compile(r"(^|/)\.env(\.[^/]*)?$", re.IGNORECASE)

CONTENT_PATTERNS = [
    ("msbuild-exec", re.compile(r"<Exec\b")),
    ("msbuild-usingtask", re.compile(r"<UsingTask\b")),
    ("msbuild-taskfactory", re.compile(r"TaskFactory")),
    ("msbuild-analyzer", re.compile(r"<Analyzer\b")),
    ("msbuild-import", re.compile(r"Import\s+Project=")),
    ("msbuild-logger", re.compile(r"-logger\b")),
    ("npm-lifecycle-script", re.compile(r"preinstall|postinstall|prepare")),
    ("npm-node-options", re.compile(r"node-options")),
    ("npm-script-shell", re.compile(r"script-shell")),
    ("msbuild-sdks", re.compile(r"msbuild-sdks")),
]
GLOBAL_JSON_PATTERNS = [("global-json-sdk-paths", re.compile(r'"paths"'))]


def resolve_on_path(name: str) -> str | None:
    """Same resolution discipline as precheck.py: no cwd search, no directory inside the repo.

    On Windows this must never return an extensionless match: WSL/Git-for-Windows ship
    extensionless shell-script shims named exactly "git" next to the real "git.exe" on some
    PATH entries, and passing one of those to subprocess.run (which does not go through
    cmd.exe) fails with WinError 193 ("not a valid Win32 application"). So on Windows this
    tries only ".exe" and ".com", in that order, never a bare name and never ".bat"/".cmd"
    (cmd.exe re-parses those, a bigger footgun than the OSError; T-05)."""
    exts = [".exe", ".com"] if os.name == "nt" else [""]
    for raw_entry in os.environ.get("PATH", "").split(os.pathsep):
        entry = raw_entry.strip('"')
        if not entry or entry == ".":
            continue
        try:
            directory = Path(entry).resolve()
        except OSError:
            continue
        if not directory.is_dir():
            continue
        try:
            directory.relative_to(REPO_ROOT)
            continue
        except ValueError:
            pass
        for ext in exts:
            candidate = directory / f"{name}{ext}"
            if candidate.is_file():
                return str(candidate)
    return None


def run(exe: str, args: list[str], cwd: str) -> str:
    result = subprocess.run([exe, *args], cwd=cwd, capture_output=True, text=True, check=False)
    return result.stdout


def git_status_paths(git: str, outside: str) -> set[str]:
    out = run(git, ["-C", str(REPO_ROOT), "status", "--porcelain=v1", "--ignored", "--untracked-files=all"], outside)
    paths: set[str] = set()
    for line in out.splitlines():
        if len(line) < 4:
            continue
        rest = line[3:].strip('"')
        # Renames: "old -> new"; keep both, the new path is what matters most but the old one
        # having existed is itself worth a look in `git diff`.
        for part in rest.split(" -> "):
            if part:
                paths.add(part)
    return paths


def git_diff_paths(git: str, base: str, outside: str) -> set[str]:
    out = run(git, ["-C", str(REPO_ROOT), "diff", "--name-only", f"{base}...HEAD"], outside)
    return {line for line in out.splitlines() if line}


def walk_fs_only_findings(root: Path) -> list[tuple[str, str]]:
    """Findings that plain `git status` cannot see: nested `.git`, and symlinks/reparse
    points anywhere in the tree (an untracked directory containing them is opaque to
    `git status`, which reports only the directory itself, not what is inside it)."""
    findings: list[tuple[str, str]] = []
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        current = Path(dirpath)
        if current != root and ".git" in dirnames:
            findings.append(("nested-git", str((current / ".git").relative_to(root))))
        for name in dirnames + filenames:
            p = current / name
            try:
                if p.is_symlink():
                    findings.append(("symlink-or-reparse-point", str(p.relative_to(root))))
            except OSError:
                continue
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_NAMES]
    return findings


def path_findings(rel_path: str) -> list[str]:
    p = PurePosix(rel_path)
    name = p.name
    lower_name = name.lower()
    suffix = p.suffix.lower()
    findings: list[str] = []

    def flag(rule: str) -> None:
        findings.append(rule)

    if suffix == ".user":
        flag("dotnet-user-file")
    if re.search(r"(^|/)(bin|obj|artifacts)(/|$)", rel_path):
        flag("build-output-in-tree")
    if re.search(r"(^|/)\.vs(/|$)", rel_path):
        flag("dotvs-in-tree")
    if lower_name == "dotnet-tools.json" or re.search(r"(^|/)\.dotnet(/|$)", rel_path):
        flag("dotnet-tool-manifest-or-cache")
    if lower_name == "global.json":
        flag("root-global-json-changed" if rel_path == "global.json" else "non-root-global-json")
    if rel_path == ".pre-commit-config.yaml":
        flag("root-pre-commit-config-changed")
    if re.search(r"(^|/)Directory\.[^/]+$", rel_path) or suffix in {".props", ".targets", ".rsp", ".csproj", ".slnx", ".sln"}:
        flag("msbuild-project-or-props-file")
    if lower_name == "nuget.config":
        flag("nuget-config")
    if rel_path.endswith("Properties/launchSettings.json"):
        flag("launch-settings")
    if lower_name in {"package.json", ".npmrc"} or lower_name.startswith(".yarnrc"):
        flag("npm-config-or-manifest")
    if rel_path == ".github" or rel_path.startswith(".github/"):
        flag("github-workflow-or-config")
    if lower_name == ".mcp.json":
        flag("mcp-config")
    if lower_name == "claude.local.md":
        flag("claude-local-md")
    if lower_name == "claude.md" and rel_path != "CLAUDE.md":
        flag("nested-claude-md")
    if re.search(r"(^|/)\.claude(/|$)", rel_path) and not rel_path.startswith(".claude"):
        flag("nested-claude-dir")
    if rel_path == ".devcontainer.json":
        flag("devcontainer-json")
    if rel_path == ".devcontainer" or rel_path.startswith(".devcontainer/"):
        flag("devcontainer-dir-changed")
    if rel_path == ".claude" or rel_path.startswith(".claude/"):
        flag("claude-dir-changed")
    if rel_path == ".vscode" or rel_path.startswith(".vscode/"):
        flag("vscode-dir-changed")
    if suffix in EXEC_EXTENSIONS:
        flag("executable-or-script")
    outside_claude_and_devcontainer = not (rel_path.startswith(".claude/") or rel_path.startswith(".devcontainer/"))
    if suffix in {".sh", ".py"} and outside_claude_and_devcontainer:
        flag("script-outside-claude-and-devcontainer")
    if suffix in WINDOWS_SHELL_ARTEFACTS or lower_name == "desktop.ini":
        flag("windows-shell-artifact")
    if lower_name in {".gitattributes", ".lfsconfig", ".gitmodules"}:
        flag("git-attributes-family")
    return findings


class PurePosix:
    """Minimal posix-path helper so the rules above read the same regardless of host OS; git
    always reports paths with '/' separators, but be defensive."""

    def __init__(self, raw: str) -> None:
        self._raw = raw.replace("\\", "/")

    def __str__(self) -> str:
        return self._raw

    @property
    def name(self) -> str:
        return self._raw.rsplit("/", 1)[-1]

    @property
    def suffix(self) -> str:
        n = self.name
        return n[n.rfind("."):] if "." in n else ""


def content_findings(rel_path: str) -> list[tuple[str, int, str]]:
    if SECRET_LIKE.search(rel_path.replace("\\", "/")):
        return []
    full = REPO_ROOT / rel_path
    if not full.is_file():
        return []
    try:
        text = full.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return []
    patterns = list(CONTENT_PATTERNS)
    if PurePosix(rel_path).name.lower() == "global.json":
        patterns = patterns + GLOBAL_JSON_PATTERNS
    findings: list[tuple[str, int, str]] = []
    for i, line in enumerate(text.splitlines(), start=1):
        for rule, pattern in patterns:
            if pattern.search(line):
                findings.append((rule, i, line.strip()))
    return findings


def main() -> int:
    # A finding's content-grep text is an arbitrary line from the working tree (which may
    # contain characters outside the host console's active code page on Windows); never crash
    # the review over that, just replace what cannot be displayed.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")

    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="main")
    args = parser.parse_args()

    outside = tempfile.mkdtemp(prefix="decisya-sandbox-host-review-")
    git = resolve_on_path("git")
    if git is None:
        print("host-review: git not found on PATH (resolved without searching the current directory)")
        return 1

    changed = git_status_paths(git, outside) | git_diff_paths(git, args.base, outside)

    findings: list[str] = []
    for rel_path in sorted(changed):
        for rule in path_findings(rel_path):
            findings.append(f"{rule} {rel_path}")
        for rule, line_no, line_text in content_findings(rel_path):
            findings.append(f"{rule} {rel_path}:{line_no}: {line_text}")

    for rule, rel_path in walk_fs_only_findings(REPO_ROOT):
        findings.append(f"{rule} {rel_path}")

    for line in findings:
        print(line)
    if findings:
        print(f"host-review: {len(findings)} finding(s); read git diff before building, running, committing, or reopening the solution")
        return 1
    print("host-review: clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
