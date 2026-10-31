#!/usr/bin/env python3
"""Bootstrap a project from a Norma release bundle: write the project layer, install the release, run the checks.

First install (the project has no Norma lock yet: a new project, or one whose older copies were removed from
.claude/hooks/ and .claude/scripts/):
  1. run the first-install check from the release notice (Norma's consumer guide has it for bash and PowerShell) in an
     empty folder outside the project. With system tools only, it checks the bundle's SHA-256 against the notice, the
     tag signature with `ssh-keygen -Y verify` against the notice's signer line, the manifest against the signed tag,
     and this script, lock.py and base-settings.json against the manifest, and leaves the files in norma-check/.
     Never run this script from a bundle that has not passed that check;
  2. from the project root:
       python -I <tmp>/norma-check/core/.claude/scripts/bootstrap.py --bundle <tmp>/norma-vX.tar --tag vX --packs dotnet
              --signer-line 'norma-release namespaces="git" ssh-ed25519 AAAA...' --visibility private|public
              --approver '<name>' [--approver '<name>' ...] --project-id GL-NNNN
     (single quotes, in bash and in PowerShell: the signer line holds namespaces="git", which double quotes break)
              [--pack-settings FILE] [--rulesets] [--dependabot] [--bot-branch dependabot/ --bot-login 'dependabot[bot]'] [--python python3]
              [--root DIR] [--adopt [--plan]]
It writes only the files that are missing (it never replaces one), so after fixing a value it can run again:
  .claude/project.json      schema 1 with the repository's visibility (GitHub Pro is the baseline; a private
                            repository gets no setting for a feature Pro does not include there), the people who may
                            approve a skipped gate (gates.approvers, from --approver; needed only when this file is
                            written), the declared packs,
                            the rulesets and Dependabot patterns in gates.review_required_paths when the repository has
                            those files or the flags say it will, the dependency bot branches given with --bot-branch
                            (none by default), and starting values the project reviews (ci_tooling_paths,
                            dependency_paths, templates), and the usage capture's folder (.local.usage) with the
                            tracking project's id (usage.project_id, from --project-id; needed only when this file is
                            written)
  .claude/boundaries.json   documentation lanes for the core agents, no Docker, the core files and the usage capture's
                            folder denied to every agent,
                            G4 routed to the tests lane until the project adds its own implementer agents
  .claude/settings.json     the base deny floor and the core hooks in exec form with onFailure "block"
  .github/allowed_signers   the release signer line (exactly one line for norma-release, namespaces="git")
  docs/ai/README.md         the roster markers;  CLAUDE.md  a stub for the project's own rules
With --adopt, a project's existing .claude/settings.json and .claude/boundaries.json gain what they lack, and nothing
else changes: the base deny entries and the two core hook registrations in settings.json; the installed packs'
build-logic globs and the usage capture's folder in the deny list of boundaries.json. It only adds, at the end of each
list, and never removes, reorders or replaces an entry. It prints every addition before writing, and writes nothing at
all when a file cannot be merged cleanly (it is not JSON, repeats a key, has another shape, or registers a core hook
another way). --plan prints the additions and stops, with nothing written and nothing installed.
Then it runs `lock.py write` from the bundle (which checks the signature, the manifest and every file before
installing anything), renders the roster, and runs lint.py, floors.py and `prereqs.py interpreter` in the project.
Exit 0 = installed and every check passed; 1 = a check failed; 2 = an input cannot be used or the install refused.
The --pack-settings file is a JSON object {pack: {key: value}} for the keys a pack needs (for the dotnet pack:
solution, unit_test_filters); this script names no project and invents no project fact.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import argparse
import contextlib
import hashlib
import json
import math
import os
import re
import subprocess
import types
from pathlib import Path

SELF = Path(__file__).absolute()  # never resolved: a link must not lead to another folder's files
BASE_SETTINGS_REL = ".claude/base-settings.json"
SIGNERS_FILE = ".github/allowed_signers"
INTERPRETERS = ("python", "python3")
VISIBILITIES = ("public", "private")  # GitHub Pro is the baseline; see .claude/github-features.json
# The loader's rule for gates.approvers (_claudecfg._APPROVER), copied because bootstrap runs before the loader is
# installed; a test keeps the two equal. "pending" is the placeholder gates.py init writes, never a name.
APPROVER = re.compile(r"[^\W\d_]+(?:[ .'-]{1,2}[^\W\d_]+)*\.?")
APPROVERS_MAX = 10
# The loader's rule for usage.project_id (_claudecfg._PROJECT_ID), copied for the same reason; the usage capture's
# folder (ADR 0006) is the same for every new project, and no listed agent writes there
PROJECT_ID = re.compile(r"GL-[0-9]{4}")
CAPTURE_FOLDER = ".local.usage"
CHECK_TIMEOUT = 600
CI_TOOLING = ["^\\.github/", "^\\.pre-commit-config\\.yaml$"]
DEPENDENCY_PATHS = {  # starting values per pack; the project reviews them
    "dotnet": ["(^|/)Directory\\.Packages\\.props$", "(^|/)packages\\.lock\\.json$"],
    "npm": ["(^|/)package\\.json$", "(^|/)package-lock\\.json$"],
    "python": ["(^|/)pyproject\\.toml$", "(^|/)uv\\.lock$"],
}
DEPENDENCY_FALLBACK = ["(^|/)requirements\\.txt$"]
# Files that steer what a pack's build and test commands load or run (pytest plugins and conftest.py, mypy plugins,
# uv's package sources and interpreter): no listed agent writes them, so an agent granted those commands cannot plant
# code through them. Added to boundaries.json `deny` when the pack is installed; the project reviews them.
BUILD_LOGIC = {
    # project, solution and shared-project files and their imports run MSBuild targets, including a project's .user
    # file, which MSBuild imports on its own and .gitignore usually hides; obj/ holds the restore's generated imports
    # and assets, imported by wildcard; bin/ is where dotnet test loads test adapters from, and a file planted there
    # survives the next build; runsettings load test adapters, nuget.config redirects package sources, and global.json
    # can point the dotnet host at an SDK folder of the repository (sdk.paths)
    "dotnet": ["*.csproj", "**/*.csproj", "*.fsproj", "**/*.fsproj", "*.vbproj", "**/*.vbproj", "*.proj", "**/*.proj",
               "*.user", "**/*.user", "*.projitems", "**/*.projitems", "*.shproj", "**/*.shproj",
               "*.sln", "**/*.sln", "*.slnx", "**/*.slnx", "*.slnf", "**/*.slnf", "obj/**", "**/obj/**",
               "bin/**", "**/bin/**",
               "*.props", "**/*.props", "*.targets", "**/*.targets", "Directory.Build.*", "**/Directory.Build.*",
               "*.runsettings", "**/*.runsettings", "nuget.config", "**/nuget.config", "global.json", "**/global.json"],
    # configuration that loads plugins or redirects packages, startup modules that every Python tool imports, and
    # `pip` in the working folder, which shadows the pip that pip-audit runs as `python -m pip`
    "python": ["pyproject.toml", "**/pyproject.toml", "uv.lock", "**/uv.lock", "uv.toml", "**/uv.toml",
               "conftest.py", "**/conftest.py", "pytest.ini", "**/pytest.ini", ".pytest.ini", "**/.pytest.ini",
               "pytest.toml", "**/pytest.toml", ".pytest.toml", "**/.pytest.toml", "tox.ini", "**/tox.ini",
               "mypy.ini", "**/mypy.ini", ".mypy.ini", "**/.mypy.ini", "setup.cfg", "**/setup.cfg",
               "ruff.toml", "**/ruff.toml", ".ruff.toml", "**/.ruff.toml", ".python-version", "**/.python-version",
               "sitecustomize.py", "**/sitecustomize.py", "usercustomize.py", "**/usercustomize.py",
               "pip.py", "pip/**", ".venv/**", "**/.venv/**"],
    # npm test runs the test script package.json names, with the settings .npmrc gives (script-shell, for example)
    # and the test runners' configuration, which loads code, and node_modules, which npm puts on PATH
    "npm": ["package.json", "**/package.json", ".npmrc", "**/.npmrc", "node_modules/**", "**/node_modules/**",
            "jest.config.*", "**/jest.config.*", "vitest.config.*", "**/vitest.config.*", ".mocharc.*", "**/.mocharc.*",
            "babel.config.*", "**/babel.config.*", ".babelrc", "**/.babelrc", ".babelrc.*", "**/.babelrc.*"],
}
RULESETS = "\\.github/rulesets/.+\\.json"
DEPENDABOT = "\\.github/dependabot\\.yml"
CORE_AGENT_LANES = {
    "architect": ["docs/architecture/**", "docs/adr/**"],
    "product-owner": ["docs/requirements/**"],
    "security-reviewer": ["docs/security/**"],
    "tech-writer": ["README.md", "docs/runbooks/**"],
    "test-engineer": ["tests/**"],
}
ROSTER_PAGE = "# Agents\n\n<!-- roster:begin generated -->\n<!-- roster:end -->\n"
CLAUDE_STUB = ("# Working on this project\n\nThe shared rules come from Norma (`.claude/rules/core.md`, locked). "
               "Add this project's own rules here.\n")


class BootstrapError(Exception):
    pass


def _load(name: str, path: Path):
    """Run the source file itself: never bytecode from a __pycache__ folder next to it, which the first-install check
    does not cover."""
    module = types.ModuleType(name)
    module.__file__ = str(path)
    sys.modules[name] = module
    exec(compile(path.read_bytes(), str(path), "exec"), module.__dict__)  # noqa: S102 - the checked lock.py
    return module


lock = _load("norma_lock_bootstrap", SELF.parent / "lock.py")


def checked_release(bundle: Path, tag: str, packs: list[str], signers_text: str) -> dict:
    """Check the bundle before any project file is written: its signature against the signer line, its tag, its
    manifest against the signed tag. Returns the base settings from the bundle itself, checked against the manifest,
    so no file next to this script can change what is written."""
    try:
        raw_tag, members = lock.read_bundle(Path(bundle), packs)
        _, tag_name, message_tag, manifest_hash, _ = lock.verify_signature(raw_tag, signers_text)
        if tag_name != tag or message_tag != tag:
            raise lock.LockError("tag", "the bundle's tag is not the tag named with --tag")
        manifest_raw = members.get(lock.MANIFEST_REL)
        if manifest_raw is None or hashlib.sha256(lock.normalised(manifest_raw, lock.MANIFEST_REL)).hexdigest() != manifest_hash:
            raise lock.LockError("manifest", "the manifest in the bundle does not match the hash signed in the tag")
        manifest = lock.parse_manifest(lock.normalised(manifest_raw, lock.MANIFEST_REL))
        base_raw = members.get(BASE_SETTINGS_REL)
        if base_raw is None or lock.file_hash(base_raw, BASE_SETTINGS_REL) != manifest["units"]["core"]["files"].get(BASE_SETTINGS_REL):
            raise lock.LockError("manifest", "the base settings in the bundle differ from the manifest")
    except lock.LockError as exc:
        raise BootstrapError(f"the bundle was refused before anything was written: {exc}") from None
    return json.loads(base_raw.decode("utf-8"))


def hook_entry(spec: dict, python: str) -> dict:
    """The registration of one core hook, in exec form, as floors.py wants it."""
    return {"matcher": spec["matcher"],
            "hooks": [{"type": "command", "command": python, "args": ["-I", "${CLAUDE_PROJECT_DIR}/" + spec["script"]],
                       "timeout": spec["timeout"], "onFailure": "block"}]}


def project_layer(packs: dict, rulesets: bool, dependabot: bool, python: str, signer_line: str | None,
                  base: dict, visibility: str = "private", bots: tuple[tuple[str, str], ...] = (),
                  approvers: tuple[str, ...] = (), project_id: str = "") -> dict[str, str]:
    """{project path: text} of the files bootstrap may write; `base` is the checked base settings of the release."""
    dependency = [p for name in sorted(packs) for p in DEPENDENCY_PATHS.get(name, [])] or DEPENDENCY_FALLBACK
    project = {
        "schema": 1,
        "repository": {"visibility": visibility},
        "packs": packs,
        "agent_boundaries": {"cd_targets": []},
        "secret_guard": {"blocked_folders": []},
        "gates": {"approvers": list(approvers), "ci_tooling_paths": CI_TOOLING, "dependency_paths": dependency,
                  "review_required_paths": [p for p, on in ((RULESETS, rulesets), (DEPENDABOT, dependabot)) if on],
                  "docs_line_from": 0,
                  "dependency_bot_branches": [{"branch": b, "login": login} for b, login in bots]},
        "prepush": {"commit_message_check": ".claude/scripts/commitlint.py", "ignore_paths": [], "ignore_markdown": []},
        "prereqs": {},
        "lock": {"signers_file": SIGNERS_FILE},
        "templates": {"adr_tags": ["security", "tooling"], "runbook_tools": ["CLI"]},
        "usage": {"capture_folder": CAPTURE_FOLDER, "project_id": project_id},
    }
    # a new project has no implementer agent yet: G4 starts with the tests lane, and the project adds its own owners
    deny = [".claude/**", ".agent-logs/**", f"{CAPTURE_FOLDER}/**",
            *(g for name in sorted(packs) for g in BUILD_LOGIC.get(name, []))]
    boundaries = {"deny": deny, "agents": CORE_AGENT_LANES, "docker": {},
                  "routing": {"G4": [{"agent": "test-engineer", "summary": "tests", "paths": "`tests/**`"}]}}
    hooks = [hook_entry(spec, python) for spec in base["hooks"]["PreToolUse"]]
    settings = {"permissions": {"deny": list(base["deny"])}, "hooks": {"PreToolUse": hooks}}
    files = {
        ".claude/project.json": json.dumps(project, indent=2) + "\n",
        ".claude/boundaries.json": json.dumps(boundaries, indent=2) + "\n",
        ".claude/settings.json": json.dumps(settings, indent=2) + "\n",
        "docs/ai/README.md": ROSTER_PAGE,
        "CLAUDE.md": CLAUDE_STUB,
    }
    if signer_line is not None:
        files[SIGNERS_FILE] = signer_line.strip() + "\n"
    return files


def write_missing(root: Path, files: dict[str, str]) -> list[tuple[str, bool]]:
    """Write each file that does not exist yet; an existing file is kept as it is. Returns (path, written)."""
    done = []
    for rel, text in files.items():
        target = root / rel
        if target.is_symlink():
            raise BootstrapError(f"{rel} is a link; bootstrap writes only regular files")
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            with open(target, "x", encoding="utf-8", newline="\n") as fh:
                fh.write(text)
            done.append((rel, True))
        except FileExistsError:
            done.append((rel, False))
    return done


SETTINGS_REL = ".claude/settings.json"
BOUNDARIES_REL = ".claude/boundaries.json"
_SAFE_FOLDER = re.compile(r"[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*")


def _adopt_error(rel: str, why: str) -> BootstrapError:
    """The messages name a key or a position, never a value from the project's file."""
    return BootstrapError(f"{rel} cannot be adopted: {why}; nothing was written. Merge it by hand as "
                          "docs/consumer-guide.md (\"Settings for a project that has its own\") shows, then run again")


