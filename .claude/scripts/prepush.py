#!/usr/bin/env python3
"""Pre-push check: CI's fast checks on the commits being pushed.

A GUARDRAIL, not a gate: `git push --no-verify` skips it, and CI stays authoritative. Everything
here also runs in CI. It exists so a failure shows up in seconds on the host instead
of minutes later in CI.

Run by pre-commit at the pre-push stage (`pre-commit install` sets it up), or by hand:  python .claude/scripts/prepush.py

What it checks, on <base>..<to_ref> where base = merge-base(origin/main, to_ref):
  0. when the pushed ref is HEAD: no uncommitted tracked changes, since the steps below read
     the working tree;
  1. commit messages with the project's commit-message check (CI's rules);
  2. the lock check (`lock.py verify`: the locked core files are the release's), the settings floor
     (`floors.py`), then .claude lint and the project's own .claude unit tests when it has .claude/tests/ (a project may keep its gitleaks parity check in its
     repository tests, which step 4 runs);
  3. a WARNING (never a block) for package and build-logic changes;
  4. the build and test steps of the installed packs (pack data `prepush_steps`, for example the dotnet pack's
     `dotnet build -warnaserror` and `dotnet test --no-build` with exactly CI's unit filters), in pack order, each
     pack stopping at its first failing step; on every push when the project's CI has no ignore list (CI_IGNORE
     then matches nothing). Only when the pushed ref is HEAD (a non-HEAD push reports them as not run).
The secret scan runs as its own pre-push hook (gitleaks, same pinned version, --redact).
Exit 0 = pass (warnings allowed), 1 = a check failed, 2 = the range could not be determined or
.claude/project.json cannot be used.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import importlib.util
import os
import re
import subprocess
import sys
from pathlib import Path

SELF = Path(__file__).resolve()
ROOT = SELF.parents[2]
PROJECT_FILE = ROOT / ".claude" / "project.json"  # fixed at import, never from a patched ROOT
ZERO = re.compile(r"^0+$")


def _load(name: str, path: Path):
    """Import a sibling module by absolute path, not by sys.path name."""
    module = sys.modules.get(name)
    if module is not None and Path(getattr(module, "__file__", "")).resolve() == path:
        return module
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    # the source file itself, never bytecode from a __pycache__ folder: a planted .pyc under any name the file
    # system maps to that folder would otherwise run in place of the signed source
    exec(compile(Path(path).read_bytes(), str(path), "exec"), module.__dict__)  # noqa: S102
    return module


claudecfg = _load("_claudecfg", SELF.parent / "_claudecfg.py")
try:
    PROJECT = claudecfg.load_project(PROJECT_FILE)
    # Every key a step names must be set, whatever the pack data says is required.
    for _pack, _steps in PROJECT["pack_prepush_steps"].items():
        _named = {m.group(1) for s in _steps for w in s["argv"] if (m := claudecfg.STEP_PLACEHOLDER.fullmatch(w))}
        if not _named <= set(PROJECT["packs"][_pack]):
            raise claudecfg.ProjectConfigError("missing-key", "is missing a key that a pre-push step of an "
                                                              "installed pack needs")
except claudecfg.ProjectConfigError as _exc:
    if __name__ != "__main__":
        raise
    print(f"prepush: {_exc}", file=sys.stderr)
    sys.exit(2)

# {pack: [{"title", "argv"}]}: the build and test steps of the installed packs, validated by the loader.
STEPS = PROJECT["pack_prepush_steps"]
COMMIT_MESSAGE_CHECK = PROJECT["prepush"]["commit_message_check"]
# Must equal the project's CI: the `changes` job's ignore regexes (a pack's step arguments, such as unit filters,
# come from packs.<name> in project.json, which CI reads too).
# An empty list ignores nothing: CI builds and tests on every change.
CI_IGNORE = "|".join(PROJECT["prepush"]["ignore_paths"]) or r"(?!)"
CI_IGNORE_MD = "|".join(PROJECT["prepush"]["ignore_markdown"]) or r"(?!)"
# A `.claude/` directory at any depth is executable config; ci.yml greps the same pattern.
CLAUDE_AREA = re.compile(r"(^|/)\.claude/")

# package and build-logic files (case-insensitive, any depth unless a path is given).
WARN_NAMES = {"directory.packages.props", "directory.build.props", "directory.build.targets",
              "directory.build.rsp", "directory.solution.props", "directory.solution.targets",
              "nuget.config", "global.json", "dotnet-tools.json", "packages.lock.json", "package.json", "package-lock.json",
              ".npmrc", ".pre-commit-config.yaml", ".gitleaks.toml", "bannedsymbols.txt", ".globalconfig",
              ".editorconfig"}  # .editorconfig can switch analyzers off
PROJECT_SUFFIXES = (".csproj", ".fsproj", ".vbproj", ".esproj", ".proj", ".sln", ".slnx")
WARN_SUFFIXES = (".props", ".targets", ".rsp")
WARN_PREFIXES = (".github/workflows/", ".github/scripts/", ".github/image-scan/", ".github/release/")
# tokens on added or removed lines of *.csproj / *.slnx.
PROJECT_TOKENS = ("PackageReference", "PackageVersion", "VersionOverride", "PackageDownload",
                  "GlobalPackageReference", "Import", "Sdk=", "<Sdk", "Exec", "UsingTask", "<Target",
                  "RestoreSources", "RestoreAdditionalProjectSources", "ManagePackageVersionsCentrally",
                  "CentralPackage", "NuGetAudit", "TreatWarningsAsErrors", "WarningsNotAsErrors", "NoWarn",
                  "RestoreLockedMode", "RestorePackagesWithLockFile", "RuntimeIdentifier")


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
    """Control characters escaped, so a file name cannot hide or fake output lines."""
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
    """--no-renames: deletions and both sides of a rename count."""
    out = git("diff", "--name-only", "--no-renames", "-z", f"{base}..{to_sha}", cwd=cwd)
    return [p for p in out.split("\0") if p]


def warnings_for(files: list[str], base: str, to_sha: str, cwd: Path | None = None) -> list[tuple[str, str]]:
    """(path, reason) for every package or build-logic change."""
    found = []
    for path in files:
        lower = path.lower()
        name = lower.rsplit("/", 1)[-1]
        if name in WARN_NAMES or lower.endswith(WARN_SUFFIXES) or lower.startswith(WARN_PREFIXES):
            found.append((path, "package or build-logic file"))
        elif lower.endswith(PROJECT_SUFFIXES):
            diff = git("diff", "--no-renames", "-U0", f"{base}..{to_sha}", "--", path, cwd=cwd)
            lines = [ln[1:] for ln in diff.splitlines() if ln[:1] in "+-" and not ln.startswith(("+++", "---"))]
            hits = sorted({t for t in PROJECT_TOKENS for ln in lines if t.lower() in ln.lower()})  # MSBuild names are case-insensitive
            if hits:
                found.append((path, "changes " + ", ".join(hits)))
    return found


def step_argv(pack: str, argv: list[str]) -> list[str]:
    """The arguments of one step with each placeholder replaced by the value of packs.<pack>.<key>: `{key}` gives one
    argument, `{key...}` one argument per item. Values were checked against the pack's key patterns."""
    config = PROJECT["packs"][pack]
    out: list[str] = []
    for word in argv:
        m = claudecfg.STEP_PLACEHOLDER.fullmatch(word)
        if m is None:
            out.append(word)
        elif m.group(2):
            out.extend(config[m.group(1)])
        else:
            out.append(config[m.group(1)])
    return out


