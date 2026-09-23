#!/usr/bin/env python3
"""Host-side precheck for the agent sandbox (ADR-0010, issue #36; G4-06, G4-14, G4-18).

Runs on the HOST, before the sandbox starts: from `sandbox.py up`, and from the optional
`.devcontainer/devcontainer.json` `initializeCommand` (array form, absolute interpreter). It
refuses to start the sandbox -- printing paths only, never file contents -- when the working
tree has:

  - a secret file (git-ignored .env / .env.* other than .env.example, secrets.json, *.pfx,
    *.p12, *.pem, *.key, or an .npmrc with an _authToken) present at start (G4-14);
  - any gitleaks finding over the tree, using the repository's own .gitleaks.toml (fails
    closed if gitleaks itself is missing, per G4-14's "fails closed if missing");
  - an untracked executable or script in the repository root, which Windows' current-directory-
    first executable search could run instead of the real tool (T-05, G4-06);
  - a second `.devcontainer.json` (root, or nested under `.devcontainer/`), a nested `.git`, or
    a non-root `global.json` (T-01, T-02, T-05, T-06);
  - an untracked symlink or reparse point, if this host's bind mount represents container-
    created links as real links on the host side (G4-18, U-21) -- this script cannot observe
    that from the host side of a link created on the host, so it treats every untracked
    symlink the same way regardless of the answer to U-21, which is the conservative (fail
    closed) reading of "if U-21 shows...".

This file is itself read-only inside the sandbox (the `.devcontainer` overlay, G4-01); it only
ever runs on the host, invoked as `& $env:DECISYA_PYTHON .devcontainer\\precheck.py` (never
`python precheck.py` from inside `.devcontainer/`, so Windows' current-directory-first lookup
never has a chance to run a planted `python.bat` here either -- see sandbox.py and the runbook
for `NoDefaultCurrentDirectoryInExePath`).

`git` and `gitleaks` are resolved by walking PATH ourselves (never `shutil.which`, which
searches the current directory on Windows unless `NoDefaultCurrentDirectoryInExePath` is set),
skipping empty entries, ".", and any directory inside the repository. Every subprocess runs
with `cwd` outside the repository, using `-C <repo>` (git) or an absolute path (gitleaks) to
name the target, so a planted `git.exe`/`gitleaks.exe` next to the working directory is never
reachable even before that environment variable is set on a fresh machine.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

EXEC_EXTENSIONS = {".exe", ".dll", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".js", ".msi", ".scr"}
SECRET_EXACT_NAMES = {"secrets.json"}
SECRET_SUFFIXES = (".pfx", ".p12", ".pem", ".key")
# Never walked for the secret/executable/symlink scans below: git internals are not a file an
# agent "creates" in the working tree, and walking ~1000s of loose objects is pure overhead.
SKIP_DIR_NAMES = {".git"}


def resolve_on_path(name: str) -> str | None:
    """Finds `name` on PATH, skipping empty entries, ".", and any directory inside the repo.

    On Windows this must never return an extensionless match: WSL/Git-for-Windows ship
    extensionless shell-script shims named exactly "docker"/"git"/"gitleaks" next to the real
    "*.exe" on some PATH entries, and passing one of those to subprocess.run (which does not go
    through cmd.exe) fails with WinError 193 ("not a valid Win32 application") -- or worse,
    could silently run the wrong tool. So on Windows this tries only ".exe" and ".com", in that
    order, never a bare name and never ".bat"/".cmd" (cmd.exe re-parses those, a bigger footgun
    than the OSError; T-05)."""
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
            continue  # a directory inside the repo is never trusted for tool resolution
        except ValueError:
            pass
        for ext in exts:
            candidate = directory / f"{name}{ext}"
            if candidate.is_file():
                return str(candidate)
    return None


def run(exe: str, args: list[str], cwd: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([exe, *args], cwd=cwd, capture_output=True, text=True, check=False)


def walk(root: Path, *, include_dirs: bool):
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_NAMES]
        names = filenames + dirnames if include_dirs else filenames
        for name in names:
            yield Path(dirpath) / name


def tracked_files(git: str | None, outside_cwd: str) -> set[str] | None:
    """POSIX-style relative paths of every tracked file, or None if git could not be asked
    (in which case every untracked-looking file is treated as untracked, the safe direction).
    """
    if git is None:
        return None
    result = run(git, ["-C", str(REPO_ROOT), "ls-files", "-z"], outside_cwd)
    if result.returncode != 0:
        return None
    return set(result.stdout.split("\0")) if result.stdout else set()


def is_tracked(tracked: set[str] | None, path: Path) -> bool:
    if tracked is None:
        return False
    rel = path.relative_to(REPO_ROOT).as_posix()
    return rel in tracked


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")

    problems: list[str] = []
    outside = tempfile.mkdtemp(prefix="decisya-sandbox-precheck-")

    git = resolve_on_path("git")
    if git is None:
        problems.append("git not found on PATH (resolved without searching the current directory)")
    tracked = tracked_files(git, outside)

    # --- secret files present at start (G4-14) ---------------------------------------------
    for path in walk(REPO_ROOT, include_dirs=False):
        name = path.name
        rel = path.relative_to(REPO_ROOT)
        if name == ".env" or (name.startswith(".env.") and name != ".env.example"):
            problems.append(f"secret file present at start: {rel}")
        elif name in SECRET_EXACT_NAMES:
            problems.append(f"secret file present at start: {rel}")
        elif path.suffix.lower() in SECRET_SUFFIXES:
            problems.append(f"secret file present at start: {rel}")
        elif name == ".npmrc":
            try:
                content = path.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                content = ""
            if "_authtoken" in content.lower():
                problems.append(f".npmrc contains _authToken: {rel}")

    # --- gitleaks: fails closed if missing (G4-14) ------------------------------------------
    gitleaks = resolve_on_path("gitleaks")
    if gitleaks is None:
        problems.append("gitleaks not found on PATH; refusing to start with an unscanned tree (fails closed, G4-14)")
    else:
        config = REPO_ROOT / ".gitleaks.toml"
        result = run(gitleaks, ["dir", str(REPO_ROOT), "-c", str(config), "--redact",
                                 "--no-banner", "--exit-code", "1"], outside)
        if result.returncode != 0:
            problems.append("gitleaks reported a finding in the working tree (run `gitleaks dir` "
                             "yourself for details; contents are withheld here)")

    # --- untracked executable/script in the repository root only (T-05) --------------------
    for entry in REPO_ROOT.iterdir():
        if entry.is_file() and entry.suffix.lower() in EXEC_EXTENSIONS and not is_tracked(tracked, entry):
            problems.append(f"untracked executable/script in the repo root: {entry.name}")

    # --- a second devcontainer.json (root, or nested under .devcontainer/) -----------------
    root_devcontainer_json = REPO_ROOT / ".devcontainer.json"
    if root_devcontainer_json.exists():
        problems.append("root .devcontainer.json is not allowed: .devcontainer.json")
    devcontainer_dir = REPO_ROOT / ".devcontainer"
    if devcontainer_dir.is_dir():
        for sub in devcontainer_dir.rglob("devcontainer.json"):
            if sub.parent != devcontainer_dir:
                problems.append(f"nested devcontainer.json is not allowed: {sub.relative_to(REPO_ROOT)}")

    # --- a second .git, or a non-root global.json -------------------------------------------
    for sub in REPO_ROOT.rglob(".git"):
        if sub != REPO_ROOT / ".git":
            problems.append(f"nested .git is not allowed: {sub.relative_to(REPO_ROOT)}")
    for sub in REPO_ROOT.rglob("global.json"):
        if sub != REPO_ROOT / "global.json":
            problems.append(f"non-root global.json is not allowed: {sub.relative_to(REPO_ROOT)}")

    # --- untracked symlinks / reparse points (G4-18) ----------------------------------------
    for path in walk(REPO_ROOT, include_dirs=True):
        try:
            if path.is_symlink() and not is_tracked(tracked, path):
                problems.append(f"untracked symlink or reparse point: {path.relative_to(REPO_ROOT)}")
        except OSError:
            continue

    if problems:
        for problem in problems:
            print(f"precheck: REFUSED: {problem}")
        print(f"precheck: {len(problems)} problem(s); the sandbox was not started")
        return 1

    print("precheck: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
