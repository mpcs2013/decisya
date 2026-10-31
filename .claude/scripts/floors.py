#!/usr/bin/env python3
"""Base settings floor check: a project's checked-in .claude/settings.json must keep everything in
.claude/base-settings.json.

Usage:  python .claude/scripts/floors.py [--settings .claude/settings.json] [--base .claude/base-settings.json]
                                          [--root <project folder>]
Exit 0 = the floor holds; exit 2 = it does not (each problem is printed), or a file cannot be used.

The check is a tripwire for drift and for edits made under review, not a security boundary: it matches the text
and shape of settings, not behaviour; a shell command can get around any settings rule, and a user can override a
checked-in setting in their own local settings file. The hooks and the review of changes are the real guards.
It fails when the project's file:
  - lacks a base deny entry, or lists one in `ask` or `allow` as well;
  - does not register a core hook, or any registration of one has a narrower matcher (judged together), a
    shorter timeout, no `onFailure: "block"`, an interpreter other than the allowed tokens, arguments other than
    `-I <hook path>`, or any key that is not part of the exec form (so no asynchronous run);
  - sets something that can switch a guard off without touching it: `disableAllHooks`, an `env` entry that changes
    how programs load or run, a permission mode that bypasses permissions, `enableAllProjectMcpServers`, or a hook
    of any event that names a core hook, settings or lock file;
  - registers a core hook whose file is missing, not a regular file, or empty (its content is the lock's job).
Messages name the base entry, never a value taken from the project file.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import argparse
import json
import math
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MAX_BYTES = 262144
HOOK_KEYS = {"type", "command", "args", "timeout", "onFailure"}
ENTRY_KEYS = {"matcher", "hooks"}
TOOL_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
MAX_TIMEOUT = 600  # Claude Code's own default for a command hook
# Environment variables that change how programs are found or loaded, so a registered hook could run something else.
# Variables that change how programs load or run, or where they read their configuration (HOME moves ~/.gitconfig).
ENV_DENY = re.compile(r"(?:PATH|PATHEXT|PYTHON|GIT_|LD_|DYLD_|NODE_|DOTNET_|CLAUDE_|HOME$|USERPROFILE$|XDG_"
                      r"|BASH_ENV$|ENV$|COMSPEC$|NPM_CONFIG_|CORECLR_|COMPLUS_|MSBUILD"
                      r"|SYSTEMROOT$|PROGRAMDATA$|ALLUSERSPROFILE$"  # lock.py passes the first two
                      r"|UV_|PIP_|PIPAPI_|PYTEST_|MYPY|RUFF_|COV_CORE_|VIRTUAL_ENV$|APPDATA$|LOCALAPPDATA$)",
                      re.IGNORECASE)  # package sources, plugins, environments, user configuration
# Paths of the core hooks, settings and lock: no other hook may name them.
PROTECTED = re.compile(r"\.claude[/\\](?:hooks|settings|base-settings|core\.lock|core\.manifest|packs)", re.IGNORECASE)


class FloorError(Exception):
    pass


def _no_duplicates(pairs):
    seen = set()
    for key, _ in pairs:
        if key in seen:
            raise ValueError("duplicate key")
        seen.add(key)
    return dict(pairs)


def _not_a_number(name: str):
    raise ValueError(name)


def _finite(text: str) -> float:
    value = float(text)
    if not math.isfinite(value):
        raise ValueError(text)
    return value


def read_json(path: Path, label: str) -> dict:
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        raise FloorError(f"{label} is missing") from None
    except OSError:
        raise FloorError(f"{label} cannot be read") from None
    if len(raw) > MAX_BYTES:
        raise FloorError(f"{label} is larger than {MAX_BYTES} bytes")
    try:  # strict JSON, as Claude Code reads it: NaN, Infinity and a number too large to be finite are refused
        data = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=_no_duplicates, parse_constant=_not_a_number,
                          parse_float=_finite)
    except (ValueError, RecursionError):
        raise FloorError(f"{label} is not valid JSON, has a duplicate key or a number that is not finite") from None
    if not isinstance(data, dict):
        raise FloorError(f"{label} must be a JSON object")
    return data


def tool_set(matcher: object) -> set[str] | None:
    """The tool names a matcher covers; None when it covers every tool; raises for a form that cannot be judged."""
    if not isinstance(matcher, str):
        raise ValueError
    if matcher in ("", "*", ".*"):
        return None
    names = matcher.split("|")
    if not all(TOOL_NAME.fullmatch(n) for n in names):
        raise ValueError
    return set(names)


def _texts(value, depth=0):
    """Every string inside a JSON value, without recursion limits being an issue (depth is capped)."""
    if depth > 8:
        return
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for k, v in value.items():
            yield from _texts(k, depth + 1)
            yield from _texts(v, depth + 1)
    elif isinstance(value, list):
        for v in value:
            yield from _texts(v, depth + 1)


def hook_problems(settings: dict, base: dict, root: Path | None = None) -> list[str]:
    problems: list[str] = []
    interpreters = base["interpreters"]
    hooks = settings.get("hooks")
    entries = hooks.get("PreToolUse", []) if isinstance(hooks, dict) else []
    if not isinstance(entries, list):
        entries = []
    for spec in base["hooks"]["PreToolUse"]:
        script, want_tools, want_timeout = spec["script"], tool_set(spec["matcher"]), spec["timeout"]
        wanted_path = "${CLAUDE_PROJECT_DIR}/" + script
        found = 0
        reasons: list[str] = []
        covered: set[str] = set()
        covers_all = False
        for entry in entries:
            if not isinstance(entry, dict) or not isinstance(entry.get("hooks"), list):
                continue
            for hook in entry["hooks"]:
                if not isinstance(hook, dict) or not isinstance(hook.get("args"), list) or wanted_path not in hook["args"]:
                    continue
                found += 1
                reasons.extend(_hook_reasons(entry, hook, wanted_path, want_timeout, interpreters))
                try:
                    have = tool_set(entry.get("matcher", ""))
                except ValueError:
                    continue
                if have is None:
                    covers_all = True
                else:
                    covered |= have
        if found == 0:
            problems.append(f"core hook {script}: it is not registered")
            continue
        if not covers_all and (want_tools is None or not want_tools <= covered):
            reasons.append("its matcher is narrower than the base")
        if reasons:
            problems.append(f"core hook {script}: " + "; ".join(sorted(set(reasons))))
        if root is not None:
            target = root / script
            if not target.is_file() or target.is_symlink() or target.stat().st_size == 0:
                problems.append(f"core hook {script}: the file is missing, not a regular file, or empty")
    return problems


def _hook_reasons(entry, hook, wanted_path, want_timeout, interpreters) -> list[str]:
    """Why one registration of a core hook does not meet the floor (every registration of it must)."""
    why: list[str] = []
    if not set(entry) <= ENTRY_KEYS:
        why.append("its registration has a key that is not allowed")
    try:
        tool_set(entry.get("matcher", ""))
    except ValueError:
        why.append("its matcher form cannot be judged (use tool names joined with |)")
    if set(hook) != HOOK_KEYS:
        why.append("it must have exactly type, command, args, timeout and onFailure")
    if hook.get("type") != "command":
        why.append("type must be command")
    if hook.get("command") not in interpreters:
        why.append("the interpreter is not one of the allowed tokens")
    if hook.get("args") != ["-I", wanted_path]:
        why.append("args must be -I and the hook path")
    timeout = hook.get("timeout")
    if isinstance(timeout, bool) or not isinstance(timeout, int) or not want_timeout <= timeout <= MAX_TIMEOUT:
        why.append(f"timeout must be an integer from {want_timeout} to {MAX_TIMEOUT}")
    if hook.get("onFailure") != "block":
        why.append('onFailure must be "block"')
    return why


def deny_problems(settings: dict, base: dict) -> list[str]:
    perms = settings.get("permissions", {})
    if not isinstance(perms, dict):
        perms = {}
    lists = {name: perms.get(name, []) if isinstance(perms.get(name, []), list) else [] for name in ("deny", "ask", "allow")}
    problems = []
    for entry in base["deny"]:
        if entry not in lists["deny"]:
            problems.append(f"deny entry missing: {entry}")
        for other in ("ask", "allow"):
            if entry in lists[other]:
                problems.append(f"deny entry also listed in {other}: {entry}")
    return problems


def switch_off_problems(settings: dict, base: dict) -> list[str]:
    """Settings that can neutralise a guard without touching the text of the guard."""
    problems = []
    if settings.get("disableAllHooks"):
        problems.append("disableAllHooks must not be set")
    env = settings.get("env")
    if isinstance(env, dict) and any(isinstance(k, str) and ENV_DENY.match(k) for k in env):
        problems.append("env must not set a variable that changes how programs load or run")
    perms = settings.get("permissions")
    if isinstance(perms, dict) and perms.get("defaultMode") == "bypassPermissions":
        problems.append("permissions.defaultMode must not bypass permissions")
    if settings.get("enableAllProjectMcpServers"):
        problems.append("enableAllProjectMcpServers must not be set")
    core_paths = {"${CLAUDE_PROJECT_DIR}/" + s["script"] for s in base["hooks"]["PreToolUse"]}
    hooks = settings.get("hooks")
    if isinstance(hooks, dict):
        for event, entries in hooks.items():
            for i, entry in enumerate(entries if isinstance(entries, list) else []):
                for j, hook in enumerate(entry.get("hooks", []) if isinstance(entry, dict) and isinstance(entry.get("hooks"), list) else []):
                    args = hook.get("args") if isinstance(hook, dict) else None
                    if event == "PreToolUse" and isinstance(args, list) and len(args) == 2 and args[1] in core_paths:
                        continue  # the registration of a core hook is judged above
                    if any(PROTECTED.search(t) for t in _texts(hook)):
                        problems.append(f"a {event} hook names a core hook, settings, lock or pack file "
                                        f"(hooks.{event}[{i}].hooks[{j}]); a project's own guard lives outside "
                                        ".claude/hooks/ and .claude/scripts/")
    return problems


def local_settings_problems(root: Path | None) -> list[str]:
    """`.claude/settings.local.json` is each person's own file: committed, it would apply to every checkout and could
    switch the hooks off without this check ever reading it."""
    if root is None or not (root / ".git").exists():
        return []
    env = {**os.environ, "NoDefaultCurrentDirectoryInExePath": "1"}
    try:
        run = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], capture_output=True, timeout=60, env=env,
                             check=False)
    except (OSError, subprocess.TimeoutExpired):
        return ["the tracked files cannot be listed, so a committed .claude/settings.local.json cannot be ruled out"]
    if run.returncode != 0:
        return ["the tracked files cannot be listed, so a committed .claude/settings.local.json cannot be ruled out"]
    names = {n.decode("utf-8", "replace").lower() for n in run.stdout.split(b"\0") if n}
    if ".claude/settings.local.json" in names:
        return [".claude/settings.local.json is committed; it is each person's own file (remove it from git)"]
    return []


def problems_for(settings: dict, base: dict, root: Path | None = None) -> list[str]:
    return (deny_problems(settings, base) + hook_problems(settings, base, root) + switch_off_problems(settings, base)
            + local_settings_problems(root))


def check_base(base: dict) -> None:
    ok = (set(base) >= {"schema", "interpreters", "deny", "hooks"} and base["schema"] == 1
          and isinstance(base["interpreters"], list) and base["interpreters"]
          and isinstance(base["deny"], list) and isinstance(base["hooks"], dict)
          and isinstance(base["hooks"].get("PreToolUse"), list))
    if not ok:
        raise FloorError(".claude/base-settings.json has the wrong shape")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--settings", default=str(ROOT / ".claude" / "settings.json"))
    parser.add_argument("--base", default=str(ROOT / ".claude" / "base-settings.json"))
    parser.add_argument("--root", default=None, help="the project folder, to check that the hook files exist")
    args = parser.parse_args(argv)
    try:
        base = read_json(Path(args.base), ".claude/base-settings.json")
        check_base(base)
        settings = read_json(Path(args.settings), ".claude/settings.json")
        root = Path(args.root) if args.root else (ROOT if args.settings == str(ROOT / ".claude" / "settings.json") else None)
        problems = problems_for(settings, base, root)
    except FloorError as exc:
        print(f"floors: {exc}", file=sys.stderr)
        return 2
    if problems:
        print("floors: the base settings floor does not hold:", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 2
    print("floors: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