def _strict_json(text: str, rel: str):
    def pairs(items: list[tuple[str, object]]) -> dict:
        if len({k for k, _ in items}) != len(items):
            raise _adopt_error(rel, "it repeats a key")
        return dict(items)

    def constant(name: str):
        raise _adopt_error(rel, "it holds NaN or Infinity")

    def finite(number: str) -> float:  # 1e400 would come back as Infinity, which a strict reader refuses
        value = float(number)
        if not math.isfinite(value):
            raise _adopt_error(rel, "it holds a number too large to be finite")
        return value

    try:
        return json.loads(text, object_pairs_hook=pairs, parse_constant=constant, parse_float=finite)
    except (ValueError, RecursionError):
        raise _adopt_error(rel, "it is not valid JSON") from None


def _string_list(container: dict, key: str, rel: str, where: str) -> list:
    value = container.setdefault(key, [])
    if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
        raise _adopt_error(rel, f"{where} is not a list of strings")
    return value


def _names_script(entry: object, script: str) -> bool:
    text = json.dumps(entry).lower().replace("\\\\", "/")  # a path written with backslashes names the hook too
    return script.lower() in text


def _registered(entry: object, spec: dict, interpreters: list[str]) -> bool:
    """Whether a hook entry is the core registration exactly, with any of the floor's interpreters."""
    if not isinstance(entry, dict) or not isinstance(entry.get("hooks"), list) or len(entry["hooks"]) != 1:
        return False
    hook = entry["hooks"][0]
    return isinstance(hook, dict) and hook.get("command") in interpreters \
        and entry == hook_entry(spec, hook["command"])