def needs_build(files: list[str]) -> bool:
    """CI's `lanes` decision (ci.yml): everything under .claude/ counts; elsewhere the ignore list and
    Markdown are skipped. With an empty ignore list nothing is skipped, as in CI."""
    ignore, markdown = re.compile(CI_IGNORE), re.compile(CI_IGNORE_MD)
    return any(CLAUDE_AREA.search(p) or not (ignore.search(p) or markdown.search(p)) for p in files)


def run_step(title: str, argv: list[str], extra_env: dict[str, str] | None = None) -> bool:
    print(f"prepush: {title}", flush=True)
    env = {**os.environ, **extra_env} if extra_env else None
    return subprocess.run(argv, cwd=ROOT, check=False, env=env).returncode == 0


def pack_env(pack: str) -> dict[str, str]:
    """The pack's `prepush_env` variable, when the project sets the key that names it (as the project's CI does)."""
    spec = PROJECT["pack_prepush_env"].get(pack)
    if spec is None:
        return {}
    value: object = PROJECT["packs"][pack]
    for part in spec["name_from"].split("."):
        value = value.get(part) if isinstance(value, dict) else None
    return {value: spec["value"]} if isinstance(value, str) else {}


def pack_steps(pack: str) -> list[dict]:
    """The pack's steps that apply to this project: a step with `only_with` runs only when that key is set."""
    config = PROJECT["packs"][pack]
    return [s for s in STEPS[pack] if "only_with" not in s or s["only_with"].split(".")[0] in config]


