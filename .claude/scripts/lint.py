#!/usr/bin/env python3
"""Lint the Claude Code configuration in .claude/ and CLAUDE.md.

Usage (from the repository root):  python .claude/scripts/lint.py
Exit 1 with one "path:line: message" per problem. Checks:
  1. Frontmatter of every agent and skill parses (no unquoted value containing ": ")
     and has name + description; name matches the file or folder name.
  2. Banned patterns (outdated or wrong commands and packages).
  3. Dangling references: skills named in agents/skills/CLAUDE.md, references/ and
     assets/ paths in skills, .claude/scripts paths, agents in boundaries.json.
  4. An agent whose text says it updates existing files has the Edit tool.
  5. No copied "Standing rules" sections (CLAUDE.md is the single source).
  6. boundaries.json matches its schema, and its agents keys equal the agents' frontmatter
     names in both directions.
  7. Agent tools never grant a gh or dotnet command group with a wildcard verb.
  8. G4 routing in boundaries.json names only paths its agent can write.
  9. The generated roster blocks match the config (roster.py --check).
 10. Every Bash(...) tools entry is an exact command or ends in ' *'.
 11. No bare 'Bash' tools entry.
 12. Agent tools are only Read, Grep, Glob, Write, Edit, MultiEdit, NotebookEdit and Bash(...),
     the tools the hook sees and can freeze; only name, description, tools and model in the
     frontmatter, on one tools: line, with no ',' inside Bash(...).
 13. Check 7 also covers aspire and pre-commit.
 14. .claude/project.json loads: the schema, types and rules of the project facts.
 15. The usage capture folder (usage.capture_folder), when it exists, is ignored by git; in a worktree whose git
     cannot name the main checkout (older than 2.31), that is reported instead.
 16. The shared deny list in boundaries.json holds `<usage.capture_folder>/**`.
 17. Every glob a routing.G4 row gives to main-session is covered by the shared deny list.
 18. Each reference folder (secret_guard.reference_folders, ADR 0012) has its Edit ask rule in settings.json and
     `<folder>/**` in the shared deny list, and, when it exists, is a folder git ignores that does not lead to the
     repository.
 19. A "not a bash lookup" marker counts only outside the string it would exempt (in a `#` comment or a docstring's
     prose); a bash or wsl lookup by name fails otherwise.
Warnings, printed but never counted: an installed pack's build-logic files that the shared deny list lacks
(bootstrap's list for that pack); an agent lane over the signers file or the revoked-keys file; an expiring signer
line; a lock.py verify call in a workflow that disagrees with lock.revoked_file; a core agent's lane beyond the area
its text names by default; an own agent whose file states no deny rule or no tool rule; a Bash, PowerShell or
Monitor settings rule that names a reference folder.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import json
import os
import re
import subprocess
import sys
from datetime import date, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLAUDE = ROOT / ".claude"
SELF = Path(__file__).resolve()
def _load(name: str, path: Path):
    """A sibling module from its source file, never from bytecode in __pycache__; one module object per file."""
    import importlib.util
    module = sys.modules.get(name)
    if module is not None and Path(getattr(module, "__file__", "") or "").resolve() == path:
        return module
    module = importlib.util.module_from_spec(importlib.util.spec_from_file_location(name, path))
    sys.modules[name] = module
    exec(compile(path.read_bytes(), str(path), "exec"), module.__dict__)  # noqa: S102
    return module


_hooklib = _load("_hooklib", SELF.parents[1] / "hooks" / "_hooklib.py")  # one schema for lint and hook
(AGENT_FRONTMATTER_KEYS, AGENT_TOOL_NAMES, NAME, ConfigError, _valid_rel, parse_boundaries, pattern_body, printable,
 route_globs, tool_entries, tools_line) = (_hooklib.AGENT_FRONTMATTER_KEYS, _hooklib.AGENT_TOOL_NAMES, _hooklib.NAME,
                                           _hooklib.ConfigError, _hooklib._valid_rel, _hooklib.parse_boundaries,
                                           _hooklib.pattern_body, _hooklib.printable, _hooklib.route_globs,
                                           _hooklib.tool_entries, _hooklib.tools_line)
_claudecfg = _load("_claudecfg", SELF.parent / "_claudecfg.py")  # one parser for lint and roster
PROJECT_REL, SKILL_REF, ProjectConfigError, load_project, parse_frontmatter = (
    _claudecfg.PROJECT_REL, _claudecfg.SKILL_REF, _claudecfg.ProjectConfigError, _claudecfg.load_project,
    _claudecfg.parse_frontmatter)

PROJECT_FILE = ROOT / ".claude" / "project.json"  # fixed at import, never from a patched ROOT

class OutsideStrings:
    """An allowed-if marker that counts only outside the line's string literals: in a comment or in a docstring's
    prose, never inside the quoted text it would exempt (issue #32 item 15)."""

    def __init__(self, marker: str):
        self.marker = marker

    @staticmethod
    def states(line: str, initial: str = "") -> tuple[list[str], str]:
        """The quote open before each character of a code line ("" outside, "#" in a comment), and the triple quote
        still open at its end, which the next line starts in (only a triple-quoted string spans lines)."""
        out, quote, i = [], initial, 0
        while i < len(line):
            triple = line[i:i + 3]
            if quote == "#":
                out.append("#")
                i += 1
                continue
            if quote and len(quote) == 3 and triple == quote:
                out += [quote] * 3
                quote, i = "", i + 3
                continue
            if not quote and triple in ('"""', "'''"):
                out += [""] * 3
                quote, i = triple, i + 3
                continue
            ch = line[i]
            out.append(quote)
            if ch == "\\" and quote:
                out.append(quote)
                i += 2
                continue
            if ch in "'\"" and len(quote) < 3 and (not quote or ch == quote):
                quote = "" if quote else ch
            elif ch == "#" and not quote:
                quote = "#"
            i += 1
        return out, (quote if len(quote) == 3 else "")

    def found(self, line: str, markdown: bool = False, initial: str = "", match_at: int = 0) -> bool:
        """In Markdown the marker counts outside backtick code spans (an apostrophe in prose is no quote there). In
        code it counts in code or a comment, or in the same triple-quoted docstring as the form it exempts (the
        docstring's prose, with triple quotes carried in from earlier lines as `initial`), never inside a one-line
        '...' or "..." string, where a guarded command lives. A docstring's prose line with an apostrophe before
        the marker reads as an open string: write the marker before it, or in a comment. Not told, as a guardrail
        allows: a one-line string continued by a backslash at a line's end, and a marker inside a Markdown fenced
        block (no backtick on its line)."""
        start = 0
        states = None if markdown else self.states(line, initial)[0]
        while (at := line.find(self.marker, start)) >= 0:
            if markdown:
                if line[:at].count("`") % 2 == 0:
                    return True
            elif states[at] in ("", "#") or (len(states[at]) == 3 and states[min(match_at, len(states) - 1)] == states[at]):
                return True
            start = at + 1
        return False


# (regex, message, allowed-if-line-matches). Stack-specific patterns are pack data (`lint_patterns`).
BANNED = [
    (r"\bV3(\.\d+)*\s+[Ss]ession|\bV4\s+[Aa]ccess control|\bV5\s+Validation and encoding|\bV7\s+Error handling", "ASVS 4.0 chapter label; ASVS 5.0 numbering differs (see asvs-checklist references)", None),
    (r"\bgit diff main\b", "diff against origin/main...HEAD; local main may be stale", None),
    # on Windows a bash started or found by name can be a WSL launcher; one line at a time: which("bash"), a list or
    # tuple that starts with "bash" or "wsl" (.exe, any case, string prefixes of up to two letters), or a command
    # string given to a process call. In a list or tuple the exact name "Bash" is Claude Code's tool name (settings
    # permissions, skill allowed-tools), never a program. A line that names bash on purpose carries the marker
    # "not a bash lookup" outside its string literals (a comment, or the prose of a docstring that quotes the form)
    (r"""(?i)which\(\s*(?:cmd\s*=\s*)?[rbuf]{0,2}['"](?:bash|wsl)(?:\.exe)?['"]"""
     r"""|\[\s*[rbuf]{0,2}['"](?-i:(?!Bash['"]))(?:bash|wsl)(?:\.exe)?['"]\s*[,\]]"""
     r"""|\(\s*[rbuf]{0,2}['"](?-i:(?!Bash['"]))(?:bash|wsl)(?:\.exe)?['"]\s*,"""
     r"""|\b(?:run|Popen|call|check_call|check_output|getoutput|system|popen)\(\s*[rbuf]{0,2}['"]"""
     r"""(?:bash|wsl)(?:\.exe)?(?=[\s'"])""",
     "looks bash or wsl up by name; on Windows that can start the WSL launcher: take Git Bash from git's install folder",
     OutsideStrings("not a bash lookup")),
]
# the agent sandbox never sees the host Docker daemon or host secret folders.
SANDBOX_FORBIDDEN = [
    (r"docker\.sock", "mounts the host Docker socket (full host control)"),
    (r"UserSecrets|usersecrets", "mounts dotnet user-secrets"),
    (r"localEnv:(USERPROFILE|HOME)\}?[/\\]\.(ssh|claude|aws|azure|kube|docker)\b", "mounts a host credential folder"),
    (r"(^|[\s\"'=])(~|\$HOME|\$\{HOME\})[/\\]\.(ssh|aws|azure|kube)\b", "mounts a host credential folder"),
    (r"SSH_AUTH_SOCK", "forwards the SSH agent"),
    # threat model: never, no marker can allow these.
    (r"--privileged|\"privileged\"\s*:\s*true|privileged:\s*true", "privileged container (never; the fallback is no sidecar)"),
    (r"seccomp\s*[:=]\s*[\"']?unconfined", "seccomp=unconfined (never shipped)"),
    (r"\b(CAP_)?SYS_ADMIN\b", "adds CAP_SYS_ADMIN"),
    (r"^\s*[\"']?(pid|ipc|uts|cgroup|network_mode|userns_mode)[\"']?\s*:\s*[\"']?host\b|--(pid|ipc|uts|network|net|userns)[= ]host\b", "joins a host namespace"),
    (r"^\s*[\"']?o[\"']?\s*:\s*[\"']?[^\"'\n]*\bbind\b", "volume driver_opts bind (host path mount)"),
    (r"[{,]\s*[\"']?o[\"']?\s*:\s*[\"']?[^,}\n]*\bbind\b", "volume driver_opts bind in flow style (host path mount)"),
    # Conditional rungs: only with an explicit "sandbox-lint: allow-rung (<reason>)" marker on the line.
    (r"systempaths\s*[:=]\s*[\"']?unconfined", "systempaths=unconfined (only with no-new-privileges on and no setuid/file-capability binary; mark 'sandbox-lint: allow-rung')"),
    (r"apparmor\s*[:=]\s*[\"']?unconfined", "apparmor=unconfined (only if AppArmor is inactive; mark 'sandbox-lint: allow-rung')"),
]
# Patterns a marked line may use; every other SANDBOX_FORBIDDEN pattern has no exemption.
SANDBOX_RUNG_MARKER = "sandbox-lint: allow-rung"
SANDBOX_RUNG_PATTERNS = {r"systempaths\s*[:=]\s*[\"']?unconfined", r"apparmor\s*[:=]\s*[\"']?unconfined"}
EDIT_VERBS = re.compile(r"\b(update[sd]?|keep current|kept current|add a row|a row in|maintain(s|ed)?|append(ed|s)?|edit (statuses|in place))\b", re.IGNORECASE)
LOCAL_REF = re.compile(r"`((?:\.\./[a-z0-9-]+/)?(?:references|assets|scripts)/[\w./-]+)`")
SCRIPT_REF = re.compile(r"\.claude/(?:scripts|hooks)/[\w.-]+\.py")

problems: list[str] = []

# G6: the only capabilities and devices any sandbox service may add.
SANDBOX_ALLOWED_CAPS = {"SETUID", "SETGID", "SYS_CHROOT"}
SANDBOX_ALLOWED_DEVICES = {"/dev/net/tun"}


def check_compose_lists(path: Path) -> None:
    """cap_add and devices entries must be on the allow-lists, whether written as a flow list on
    the same line, a flow list starting on the next line, a flow list spanning several lines, or a
    block list. In overlays (every compose file except the base
    compose.yaml, which binds the repository by design) no host path may be mounted in any form."""
    lines = [raw.split("#", 1)[0].rstrip() for raw in path.read_text(encoding="utf-8", errors="ignore").splitlines()]
    i = 0
    while i < len(lines):
        m = re.match(r"^(\s*)[\"']?(cap_add|devices)[\"']?\s*:\s*(.*)$", lines[i])
        if not m:
            i += 1
            continue
        indent, key, rest, j = len(m.group(1)), m.group(2), m.group(3).strip(), i + 1
        if not rest:
            while j < len(lines) and not lines[j].strip():
                j += 1
            if j < len(lines) and lines[j].strip().startswith("["):
                rest, j = lines[j].strip(), j + 1
        if rest.startswith("["):
            line_no, buf = j, rest
            while "]" not in buf and j < len(lines):
                buf, j = buf + " " + lines[j].strip(), j + 1
            for item in (s.strip().strip("\"'") for s in buf.strip().strip("[]").split(",")):
                if item:
                    _check_list_item(path, line_no, key, item)
            i = j
            continue
        k = i + 1
        while k < len(lines):
            text = lines[k]
            if text.strip():
                if len(text) - len(text.lstrip()) <= indent:
                    break
                if text.lstrip().startswith("-"):
                    _check_list_item(path, k + 1, key, text.lstrip()[1:].strip().strip("\"'"))
            k += 1
        i = k

    if path.name == "compose.yaml":
        return
    parents: list[tuple[int, str]] = []
    for n, text in enumerate(lines, start=1):
        if not text.strip():
            continue
        indent = len(text) - len(text.lstrip())
        while parents and parents[-1][0] >= indent:
            parents.pop()
        if re.search(r"\btype[\"']?\s*:\s*[\"']?bind\b", text):
            report(path, n, "sandbox-config: bind mount in a compose overlay")
        in_volumes = bool(parents) and parents[-1][1] == "volumes"
        if in_volumes and re.match(r"^\s*-\s*[\"']?[/.~$][^\s\"',]*:", text):
            report(path, n, "sandbox-config: short-syntax host path mount in a compose overlay")
        km = re.match(r"^\s*[\"']?([\w.-]+)[\"']?\s*:\s*$", text)
        if km:
            parents.append((indent, km.group(1)))


def _check_list_item(path: Path, line: int, key: str, item: str) -> None:
    if key == "cap_add":
        cap = item.upper().removeprefix("CAP_")
        if cap not in SANDBOX_ALLOWED_CAPS:
            report(path, line, f"sandbox-config: cap_add {item} is not in {sorted(SANDBOX_ALLOWED_CAPS)}")
    else:
        device = item.split(":", 1)[0]
        if device not in SANDBOX_ALLOWED_DEVICES:
            report(path, line, f"sandbox-config: device {item} is not in {sorted(SANDBOX_ALLOWED_DEVICES)}")


def report(path: Path, line: int, msg: str) -> None:
    problems.append(f"{path.relative_to(ROOT).as_posix()}:{line}: {msg}")


def build_logic_warnings(packs: dict, deny: list[str]) -> list[str]:
    """The build-logic globs bootstrap denies for each installed pack that the shared deny list lacks, compared without
    case as the hook compares them. A warning, never a problem: bootstrap writes these only for a new project, and a
    project may keep the files out of every lane by narrowing the lanes instead."""
    table = _load("bootstrap", SELF.parent / "bootstrap.py").BUILD_LOGIC  # one list for bootstrap and lint
    denied = {glob.lower() for glob in deny}
    warnings = []
    for pack in sorted(packs):
        missing = [glob for glob in table.get(pack, []) if glob.lower() not in denied]
        if missing:
            warnings.append(f".claude/boundaries.json: warning: the {pack} pack's build-logic files are not in deny "
                            f"(no listed agent may write them): {', '.join(missing)}")
    return warnings


def frontmatter(path: Path) -> tuple[dict[str, str], int]:
    data, body_start, found = parse_frontmatter(path.read_text(encoding="utf-8"))
    for line, msg in found:
        report(path, line, msg)
    return data, body_start


def check_banned(path: Path, patterns: list) -> None:
    """The core patterns, those of the installed packs (pack data `lint_patterns`) and the project's own
    (`lint_patterns` in project.json)."""
    markdown, carried = path.suffix.lower() == ".md", ""  # the triple quote a code line starts in
    for i, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        for pattern, msg, allowed in patterns:
            match = re.search(pattern, line)
            if not match:
                continue
            exempt = bool(allowed) and (allowed.found(line, markdown, carried, match.start())
                                        if isinstance(allowed, OutsideStrings) else re.search(allowed, line) is not None)
            if not exempt:
                report(path, i, f"banned pattern /{pattern}/: {msg}")
        if not markdown:
            carried = OutsideStrings.states(line, carried)[1]


def check_skill_refs(path: Path, skills: set[str]) -> None:
    for i, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        for m in SKILL_REF.finditer(line):
            name = next(g for g in m.groups() if g)
            if name not in skills:
                report(path, i, f"references skill '{name}', which has no .claude/skills/{name}/SKILL.md")
        for m in SCRIPT_REF.finditer(line):
            if not (ROOT / m.group(0)).exists():
                report(path, i, f"references {m.group(0)}, which does not exist")


# gh <group> <verb> and dotnet <verb>; these dotnet verbs also need their sub-verb (e.g. `new list`).
DOTNET_GROUPS = {"new", "tool", "package", "nuget", "workload"}
DOCKER_GROUPS = {"volume", "container", "compose", "image", "network", "system", "context"}
# tool grants that print container environments (generated secrets) or open a shell.
FORBIDDEN_TOOLS = re.compile(r"docker\s+(container\s+)?(inspect|exec|cp)\b|docker\s+compose\s+config\b", re.IGNORECASE)
# skill frontmatter keys. Skills are trusted instructions; they must not grant tools,
# register hooks or switch models. The last two keys are already used by the issue skill.
SKILL_FRONTMATTER_KEYS = {"name", "description", "argument-hint", "disable-model-invocation"}
# instructions that must not appear in skills or agents unless the line forbids them.
RISKY_INSTRUCTIONS = re.compile(
    r"docker\s+(container\s+)?inspect|docker\s+exec|docker\s+(volume\s+)?(rm|prune)\b|docker\s+run\b"
    r"|compose\s+down\s+-v|user-secrets\s+list|WithBindMount|docker\.sock|--privileged"
    r"|TESTCONTAINERS_RYUK_DISABLED|sslRequired\"?\s*:\s*\"?none", re.IGNORECASE)
FORBIDDING = re.compile(r"\b(never|do not|not)\b", re.IGNORECASE)


def unforbidden_risk(line: str) -> str | None:
    """The first risky instruction on the line that is not negated in its own clause, else None.
    The negation must come before the match and after the last clause boundary:
    "never run docker exec" passes; "if it did not start, run docker exec" does not."""
    for m in RISKY_INSTRUCTIONS.finditer(line):
        clause = re.split(r"[.;:!?,]\s", line[:m.start()])[-1]
        if not FORBIDDING.search(clause):
            return m.group(0)
    return None
WORD = re.compile(r"-{0,2}[A-Za-z][\w-]*")
# Verbs the hook denies to agents (agent_boundaries.AGENT_DENIED_COMMANDS). A trailing-* verb that
# is a strict prefix of one of these (e.g. `dotnet p*`) grants it by accident.
DANGEROUS_VERBS = {
    ("dotnet",): {"add", "package", "nuget", "workload"},
    ("dotnet", "new"): {"install"}, ("dotnet", "tool"): {"install", "update"},
    ("dotnet", "package"): {"add", "update"}, ("dotnet", "nuget"): {"add"},
    ("dotnet", "workload"): {"install", "update", "restore"},
    ("gh", "issue"): {"delete", "transfer", "edit", "close", "reopen", "comment", "develop", "pin", "unpin", "lock", "unlock"},
    ("gh", "run"): {"rerun", "cancel", "delete", "download"},
    ("gh", "repo"): {"delete", "archive", "edit", "rename"},
    ("gh", "pr"): {"merge"}, ("gh", "release"): {"delete"},
}


def wildcard_grant(entry: str) -> bool:
    """True when a Bash(gh|dotnet|docker|aspire|pre-commit ...) tool entry leaves the verb to a
    wildcard. `x *` completes the last word; `x*` leaves it open."""
    m = re.fullmatch(r"\s*Bash\((.*)\)\s*", entry)
    if not m:
        return False
    body = m.group(1).strip()
    body = body[:-2].rstrip() + " *" if body.endswith(":*") else body
    tokens = (body[:-2] if body.endswith(" *") else body).split()
    if not tokens or not re.match(r"(gh|dotnet|docker|aspire|pre-commit)(\*|$)", tokens[0]):
        return False
    if tokens[0] not in ("gh", "dotnet", "docker", "aspire", "pre-commit"):
        return True  # Bash(gh*) / Bash(dotnet*) / Bash(docker*)
    if tokens[0] in ("aspire", "pre-commit"):  # lint check 13: `aspire *` includes exec and deploy
        return len(tokens) < 2 or tokens[1].endswith("*")
    if tokens[0] == "docker":  # `docker volume *`, `docker compose *`, `docker *`
        fixed = 2 if len(tokens) > 1 and tokens[1] in DOCKER_GROUPS else 1
    else:
        fixed = 2 if tokens[0] == "gh" or (len(tokens) > 1 and tokens[1] in DOTNET_GROUPS) else 1
    if len(tokens) <= fixed:
        return True
    words, verb = tokens[1:fixed], tokens[fixed]
    if not all(WORD.fullmatch(w) for w in words) or not WORD.fullmatch(verb.removesuffix("*")):
        return True
    stem = verb.removesuffix("*")
    dangerous = DANGEROUS_VERBS.get((tokens[0], *words), set())
    return verb.endswith("*") and any(d.startswith(stem) and d != stem for d in dangerous)


def _glob_regex(pattern: str) -> re.Pattern[str]:
    """Same glob semantics as agent_boundaries.glob_to_regex."""
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return re.compile(f"^{out}$")


def check_roster() -> None:
    """the generated blocks match the config. A stale block and any exception each become one
    problem (exception class only) so lint never passes silently."""
    readme = ROOT / "docs" / "ai" / "README.md"
    if not readme.exists():
        return  # a lint test tree; CI's "Roster is current" step fails on a missing README
    try:
        roster = _roster_module()
        for message in roster.check(ROOT):
            report(readme, 1, message)
    except Exception as exc:  # noqa: BLE001
        message = str(exc) if type(exc).__name__ == "RosterError" else f"roster check failed ({type(exc).__name__})"
        report(readme, 1, message)


def _roster_module():
    return _load("roster", SELF.parent / "roster.py")


def check_boundaries(path: Path, agent_names: dict[str, Path]) -> None:
    """Schema, then agents keys == frontmatter names in both directions."""
    if not path.exists():
        report(path.parent, 1, "boundaries.json is missing")
        return
    try:
        text = path.read_text(encoding="utf-8-sig")
        config = parse_boundaries(text)
    except json.JSONDecodeError as exc:
        report(path, exc.lineno, f"boundaries.json is not valid JSON: {exc.msg}")
        return
    except ConfigError as exc:
        report(path, 1, f"boundaries.json schema: {printable(str(exc))}")
        return
    except (OSError, UnicodeDecodeError, RecursionError) as exc:  # unreadable, not UTF-8, or nested too deep
        report(path, 1, f"boundaries.json cannot be read ({type(exc).__name__})")
        return
    lines = text.splitlines()
    if (ROOT / "docs" / "ai" / "README.md").exists():  # the real repository (test trees have no docs/)
        for key in ("docker", "routing"):
            if key not in config:
                report(path, 1, f"boundaries.json has no '{key}' key; roster.py and the hook need it")
    deny = [_glob_regex(g) for g in config.get("deny", [])]
    for i, row in enumerate(config.get("routing", {}).get("G4", [])):
        line = next((n for n, l in enumerate(lines, start=1) if f'"agent": "{row["agent"]}"' in l), 1)
        if row["agent"] == "main-session":  # _hooklib.MAIN_SESSION: no listed agent, so no lanes to check against
            # a path only the main session may write must be one no listed agent can (issue #32 item 16)
            for glob in route_globs(row["paths"]):
                # covered only by the same glob, by `**`, or by `<a literal folder>/**` above it: sampled paths would
                # let `x*` or `src/a**` pass for a route they do not cover
                route = glob.lower()
                folded = [g.lower() for g in config.get("deny", [])]
                # a route without a wildcard is one path: any deny glob that matches it covers it
                if not any(d == route or d == "**" or (d.endswith("/**") and "*" not in d[:-3]
                                                       and route.startswith(d[:-2]))
                           or ("*" not in route and _glob_regex(d).match(route)) for d in folded):
                    report(path, line, f"routing.G4[{i}]: `{glob}` routes to main-session but the shared deny does "
                                       "not cover it, so a listed agent could write it too; add it to deny")
            continue
        lanes = config["agents"][row["agent"]]
        for glob in route_globs(row["paths"]):
            # A route names only paths its agent can actually write.
            if not _valid_rel(glob) or any(d.match(glob) for d in deny):
                report(path, line, f"routing.G4[{i}]: `{glob}` is not a writable repository path (shared deny or invalid)")
            elif glob not in lanes and not any(_glob_regex(g).match(glob) for g in lanes):
                report(path, line, f"routing.G4[{i}]: `{glob}` is outside {row['agent']}'s lanes")
    for name in config["agents"]:
        if name not in agent_names:
            line = next((i for i, l in enumerate(lines, start=1) if f'"{name}"' in l), 1)
            report(path, line, f"boundaries for '{name}', but no .claude/agents/*.md has name '{name}'")
    for name, agent in sorted(agent_names.items()):
        if name not in config["agents"]:
            report(agent, 2, f"agent '{name}' has no boundaries.json entry (use [] for an agent that never writes)")


def check_agent_tools(agent: Path, data: dict[str, str], text: list[str]) -> None:
    """The hook enforces the Bash patterns read by the same
    _hooklib functions, and only tools its matcher sees, so the freeze covers every tool with side
    effects (SubagentHandback stays the only exemption)."""
    line = next((i for i, l in enumerate(text, start=1) if l.startswith("tools:")), 1)
    for key in sorted(set(data) - AGENT_FRONTMATTER_KEYS):
        report(agent, 1, f"frontmatter key '{key}' is not allowed in an agent (allowed: "
                         f"{', '.join(sorted(AGENT_FRONTMATTER_KEYS))})")
    try:
        value = tools_line("\n".join(text))
    except ConfigError as exc:
        report(agent, 1, f"{exc}")
        return
    for entry in tool_entries(value):
        if entry == "Bash":
            report(agent, line, "bare 'Bash' grants every command; list Bash(...) patterns")
        elif entry.startswith("Bash(") != entry.endswith(")") or entry.count("(") > 1:
            report(agent, line, f"tools entry '{entry[:60]}' is cut by a ',' inside Bash(...)")
        elif entry.startswith("Bash("):
            try:
                pattern_body(entry[5:-1])
            except ConfigError as exc:
                report(agent, line, f"{exc}")
        elif entry not in AGENT_TOOL_NAMES:
            report(agent, line, f"tool '{entry}' is not one the hook can freeze (allowed: "
                                f"{', '.join(sorted(AGENT_TOOL_NAMES))}, Bash(...))")


def capture_folder_problem(folder: str) -> str | None:
    """Why an existing capture folder could reach a commit, or None. The folder itself must be ignored, since a later
    line can re-include a file that only a pattern for the files inside matched. A folder that does not exist yet has
    nothing in it; the capture itself refuses to write into a folder git does not ignore. The folder sits in the main
    checkout, which a worktree shares."""
    base = ROOT
    try:
        common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=ROOT,
                                capture_output=True, text=True, check=False, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        common = None
    if common is not None and common.returncode == 0:
        if Path(common.stdout.strip()).name.lower() != ".git":
            return None  # a bare repository's worktree or a submodule: no main checkout, and the capture writes nothing
        base = Path(common.stdout.strip()).parent
    elif os.path.isfile(ROOT / ".git"):  # a worktree or submodule whose git cannot name the main checkout: say so
        return ("git could not name the main checkout of this worktree (git rev-parse --path-format needs git 2.31 "
                f"or newer), so whether git ignores usage.capture_folder {folder} cannot be checked")
    # otherwise ROOT is the checkout itself (a .git folder), or no repository at all: the folder is checked under ROOT
    if not os.path.lexists(base / folder):
        return None
    try:
        result = subprocess.run(["git", "check-ignore", "-q", folder], cwd=base, capture_output=True,
                                check=False, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        return f"usage.capture_folder {folder} exists and git could not be asked whether it ignores it"
    if result.returncode == 1:
        return f"usage.capture_folder {folder} exists but git does not ignore it; add /{folder}/ to .gitignore"
    if result.returncode != 0:
        return f"usage.capture_folder {folder} exists and git could not be asked whether it ignores it"
    return None


def capture_deny_problem(folder: str, deny: list[str]) -> str | None:
    """A problem when no deny entry keeps listed agents out of the capture folder (bootstrap writes `<folder>/**`):
    an agent that may write there could plant or change a capture, a record or a report before the copy job takes it."""
    if f"{folder}/**".lower() in {glob.lower() for glob in deny}:
        return None
    return f"the capture folder is not in deny, so a listed agent could write it: add {folder}/** to deny"


def reference_folder_problems(names: list[str], deny: list[str] | None, settings: dict | None) -> list[str]:
    """ADR 0012: each reference folder needs an `ask` rule for the file tools in settings.json, `<folder>/**` in the
    shared deny (no listed agent writes there), and, when it exists in this checkout, git ignoring it as a folder."""
    out = []
    permissions = settings.get("permissions") if isinstance(settings, dict) else None
    ask = permissions.get("ask") if isinstance(permissions, dict) else None
    # the tool name compared with case, as Claude Code reads it; the folder without case, as the guard matches it
    rules = {r[len("Edit("):].lower() for r in ask if isinstance(r, str) and r.startswith("Edit(")} \
        if isinstance(ask, list) else set()
    for name in names:
        if not {f"./{name}/**)".lower(), f"{name}/**)".lower()} & rules:
            out.append(f".claude/settings.json:1: reference folder {name} has no ask rule Edit(./{name}/**), so the "
                       "file tools may write it without asking the owner")
        if deny is not None and f"{name}/**".lower() not in {glob.lower() for glob in deny}:
            out.append(f".claude/boundaries.json:1: reference folder {name} is not in deny, so a listed agent could "
                       f"write it: add {name}/** to deny")
        place = ROOT / name
        if os.path.lexists(place) and not place.is_dir():
            out.append(f"{PROJECT_REL}:1: reference folder {name} exists but is not a folder")
        elif os.path.lexists(place) and (Path(os.path.realpath(place)) == Path(os.path.realpath(ROOT))
                                         or Path(os.path.realpath(ROOT)).is_relative_to(os.path.realpath(place))):
            out.append(f"{PROJECT_REL}:1: reference folder {name} leads to the repository or a folder holding it, so "
                       "every path would count as inside it; point it elsewhere")
        elif os.path.lexists(place):
            try:
                result = subprocess.run(["git", "check-ignore", "-q", name], cwd=ROOT, capture_output=True,
                                        check=False, timeout=30)
                code = result.returncode
            except (OSError, subprocess.TimeoutExpired):
                code = 128
            if code == 1 and place.is_symlink():  # git matches a symbolic link only by a pattern without the slash
                out.append(f"{PROJECT_REL}:1: reference folder {name} is a symbolic link git does not ignore; add "
                           f"/{name} (without a trailing slash) to .gitignore")
            elif code == 1:
                out.append(f"{PROJECT_REL}:1: reference folder {name} exists but git does not ignore it; add /{name}/ "
                           "to .gitignore")
            elif code != 0:
                out.append(f"{PROJECT_REL}:1: reference folder {name} exists and git could not be asked whether it "
                           "ignores it")
    return out


def reference_shell_rule_warnings(names: list[str], settings: dict | None) -> list[str]:
    """A Bash, PowerShell or Monitor settings rule (allow, ask or deny) that names a reference folder: a hook cannot
    override an ask or deny rule, so it keeps every shell read of the folder prompting or refused, and an allow rule
    there is the project's own decision the guard now makes (ADR 0012)."""
    permissions = settings.get("permissions", {}) if isinstance(settings, dict) else {}
    out = []
    for kind in ("allow", "ask", "deny"):
        rules = permissions.get(kind, []) if isinstance(permissions, dict) else []
        for rule in rules if isinstance(rules, list) else []:
            if isinstance(rule, str) and rule.startswith(("Bash(", "PowerShell(", "Monitor(")) \
                    and any(name.lower() in rule.lower() for name in names):
                out.append(f".claude/settings.json: warning: a {rule.split('(', 1)[0]} rule in permissions.{kind} names "
                           "a reference folder; the secret guard decides those commands now (ADR 0012), so remove it")
                break
    return out


# The write area each core agent's text names as its default (issue #32): the text defers to the lane in
# boundaries.json, so the two can no longer disagree; a lane beyond the default is the project's choice, warned of
CORE_WRITE_DEFAULTS = {"architect": ("docs/",), "product-owner": ("docs/requirements/",),
                       "tech-writer": ("docs/", "README.md", "CHANGELOG.md")}


def own_agent_rule_warnings(agent_names: dict[str, Path], listed: dict) -> list[str]:
    """A warning for each listed agent of the project's own (not a core agent) whose file states no rule for a deny
    and no tool rule (issue #32 item 17): the hook freezes a listed agent at its first deny, and a launch's prompt may
    not carry the rule, so the agent's own file does. Checked by phrase, as the core agents' tests check theirs."""
    core = set(_load("bootstrap", SELF.parent / "bootstrap.py").CORE_AGENT_LANES)
    out = []
    for name, path in sorted(agent_names.items()):
        if name in core or name not in (listed if isinstance(listed, dict) else {}):
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue  # reported as unreadable already
        flat = " ".join(text.split()).lower()
        tools = next((l for l in text.splitlines() if l.startswith("tools:")), "")
        missing = []
        if "stop and report" not in flat and "stop-and-report" not in flat:
            missing.append("stop and report on any deny")
        if "Bash(" in tools:
            if "your tools line" not in flat and "your patterns" not in flat:
                missing.append("run only the commands your tools line allows, as written")
        elif "no shell call" not in flat:
            missing.append("make no shell call of any kind")
        if missing:
            out.append(f".claude/agents/{path.name}: warning: this listed agent of your own states no rule to "
                       f"{' and no rule to '.join(missing)}; the hook freezes its run at the first deny, so its file "
                       "carries the rule (docs/consumer-guide.md, \"First install\", step 5)")
    return out


def lane_beyond_default_warnings(agents: dict) -> list[str]:
    """A warning for each core agent with a lane outside the default write area its text names. The lane's glob is
    never echoed, only the agent and how many such lanes it has."""
    out = []
    for agent, defaults in CORE_WRITE_DEFAULTS.items():
        lanes = agents.get(agent) if isinstance(agents, dict) else None
        wider = [g for g in (lanes if isinstance(lanes, list) else []) if isinstance(g, str)
                 and not any(g == d or (d.endswith("/") and g.startswith(d)) for d in defaults)]
        if wider:
            out.append(f".claude/boundaries.json: warning: {agent} has {len(wider)} lane(s) beyond its default write "
                       f"area ({', '.join(defaults)}); its text defers to the lanes, so keep each as narrow as the "
                       "work needs and say why in the agent's addendum")
    return out


def signers_lane_warning(signers: str, agents: dict, deny: list[str], what: str = "the signers file",
                         key: str = "lock.signers_file") -> list[str]:
    """A warning when an agent's write lane covers the signers file (or the revoked-keys file) and no deny entry keeps
    it out: that agent could change the release signer line (or drop a revoked key). Deny globs match without case and
    lanes with it, as the hook matches them."""
    if any(_glob_regex(glob.lower()).match(signers.lower()) for glob in deny):
        return []
    if not any(_glob_regex(glob).match(signers) for lanes in agents.values() for glob in lanes):
        return []
    return [f".claude/boundaries.json: warning: an agent's write lane covers {what} ({key}), "
            "and no deny entry keeps listed agents out of it; add it to deny or narrow the lane"]


# a `lock.py verify` call in a workflow, and its `--revoked <file>` (quoted or not, or as --revoked=<file>)
_VERIFY_CALL = re.compile(r"lock\.py[\"']?\s+verify\b")
_REVOKED_ARG = re.compile(r"--revoked(?:=|\s+)([\"']?)([^\s\"']+)\1")


def revoked_ci_warnings(revoked: str | None, workflows: Path | None = None) -> list[str]:
    """Warnings when CI's `lock.py verify --revoked <file>` and `lock.revoked_file` disagree: the review floor would
    then cover another file than the one CI reads. A text check of `.github/workflows/*.yml` and `*.yaml` (a line
    ending in `\\` joins the next one); a value built from a variable, or a call split another way, is beyond it.
    The values are never printed, only the workflow and its line."""
    workflows = workflows or ROOT / ".github" / "workflows"
    calls = []  # (where, the --revoked values on that call)
    try:
        files = sorted(p for p in workflows.iterdir() if p.suffix.lower() in (".yml", ".yaml") and p.is_file())
    except OSError:
        files = []
    for path in files:
        try:
            lines = path.read_text(encoding="utf-8-sig").splitlines()
        except (OSError, UnicodeDecodeError):
            continue
        n = 0
        while n < len(lines):
            start, text = n + 1, lines[n]
            while text.rstrip().endswith("\\") and n + 1 < len(lines):
                n += 1
                text = text.rstrip()[:-1] + " " + lines[n]
            n += 1
            if not text.lstrip().startswith("#") and _VERIFY_CALL.search(text):
                values = [m.group(2) for m in _REVOKED_ARG.finditer(text)]
                calls.append((f".github/workflows/{printable(path.name)}:{start}", values))
    if revoked is None:
        return [f"{where}: warning: CI passes --revoked to lock.py verify but lock.revoked_file is not set, so an edit "
                "of the revoked-keys file needs no G3 and G6 review; name the file in lock.revoked_file"
                for where, values in calls if values]
    if not calls:
        return [f"{PROJECT_REL} lock.revoked_file: warning: no workflow in .github/workflows runs `lock.py verify`, so "
                "nothing shows that CI passes the revoked-keys file; check the CI lock step by hand"]

    def same(value: str) -> bool:
        return value.replace("\\", "/").removeprefix("./").lower() == revoked.lower()

    out = []
    for where, values in calls:
        if not values:
            out.append(f"{where}: warning: this lock.py verify call has no --revoked, but lock.revoked_file is set; "
                       "add --revoked with that file")
        elif not all(same(v) for v in values):
            out.append(f"{where}: warning: this lock.py verify call names another revoked-keys file than "
                       "lock.revoked_file (or names it through a variable, which this check cannot follow)")
    return out


EXPIRY_NOTICE_DAYS = 30


def signer_expiry_warnings(signers: str, today: date | None = None) -> list[str]:
    """ADR 0010: warnings for an expiring release signer line. From 30 days before its date while the installed
    release is still signed by its key (after the date every lock check of that release fails), and after the date
    while the line is still there. A file `lock.py` refuses gives none: `lock.py verify` reports it."""
    lock = _load("_norma_lock", SELF.parent / "lock.py")
    today = today or date.today()
    try:
        lines = lock.select_signers((ROOT / signers).read_text(encoding="utf-8-sig"), today)
        installed = json.loads((ROOT / lock.LOCK_REL).read_text(encoding="utf-8-sig")).get("signer")
    except (OSError, ValueError, AttributeError, RecursionError, lock.LockError):
        return []
    out = []
    for _, key, ends in lines:
        if ends is None:
            continue
        end = datetime.strptime(ends, "%Y%m%d").date()
        where = f"{PROJECT_REL} lock.signers_file"  # the setting, never its value
        old_installed = installed == lock.key_fingerprint(key)
        if today >= end and old_installed:
            out.append(f"{where}: warning: the installed release is signed by the key whose line ended on "
                       f"{end.isoformat()}, so every lock check fails: bump to a release signed with the new key, "
                       "then remove the line")
        elif today >= end:
            out.append(f"{where}: warning: the expiring release signer line ended on {end.isoformat()}; remove it")
        elif (end - today).days <= EXPIRY_NOTICE_DAYS and old_installed:
            out.append(f"{where}: warning: the installed release is signed by the key whose line ends on "
                       f"{end.isoformat()}; bump to a release signed with the new key before then, or every lock "
                       "check fails from that day")
    return out


def unreadable_files(paths: list[Path]) -> set[Path]:
    """The files that cannot be read as UTF-8 text, each reported once: lint goes on with the others instead of
    stopping on a traceback that hides every other problem."""
    bad = set()
    for path in dict.fromkeys(paths):
        try:
            path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            report(path, 1, "is not UTF-8 text")
            bad.add(path)
        except OSError as exc:
            report(path, 1, f"cannot be read ({type(exc).__name__})")
            bad.add(path)
    return bad


def main() -> int:
    agents = sorted((CLAUDE / "agents").glob("*.md"))
    skill_dirs = sorted(p.parent for p in (CLAUDE / "skills").glob("*/SKILL.md"))
    skills = {d.name for d in skill_dirs}  # an unreadable skill still exists: references to it are not dangling
    text_files = [p for p in CLAUDE.rglob("*") if p.is_file() and p.suffix in {".md", ".json", ".py", ".cs", ".csproj"}
                  and "__pycache__" not in p.parts and p.resolve() != SELF
                  and p.parent != CLAUDE / "packs"]  # pack data holds the patterns themselves
    claude_md = [ROOT / "CLAUDE.md"] if (ROOT / "CLAUDE.md").is_file() else []  # optional for Claude Code
    # every file the checks below read, checked once before any of them reads it
    unreadable = unreadable_files(agents + [d / "SKILL.md" for d in skill_dirs] + sorted(text_files) + claude_md)
    agents = [a for a in agents if a not in unreadable]
    skill_dirs = [d for d in skill_dirs if d / "SKILL.md" not in unreadable]
    agent_names: dict[str, Path] = {}

    for agent in agents:
        data, body_start = frontmatter(agent)
        for key in ("name", "description", "tools"):
            if key not in data:
                report(agent, 1, f"frontmatter has no '{key}'")
        if data.get("name"):
            agent_names[data["name"]] = agent
            if data["name"] != agent.stem:
                report(agent, 2, f"name '{data['name']}' differs from file name '{agent.stem}'")
        text = agent.read_text(encoding="utf-8").splitlines()
        check_agent_tools(agent, data, text)
        tools = {t.strip().split("(")[0] for t in data.get("tools", "").split(",")}
        for entry in data.get("tools", "").split(","):
            tools_line = next((i for i, l in enumerate(text, start=1) if l.startswith("tools:")), 1)
            if wildcard_grant(entry):
                report(agent, tools_line, f"tools entry '{entry.strip()}' grants gh/dotnet/docker with a wildcard verb; list verbs explicitly")
            if FORBIDDEN_TOOLS.search(entry):
                report(agent, tools_line, f"tools entry '{entry.strip()}' can print container environments or open a shell")
        for i, line in enumerate(text[body_start:], start=body_start + 1):
            if risk := unforbidden_risk(line):
                report(agent, i, f"risky instruction '{risk}' without a 'never'/'do not' in its clause")
            if re.match(r"^#+\s*Standing rules", line):
                report(agent, i, "copied 'Standing rules' section; CLAUDE.md is the single source (subagents inherit it)")
            if "Edit" not in tools and EDIT_VERBS.search(line):
                report(agent, i, f"text says it changes existing files ('{EDIT_VERBS.search(line).group(0)}') but tools lack Edit")

    for skill_dir in skill_dirs:
        skill_md = skill_dir / "SKILL.md"
        data, _ = frontmatter(skill_md)
        for key in ("name", "description"):
            if key not in data:
                report(skill_md, 1, f"frontmatter has no '{key}'")
        if data.get("name") and data["name"] != skill_dir.name:
            report(skill_md, 2, f"name '{data['name']}' differs from folder name '{skill_dir.name}'")
        if data.get("name") and not NAME.fullmatch(data["name"]):
            report(skill_md, 2, f"name '{data['name']}' must match {NAME.pattern}")
        for key in sorted(set(data) - SKILL_FRONTMATTER_KEYS):
            report(skill_md, 1, f"frontmatter key '{key}' is not allowed in a skill (allowed: {', '.join(sorted(SKILL_FRONTMATTER_KEYS))})")
        for i, line in enumerate(skill_md.read_text(encoding="utf-8").splitlines(), start=1):
            if risk := unforbidden_risk(line):
                report(skill_md, i, f"risky instruction '{risk}' without a 'never'/'do not' in its clause")
            for m in LOCAL_REF.finditer(line):
                if not (skill_dir / m.group(1)).exists():
                    report(skill_md, i, f"references {m.group(1)}, which does not exist in {skill_dir.name}/")

    check_boundaries(CLAUDE / "boundaries.json", agent_names)
    check_roster()
    pack_patterns: list = []
    own_patterns: list = []
    warnings: list[str] = []
    try:
        project = load_project(PROJECT_FILE)
        pack_patterns = project["pack_lint_patterns"]
        own_patterns = [(p["pattern"], p["message"], p.get("allowed_if")) for p in project.get("lint_patterns", [])]
    except ProjectConfigError as exc:
        problems.append(f"{PROJECT_REL}:{exc.line}: {exc.problem}")
    else:
        if problem := capture_folder_problem(project["usage"]["capture_folder"]):
            problems.append(f"{PROJECT_REL}:1: {problem}")
        try:  # an unusable boundaries.json is already a problem (check 6); no warning without its deny list
            config = parse_boundaries((CLAUDE / "boundaries.json").read_text(encoding="utf-8-sig"))
            deny = config.get("deny", [])
        except (OSError, ValueError, ConfigError, RecursionError):
            deny = None
        if deny is not None:
            warnings = build_logic_warnings(project["packs"], deny)
            if problem := capture_deny_problem(project["usage"]["capture_folder"], deny):
                problems.append(f".claude/boundaries.json:1: {problem}")
            warnings += signers_lane_warning(project["lock"]["signers_file"], config.get("agents", {}), deny)
            if "revoked_file" in project["lock"]:
                warnings += signers_lane_warning(project["lock"]["revoked_file"], config.get("agents", {}), deny,
                                                 "the revoked-keys file", "lock.revoked_file")
            warnings += lane_beyond_default_warnings(config.get("agents", {}))
            warnings += own_agent_rule_warnings(agent_names, config.get("agents", {}))
        warnings += signer_expiry_warnings(project["lock"]["signers_file"])
        warnings += revoked_ci_warnings(project["lock"].get("revoked_file"))
        references = project["secret_guard"].get("reference_folders", [])
        if references:
            try:
                settings = json.loads((CLAUDE / "settings.json").read_text(encoding="utf-8-sig"))
            except (OSError, ValueError, RecursionError):
                settings = None  # floors.py reports an unreadable settings.json
            problems.extend(reference_folder_problems(references, deny, settings))
            warnings += reference_shell_rule_warnings(references, settings)

    for path in sorted(text_files) + claude_md:
        if path in unreadable:
            continue
        # project.json holds the project's own patterns, as pack data holds the packs'
        check_banned(path, BANNED + pack_patterns + (own_patterns if path.resolve() != PROJECT_FILE.resolve() else []))
        if path.suffix == ".md":
            check_skill_refs(path, skills)

    devcontainer = ROOT / ".devcontainer"
    if devcontainer.exists():
        # Only files that can define host mounts, capabilities or privileges. Other files (e.g.
        # managed-settings.json) name in-container paths. Parse-based rewrite: O-1, tracked in.
        mount_files = [p for p in devcontainer.rglob("*") if p.is_file() and (
            p.name in {"docker-compose.yml", "devcontainer.json", ".devcontainer.json"}
            or re.fullmatch(r"compose(\.[\w-]+)?\.ya?ml", p.name)  # compose.yaml and overlays (compose.docker.yaml)
            or p.name.startswith("Dockerfile") or p.name.endswith(".Dockerfile"))]
        for path in sorted(mount_files):
            for i, line in enumerate(path.read_text(encoding="utf-8", errors="ignore").splitlines(), start=1):
                if line.lstrip().startswith(("#", "//")):
                    continue
                for pattern, msg in SANDBOX_FORBIDDEN:
                    allowed = pattern in SANDBOX_RUNG_PATTERNS and SANDBOX_RUNG_MARKER in line
                    if re.search(pattern, line, re.IGNORECASE) and not allowed:
                        report(path, i, f"sandbox-config: {msg}")
            if path.suffix in {".yaml", ".yml"}:
                check_compose_lists(path)

    for p in problems:
        print(p)
    for w in warnings:  # printed, never counted: they do not change the exit code
        print(w)
    print(f"claude-lint: {len(problems)} problem(s)" if problems else
          f"claude-lint: OK ({len(agents)} agents, {len(skill_dirs)} skills)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