def adopt_settings(text: str, base: dict, python: str) -> tuple[dict, list[str]]:
    """settings.json with the base deny entries and the core hook registrations it lacks; (data, additions)."""
    rel = SETTINGS_REL
    data = _strict_json(text, rel)
    if not isinstance(data, dict):
        raise _adopt_error(rel, "the top level is not an object")
    permissions = data.setdefault("permissions", {})
    if not isinstance(permissions, dict):
        raise _adopt_error(rel, "permissions is not an object")
    deny = _string_list(permissions, "deny", rel, "permissions.deny")
    added = []
    for entry in base["deny"]:
        if entry not in deny:
            deny.append(entry)
            added.append(f"permissions.deny: {entry}")
    hooks = data.setdefault("hooks", {})
    if not isinstance(hooks, dict) or not all(isinstance(v, list) for v in hooks.values()):
        raise _adopt_error(rel, "hooks is not an object of lists")
    pre = hooks.setdefault("PreToolUse", [])
    for spec in base["hooks"]["PreToolUse"]:
        naming = [(event, i) for event, entries in hooks.items() for i, entry in enumerate(entries)
                  if _names_script(entry, spec["script"])]
        if not naming:
            pre.append(hook_entry(spec, python))
            added.append(f"hooks.PreToolUse: {spec['script']} ({python}, exec form, onFailure block)")
        elif len(naming) > 1 or naming[0][0] != "PreToolUse" or not _registered(pre[naming[0][1]], spec,
                                                                               base["interpreters"]):
            event, i = naming[0]
            where = f"hooks.{event}[{i}]" if re.fullmatch(r"[A-Za-z]{1,40}", event) \
                else f"entry {i} of hook event {list(hooks).index(event) + 1}"  # a key is printed only if plain
            raise _adopt_error(rel, f"{where} registers {spec['script']} another way")
    return data, added


