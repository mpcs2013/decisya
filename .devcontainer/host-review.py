#!/usr/bin/env python3
"""Host-side review of sandbox-written changes (ADR-0010, issue #36, G4-02).

Runs on the HOST, never inside the sandbox (this file is itself read-only there, via the
`.devcontainer` overlay). Per the runbook (G4-03), run this and read its output and
`git diff` before: reopening the VS 2026 solution, any host build/run/test, `git commit`, and
any host Claude session.

Usage:  & $env:DECISYA_PYTHON .devcontainer\\host-review.py [--base <ref>]

Input: `git status --porcelain=v1 -z --ignored --untracked-files=all` and
`git diff --name-only -z <base>...HEAD` (agent commits Marco has made but not yet built;
<base> defaults to "main"; `-z` so paths with spaces or non-ASCII characters parse correctly,
N-03), plus a filesystem walk for symlinks, reparse points, and nested `.git`
(directory or file) and `.claude`/`CLAUDE.md` that plain `git status` does not see inside an
opaque nested-repository directory, the same way precheck.py's `rglob(".git")` finds both
forms of nested `.git`.

Flags one line per finding, `<rule> <path>[:<line>]`; for content greps, only the matching
line's text is printed (and never for a path that looks like a secret file). Exit 1 if
anything is flagged, 0 if clean, 2 if a git command itself fails (N-03: never report "clean"
on a git error). Flagged does not mean malicious: it means "read this diff before VS 2026 or
git runs it".
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


class GitFailure(Exception):
    """Raised whenever a git subprocess exits non-zero, so main() fails closed (N-03) instead
    of treating an empty/partial result as "clean"."""


def run(exe: str, args: list[str], cwd: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([exe, *args], cwd=cwd, capture_output=True, text=True, check=False)


def run_git(git: str, args: list[str], cwd: str, what: str) -> str:
    """Runs a git subprocess and returns its stdout, or raises GitFailure on a non-zero exit
    (N-03): a git error must never be silently read as "no changes"."""
    result = run(git, ["-C", str(REPO_ROOT), *args], cwd)
    if result.returncode != 0:
        raise GitFailure(f"{what} exited {result.returncode}: {result.stderr.strip() or '(no stderr)'}")
    return result.stdout


def git_status_paths(git: str, outside: str) -> set[str]:
    # -z: NUL-terminated, unquoted paths, so filenames with spaces, newlines or non-ASCII
    # characters parse correctly (N-03) instead of relying on porcelain v1's C-quoting, which
    # the previous version did not even undo properly (it only stripped the surrounding
    # quotes, leaving octal escapes in the text used for path matching and content greps).
    out = run_git(git, ["status", "--porcelain=v1", "-z", "--ignored", "--untracked-files=all"], outside, "git status")
    tokens = out.split("\0")
    paths: set[str] = set()
    i = 0
    while i < len(tokens):
        token = tokens[i]
        i += 1
        if not token:
            continue
        xy, path = token[:2], token[3:]
        paths.add(path)
        # Renames/copies: the porcelain -z format emits the new path in this token, then the
        # original path as a second, separate NUL-terminated token (not " -> "-joined as in
        # the non-`-z` format). Keep both; the old path having existed is itself worth a look.
        if ("R" in xy or "C" in xy) and i < len(tokens) and tokens[i]:
            paths.add(tokens[i])
            i += 1
    return paths


def git_diff_paths(git: str, base: str, outside: str) -> set[str]:
    out = run_git(git, ["diff", "--name-only", "-z", f"{base}...HEAD"], outside, "git diff")
    return {t for t in out.split("\0") if t}


def walk_fs_only_findings(root: Path) -> list[tuple[str, str]]:
    """Findings that plain `git status` cannot see, found by walking the filesystem directly
    and never following symlinks (junctions are a separate, open gap: N-14):

    - nested `.git`, as a directory OR a file. A git worktree or submodule links back with a
      *file* named `.git` (containing `gitdir: ...`), not a directory; matching only `dirnames`
      missed that form. Either form makes git treat the containing directory as an opaque
      nested repository -- it reports only the directory itself in `git status`, even with
      `--untracked-files=all`, so `path_findings` above can never see inside it. This mirrors
      precheck.py's `REPO_ROOT.rglob(".git")`, which matches both forms the same way.
    - nested `.claude` (directory) and `CLAUDE.md` (file), by the same walk, for defence in
      depth: a sibling nested `.git` in the same untracked directory would make git hide these
      too, the same way it hides everything else there.
    - symlinks and reparse points anywhere in the tree.
    """
    findings: list[tuple[str, str]] = []
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        current = Path(dirpath)
        if current != root:
            if ".git" in dirnames or ".git" in filenames:
                findings.append(("nested-git", str((current / ".git").relative_to(root))))
            if ".claude" in dirnames:
                findings.append(("nested-claude-dir", str((current / ".claude").relative_to(root))))
            for fname in filenames:
                if fname.lower() == "claude.md":
                    findings.append(("nested-claude-md", str((current / fname).relative_to(root))))
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

    try:
        changed = git_status_paths(git, outside) | git_diff_paths(git, args.base, outside)
    except GitFailure as exc:
        # N-03: fail closed. A git error must never be reported as "clean" -- exit non-zero so
        # the runbook's "before building/committing/reopening" gate blocks on it, the same as a
        # real finding would.
        print(f"host-review: git failed: {exc}", file=sys.stderr)
        return 2

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
