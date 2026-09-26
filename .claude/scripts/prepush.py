#!/usr/bin/env python3
"""Pre-push check: CI's fast checks on the commits being pushed (#59; threat model
docs/security/threat-models/pre-push-ci-parity.md, G4-59-01 to 12).

A GUARDRAIL, not a gate: `git push --no-verify` skips it, and CI stays authoritative. Everything
here also runs in CI (G4-59-01). It exists so a failure shows up in seconds on the host instead
of minutes later in CI.

Run by pre-commit at the pre-push stage (`pre-commit install` sets it up; see
docs/runbooks/issue-pipeline.md), or by hand:  python .claude/scripts/prepush.py

What it checks, on <base>..<to_ref> where base = merge-base(origin/main, to_ref) (G4-59-06):
  0. when the pushed ref is HEAD: no uncommitted tracked changes, since the steps below read
     the working tree (G4-59-05, G6-59-01);
  1. commit messages with the commitlint mirror (CI's rules);
  2. .claude lint and unit tests (incl. the gitleaks, commitlint and CPM parity tests);
  3. a WARNING (never a block) for package and build-logic changes (G4-59-08 to 11);
  4. `dotnet build -warnaserror` and `dotnet test --no-build` with exactly CI's unit filters,
     skipped when every changed file matches CI's `changes` ignore regex (G4-59-04), and only
     when the pushed ref is HEAD (a non-HEAD push reports it as not run).
The secret scan runs as its own pre-push hook (gitleaks, same pinned version, --redact).
The restore inside step 4 is also the NuGet audit (NU1901-NU1904 are errors), which covers
G4-59-12 whenever step 4 runs; CI's audit and vulnerable-package step cover every push.
Exit 0 = pass (warnings allowed), 1 = a check failed, 2 = the range could not be determined.
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ZERO = re.compile(r"^0+$")

# Must equal ci.yml (test_prepush.py checks both): the unit-test step's filters and the
# `changes` job's ignore regex.
CI_UNIT_FILTERS = ["--filter-not-trait", "Category=Integration", "--filter-not-trait", "Category=E2E",
                   "--filter-not-trait", "Category=AppHost"]
CI_IGNORE = (r"^docs/|\.md$|^\.claude/|^\.vscode/|^LICENSE$"
             r"|^\.github/(ISSUE_TEMPLATE/|dependabot\.yml$|workflows/claude-review\.yml$)")

# G4-59-09: package and build-logic files (case-insensitive, any depth unless a path is given).
WARN_NAMES = {"directory.packages.props", "directory.build.props", "directory.build.targets",
              "directory.build.rsp", "directory.solution.props", "directory.solution.targets",
              "nuget.config", "global.json", "dotnet-tools.json", "package.json", "package-lock.json",
              ".npmrc", ".pre-commit-config.yaml", ".gitleaks.toml", "bannedsymbols.txt", ".globalconfig",
              ".editorconfig"}  # .editorconfig can switch analyzers off (invariant 4; G6-59-04)
PROJECT_SUFFIXES = (".csproj", ".fsproj", ".vbproj", ".esproj", ".proj", ".sln", ".slnx")
WARN_SUFFIXES = (".props", ".targets", ".rsp")
WARN_PREFIXES = (".github/workflows/",)
# G4-59-10: tokens on added or removed lines of *.csproj / *.slnx.
PROJECT_TOKENS = ("PackageReference", "PackageVersion", "VersionOverride", "PackageDownload",
                  "GlobalPackageReference", "Import", "Sdk=", "<Sdk", "Exec", "UsingTask", "<Target",
                  "RestoreSources", "RestoreAdditionalProjectSources", "ManagePackageVersionsCentrally",
                  "CentralPackage", "NuGetAudit", "TreatWarningsAsErrors", "WarningsNotAsErrors", "NoWarn")


class RangeError(Exception):
    pass


def git(*args: str, cwd: Path | None = None) -> str:
    """Run git with an argv list (no shell); raise on failure."""
    r = subprocess.run(["git", *args], cwd=cwd or ROOT, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", check=False)
    if r.returncode != 0:
        raise RangeError(f"git {args[0]} failed")
    return r.stdout


def safe(text: str) -> str:
    """G4-59-11: control characters escaped, so a file name cannot hide or fake output lines."""
    return "".join(ch if ch.isprintable() else f"\\x{ord(ch):02x}" for ch in text)


def resolve_range(env: dict[str, str], cwd: Path | None = None) -> tuple[str, str, bool] | None:
    """(base, to_ref, to_ref_is_HEAD), or None for a branch deletion. Fails closed (RangeError)."""
    to_ref = env.get("PRE_COMMIT_TO_REF") or "HEAD"
    if ZERO.match(to_ref):
        return None  # deleting a remote branch: nothing to check
    try:
        git("rev-parse", "--verify", "--quiet", "origin/main^{commit}", cwd=cwd)
    except RangeError:
        raise RangeError("origin/main is unknown here: run `git fetch origin` and push again") from None
    to_sha = git("rev-parse", "--verify", f"{to_ref}^{{commit}}", cwd=cwd).strip()
    base = git("merge-base", "origin/main", to_sha, cwd=cwd).strip()
    head = git("rev-parse", "HEAD", cwd=cwd).strip()
    return base, to_sha, to_sha == head


def changed_files(base: str, to_sha: str, cwd: Path | None = None) -> list[str]:
    """--no-renames: deletions and both sides of a rename count (G4-59-09)."""
    out = git("diff", "--name-only", "--no-renames", "-z", f"{base}..{to_sha}", cwd=cwd)
    return [p for p in out.split("\0") if p]


def warnings_for(files: list[str], base: str, to_sha: str, cwd: Path | None = None) -> list[tuple[str, str]]:
    """(path, reason) for every package or build-logic change (G4-59-09, 10)."""
    found = []
    for path in files:
        lower = path.lower()
        name = lower.rsplit("/", 1)[-1]
        if name in WARN_NAMES or lower.endswith(WARN_SUFFIXES) or lower.startswith(WARN_PREFIXES):
            found.append((path, "package or build-logic file"))
        elif lower.endswith(PROJECT_SUFFIXES):
            diff = git("diff", "--no-renames", "-U0", f"{base}..{to_sha}", "--", path, cwd=cwd)
            lines = [ln[1:] for ln in diff.splitlines() if ln[:1] in "+-" and not ln.startswith(("+++", "---"))]
            hits = sorted({t for t in PROJECT_TOKENS for ln in lines if t.lower() in ln.lower()})  # MSBuild names are case-insensitive (G6-59-03)
            if hits:
                found.append((path, "changes " + ", ".join(hits)))
    return found


def needs_dotnet(files: list[str]) -> bool:
    ignore = re.compile(CI_IGNORE)
    return any(not ignore.search(p) for p in files)


def run_step(title: str, argv: list[str]) -> bool:
    print(f"prepush: {title}", flush=True)
    return subprocess.run(argv, cwd=ROOT, check=False).returncode == 0


def main(env: dict[str, str] | None = None, run_dotnet: bool = True) -> int:
    env = dict(os.environ) if env is None else env
    try:
        resolved = resolve_range(env)
    except RangeError as exc:
        print(f"prepush: cannot determine the pushed range: {exc}", file=sys.stderr)
        return 2
    if resolved is None:
        print("prepush: branch deletion, nothing to check")
        return 0
    base, to_sha, is_head = resolved
    files = changed_files(base, to_sha)
    failed: list[str] = []
    dirty = False
    if is_head:  # the lint, tests and build below read the working tree (G4-59-05, G6-59-01)
        if git("status", "--porcelain", "--untracked-files=no").strip():
            dirty = True
            print("prepush: uncommitted tracked changes: the checks would read files that are not pushed.\n"
                  "  Commit or `git stash` them, then push again.", file=sys.stderr)
            failed.append("uncommitted tracked changes")
        elif git("status", "--porcelain", "--untracked-files=normal").strip():
            print("prepush: note: untracked files present; they are not pushed but may affect the build")

    if not run_step("commit messages", [sys.executable, str(ROOT / ".claude/scripts/commitlint.py"),
                                         "--range", f"{base}..{to_sha}"]):
        failed.append("commit messages (CI's commitlint rules)")
    if not dirty:
        if not run_step("Claude config lint", [sys.executable, str(ROOT / ".claude/scripts/lint.py")]):
            failed.append("lint.py")
        if not run_step(".claude unit tests", [sys.executable, "-m", "unittest", "discover", "-q",
                                               "-s", str(ROOT / ".claude/tests"), "-p", "test_*.py"]):
            failed.append(".claude unit tests")

    warn = warnings_for(files, base, to_sha)
    if warn:
        print("\nprepush: WARNING: package or build-logic changes in the pushed commits:")
        for path, reason in warn:
            print(f"  - {safe(path)}  ({reason})")
        print("  The host build already ran them (ADR-0011); this cannot undo that. Review `git diff` of\n"
              "  these files; after a package change the restore audit (NU1901-NU1904) must be clean,\n"
              "  and `dotnet list package --vulnerable --include-transitive` gives the detail.\n"
              "  Copy this list into the PR body (G4-59-08).\n")

    if not needs_dotnet(files):
        print("prepush: dotnet build and tests skipped: every change matches CI's ignore list")
    elif not is_head:
        print("prepush: dotnet build and tests NOT RUN: the pushed ref is not HEAD (check it out to test it)")
    elif dirty:
        print("prepush: dotnet build and tests NOT RUN: uncommitted tracked changes (see above)")
    elif run_dotnet:
        if not run_step("dotnet build -warnaserror", ["dotnet", "build", "-warnaserror"]):
            failed.append("dotnet build -warnaserror")
        elif not run_step("dotnet test (CI's unit filters)", ["dotnet", "test", "--no-build", *CI_UNIT_FILTERS]):
            failed.append("dotnet test (CI's unit filters)")

    if failed:
        print("\nprepush: FAILED: " + "; ".join(failed) + "\n  (a guardrail: CI runs the same checks)", file=sys.stderr)
        return 1
    print("prepush: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