def adopt_boundaries(text: str, packs: list[str], capture_folder: str) -> tuple[dict, list[str]]:
    """boundaries.json with the capture folder and the packs' build-logic globs it lacks in deny; (data, additions).
    Compared without case, as the hook compares them."""
    rel = BOUNDARIES_REL
    data = _strict_json(text, rel)
    if not isinstance(data, dict):
        raise _adopt_error(rel, "the top level is not an object")
    deny = _string_list(data, "deny", rel, "deny")
    have = {glob.lower() for glob in deny}
    added = []
    for glob in [f"{capture_folder}/**", *(g for name in sorted(packs) for g in BUILD_LOGIC.get(name, []))]:
        if glob.lower() not in have:
            deny.append(glob)
            have.add(glob.lower())
            added.append(f"deny: {glob}")
    return data, added


def project_capture_folder(root: Path) -> str:
    """The capture folder an existing project.json names, when it names a usable one; the default otherwise (the
    loader then reports the project file itself)."""
    try:
        folder = json.loads((root / ".claude" / "project.json").read_text(encoding="utf-8-sig"))["usage"]["capture_folder"]
    except (OSError, ValueError, RecursionError, KeyError, TypeError):
        return CAPTURE_FOLDER
    return folder if isinstance(folder, str) and len(folder) <= 100 and _SAFE_FOLDER.fullmatch(folder) \
        and ".." not in folder.split("/") else CAPTURE_FOLDER