def main(env: dict[str, str] | None = None, run_steps: bool = True) -> int:
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
    if is_head:  # the lint, tests and build below read the working tree
        if git("status", "--porcelain", "--untracked-files=no").strip():
            dirty = True
            print("prepush: uncommitted tracked changes: the checks would read files that are not pushed.\n"
                  "  Commit or `git stash` them, then push again.", file=sys.stderr)
            failed.append("uncommitted tracked changes")
        elif git("status", "--porcelain", "--untracked-files=normal").strip():
            print("prepush: note: untracked files present; they are not pushed but may affect the build")

    if not run_step("commit messages", [sys.executable, "-I", str(ROOT / COMMIT_MESSAGE_CHECK),
                                         "--range", f"{base}..{to_sha}"]):
        failed.append("commit messages (CI's commitlint rules)")
    if not dirty:
        if not run_step("lock check", [sys.executable, "-I", str(ROOT / ".claude/scripts/lock.py"), "verify",
                                       "--from-git", to_sha]):
            failed.append("lock.py verify")
        if not run_step("settings floor", [sys.executable, "-I", str(ROOT / ".claude/scripts/floors.py")]):
            failed.append("floors.py")
        if not run_step("Claude config lint", [sys.executable, "-I", str(ROOT / ".claude/scripts/lint.py")]):
            failed.append("lint.py")
        if not (ROOT / ".claude/tests").is_dir():  # the project's own tests of its .claude files; the release has none
            print("prepush: .claude unit tests: none in this project (.claude/tests/ does not exist), skipped")
        elif not run_step(".claude unit tests", [sys.executable, "-I", "-m", "unittest", "discover", "-q",
                                                 "-s", str(ROOT / ".claude/tests"), "-p", "test_*.py"]):
            failed.append(".claude unit tests")

    warn = warnings_for(files, base, to_sha)
    if warn:
        print("\nprepush: WARNING: package or build-logic changes in the pushed commits:")
        for path, reason in warn:
            print(f"  - {safe(path)}  ({reason})")
        print("  A local build may already have run them; this cannot undo that. Review `git diff` of\n"
              "  these files, check the package audit of your stack (see the pack's documentation),\n"
              "  and copy this list into the PR body.\n")

    if not STEPS:
        print("prepush: build and test steps skipped: no installed pack declares any")
    elif not needs_build(files):
        print("prepush: build and test steps skipped: every change matches CI's ignore list")
    elif not is_head:
        print("prepush: build and test steps NOT RUN: the pushed ref is not HEAD (check it out to test it)")
    elif dirty:
        print("prepush: build and test steps NOT RUN: uncommitted tracked changes (see above)")
    elif run_steps:
        for pack in STEPS:
            extra_env = pack_env(pack)
            for step in pack_steps(pack):
                if not run_step(step["title"], step_argv(pack, step["argv"]), extra_env):
                    failed.append(step["title"])
                    break  # a later step of the same pack depends on this one (a test after its build)

    if failed:
        print("\nprepush: FAILED: " + "; ".join(failed) + "\n  (a guardrail: CI runs the same checks)", file=sys.stderr)
        return 1
    print("prepush: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