def adoption_plan(root: Path, packs: list[str], base: dict, python: str) -> dict[str, tuple[bytes, list[str]]]:
    """{path: (merged bytes, additions)} for each of settings.json and boundaries.json that exists. Every file is
    read, merged and encoded before anything is written, so one that cannot be merged stops them all."""
    plan = {}
    for rel in (SETTINGS_REL, BOUNDARIES_REL):
        target = root / rel
        if target.is_symlink():
            raise BootstrapError(f"{rel} is a link; bootstrap writes only regular files")
        if not target.is_file():
            continue
        if os.path.lexists(_adopt_temporary(target)):
            raise BootstrapError(f"{rel}{ADOPT_SUFFIX} is left from an earlier run that stopped; remove it and run "
                                 "again; nothing was written")
        try:
            text = target.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeDecodeError):
            raise _adopt_error(rel, "it cannot be read as UTF-8 text") from None
        if rel == SETTINGS_REL:
            data, added = adopt_settings(text, base, python)
        else:
            data, added = adopt_boundaries(text, packs, project_capture_folder(root))
        try:
            merged = (json.dumps(data, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")
        except (ValueError, UnicodeEncodeError):
            raise _adopt_error(rel, "it holds text or a number that cannot be written back as JSON") from None
        plan[rel] = (merged, added)
    return plan


ADOPT_SUFFIX = ".adopt-tmp"


def _adopt_temporary(target: Path) -> Path:
    return target.with_name(target.name + ADOPT_SUFFIX)


def write_adopted(root: Path, plan: dict[str, tuple[bytes, list[str]]]) -> None:
    """Replace each adopted file that gains an entry: every temporary file is written first, and only when all are
    written are the files replaced, so a failed write leaves both files as they were; a failed replace names what was
    replaced and the temporary files left."""
    written = []
    try:
        for rel, (merged, added) in plan.items():
            if added:
                temporary = _adopt_temporary(root / rel)
                with open(temporary, "xb") as fh:
                    written.append((temporary, root / rel))
                    fh.write(merged)
    except OSError:
        for temporary, _ in written:
            with contextlib.suppress(OSError):
                temporary.unlink()
        raise BootstrapError("the release is installed, but the adopted files could not be written and were left as "
                             "they were; run bootstrap with --adopt again") from None
    replaced = []
    for temporary, target in written:
        try:
            os.replace(temporary, target)
        except OSError:  # a target held open; what was replaced and what is left are named
            left = [t.relative_to(root).as_posix() for t, _ in written if t.exists()]
            raise BootstrapError(f"the release is installed, but {target.relative_to(root).as_posix()} could not be "
                                 f"replaced; adopted so far: {', '.join(replaced) or 'none'}. Remove "
                                 f"{', '.join(left)} and run bootstrap with --adopt again") from None
        replaced.append(target.relative_to(root).as_posix())


def run_checks(root: Path) -> bool:
    ok = True
    for title, argv in (("roster", [".claude/scripts/roster.py"]), ("lint", [".claude/scripts/lint.py"]),
                        ("floors", [".claude/scripts/floors.py", "--root", "."]),
                        ("interpreter", [".claude/scripts/prereqs.py", "interpreter"])):
        try:
            run = subprocess.run([sys.executable, "-I", "-B", *argv], cwd=root, capture_output=True, text=True,
                                 timeout=CHECK_TIMEOUT, check=False)
            passed, output = run.returncode == 0, run.stdout + run.stderr
        except subprocess.TimeoutExpired:
            passed, output = False, f"did not finish in {CHECK_TIMEOUT} s"
        print(f"bootstrap: {'PASS' if passed else 'FAIL'}  {title}")
        if not passed:
            print("  " + output.strip().replace("\n", "\n  "))
        ok = ok and passed
    return ok


def bootstrap(root: Path, bundle: Path, tag: str, packs: list[str], pack_settings: dict, signer_line: str | None,
              rulesets: bool, dependabot: bool, python: str, visibility: str = "private",
              bots: tuple[tuple[str, str], ...] = (), approvers: tuple[str, ...] = (), project_id: str = "",
              adopt: bool = False, plan_only: bool = False) -> bool | None:
    """True when installed and every check passed, False when a check failed, None after --plan."""
    root = Path(root).resolve()
    try:  # the lock check tells bytecode Python writes locally from a planted file by asking git
        lock.git(root, "rev-parse", "--is-inside-work-tree")
    except lock.LockError:
        raise BootstrapError("run bootstrap in the project's git repository (git init first)") from None
    if python not in INTERPRETERS:
        raise BootstrapError(f"--python must be one of {', '.join(INTERPRETERS)}")
    if visibility not in VISIBILITIES:
        raise BootstrapError("--visibility must be public or private")
    approvers = tuple(approvers)
    if not (root / ".claude" / "project.json").exists() and not approvers:
        raise BootstrapError("give --approver with the name of each person who may approve a skipped gate "
                             "(.claude/project.json gates.approvers)")
    if len(approvers) > APPROVERS_MAX or len({a.casefold() for a in approvers}) != len(approvers):
        raise BootstrapError(f"give 1 to {APPROVERS_MAX} different --approver names")
    for name in approvers:  # the message never repeats the name
        if len(name) > 60 or not APPROVER.fullmatch(name) or name.casefold() == "pending":
            raise BootstrapError("an --approver is not a person's name (letters joined by spaces or . ' -, at most 60)")
    if not (root / ".claude" / "project.json").exists() and not PROJECT_ID.fullmatch(project_id or ""):
        raise BootstrapError("give --project-id with the tracking project's id, GL- and four digits "
                             "(.claude/project.json usage.project_id)")
    if not isinstance(pack_settings, dict) or set(pack_settings) - set(packs) \
            or not all(isinstance(v, dict) for v in pack_settings.values()):
        raise BootstrapError("--pack-settings must map installed packs to objects")
    if signer_line is not None:
        try:
            lock.select_signer(signer_line)
        except lock.LockError as exc:
            raise BootstrapError(f"the signer line cannot be used ({exc})") from None
    elif not (root / SIGNERS_FILE).is_file():
        raise BootstrapError(f"give --signer-line (from the release announcement); {SIGNERS_FILE} does not exist")
    signers_text = signer_line if signer_line is not None else (root / SIGNERS_FILE).read_text(encoding="utf-8-sig")
    base = checked_release(bundle, tag, packs, signers_text)
    rulesets = rulesets or (root / ".github" / "rulesets").is_dir()
    dependabot = dependabot or (root / ".github" / "dependabot.yml").is_file()
    files = project_layer({p: pack_settings.get(p, {}) for p in packs}, rulesets, dependabot, python, signer_line, base,
                          visibility, bots, approvers, project_id)
    plan = adoption_plan(root, packs, base, python) if adopt else {}  # every merge is made before any write
    for rel, (_, added) in plan.items():
        for line in added or ["nothing to add"]:
            print(f"bootstrap: adopt {rel}: {'add ' if added else ''}{line}")
    if plan_only:
        return None
    for rel, written in write_missing(root, files):
        print(f"bootstrap: {'wrote' if written else 'kept '} {rel}")
    try:
        result = lock.write(root, bundle, tag, packs)
    except lock.LockError as exc:
        raise BootstrapError(f"the release was not installed: {exc}") from None
    write_adopted(root, plan)  # after the install, so the hooks it registers exist
    print(f"bootstrap: installed Norma {result['norma']} ({result['files']} files); packs: {', '.join(packs) or 'none'}")
    return run_checks(root)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".")
    parser.add_argument("--bundle", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--packs", default="")
    parser.add_argument("--pack-settings", dest="pack_settings", default=None)
    parser.add_argument("--signer-line", dest="signer_line", default=None)
    parser.add_argument("--rulesets", action="store_true")
    parser.add_argument("--dependabot", action="store_true")
    parser.add_argument("--python", default="python")
    parser.add_argument("--visibility", required=True, choices=VISIBILITIES)
    parser.add_argument("--bot-branch", dest="bot_branches", action="append", default=[])
    parser.add_argument("--bot-login", dest="bot_logins", action="append", default=[])
    parser.add_argument("--approver", dest="approvers", action="append", default=[])
    parser.add_argument("--project-id", dest="project_id", default="")
    parser.add_argument("--adopt", action="store_true")
    parser.add_argument("--plan", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.plan and not args.adopt:
            raise BootstrapError("--plan shows what --adopt would add; give both")
        settings = {}
        if args.pack_settings:
            try:
                settings = json.loads(Path(args.pack_settings).read_text(encoding="utf-8-sig"))
            except (OSError, ValueError):
                raise BootstrapError("--pack-settings is not a readable JSON object") from None
        if len(args.bot_branches) != len(args.bot_logins):
            raise BootstrapError("give one --bot-login for each --bot-branch")
        ok = bootstrap(Path(args.root), Path(args.bundle), args.tag, [p for p in args.packs.split(",") if p], settings,
                       args.signer_line, args.rulesets, args.dependabot, args.python, args.visibility,
                       tuple(zip(args.bot_branches, args.bot_logins)), tuple(args.approvers), args.project_id,
                       args.adopt, args.plan)
    except (BootstrapError, OSError) as exc:
        print(f"bootstrap: {exc}", file=sys.stderr)
        return 2
    if ok is None:
        print("bootstrap: --plan: nothing was written or installed; run again without --plan to apply")
        return 0
    print("bootstrap: done; review the written files, then commit them in the project's own issue and PR"
          if ok else "bootstrap: installed, but a check failed (see above); fix it and run bootstrap again")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
