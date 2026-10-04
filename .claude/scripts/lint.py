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
     names in both directions (G4-39-06, 07).
  7. Agent tools never grant a gh or dotnet command group with a wildcard verb (G4-39-12).
  8. G4 routing in boundaries.json names only paths its agent can write (#76 G4-76-24).
  9. The generated roster blocks match the config (roster.py --check, #76).
 10. Every Bash(...) tools entry is an exact command or ends in ' *' (#114 D2 grammar).
 11. No bare 'Bash' tools entry (#114).
 12. Agent tools are only Read, Grep, Glob, Write, Edit, MultiEdit, NotebookEdit and Bash(...),
     the tools the hook sees and can freeze; only name, description, tools and model in the
     frontmatter, on one tools: line, with no ',' inside Bash(...) (#114 G4-114-05).
 13. Check 7 also covers aspire and pre-commit (#114).
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLAUDE = ROOT / ".claude"
SELF = Path(__file__).resolve()
sys.path.insert(0, str(SELF.parents[1] / "hooks"))
from _hooklib import (AGENT_FRONTMATTER_KEYS, AGENT_TOOL_NAMES, NAME, ConfigError, _valid_rel,  # noqa: E402
                      parse_boundaries, pattern_body, route_globs, tool_entries, tools_line)  # one schema for lint and hook
sys.path.insert(0, str(SELF.parent))
from _claudecfg import SKILL_REF, parse_frontmatter  # noqa: E402  (one parser for lint and roster)

# (regex, message, allowed-if-line-matches)
BANNED = [
    (r"dotnet test[^\n`]*\s--filter\s", "use --filter-trait/--filter-not-trait, not --filter (consistency with CI)", None),
    (r"--logger\b", "Microsoft.Testing.Platform rejects --logger (exit 5); use --report-xunit-trx", r"\b[Nn]ever\b"),
    (r"FluentAssertions", "FluentAssertions v8+ needs a paid license; use AwesomeAssertions", r"\b[Nn]ever\b"),
    (r"\bnet11(\.0)?\b", "the repo targets net10.0 (ADR-0009)", None),
    (r"dotnet new xunit\b(?!3)", "dotnet new xunit creates xUnit v2 on VSTest; use module-scaffold assets", r"\b[Nn]ot use\b|\b[Nn]ever\b|\bDo \*\*not\*\*"),
    (r"\bV3(\.\d+)*\s+[Ss]ession|\bV4\s+[Aa]ccess control|\bV5\s+Validation and encoding|\bV7\s+Error handling", "ASVS 4.0 chapter label; ASVS 5.0 numbering differs (see asvs-checklist references)", None),
    (r"\bgit diff main\b", "diff against origin/main...HEAD; local main may be stale", None),
    # Inclusion filters only: run solution-wide, every project without that trait exits 8 (#17 CI).
    # Exclusion filters (--filter-not-trait) match in every project that has a plain unit test,
    # and are what CI's unit step and the pre-push hook run (#72).
    (r"dotnet test[^\n`]*--filter-trait", "a solution-wide inclusion trait filter exits 8 when any test project has no match; add --project <test project> (or --ignore-exit-code 8)", r"--project\s|--ignore-exit-code 8"),
]
# ADR-0010: the agent sandbox never sees the host Docker daemon or host secret folders.
SANDBOX_FORBIDDEN = [
    (r"docker\.sock", "mounts the host Docker socket (full host control)"),
    (r"UserSecrets|usersecrets", "mounts dotnet user-secrets"),
    (r"localEnv:(USERPROFILE|HOME)\}?[/\\]\.(ssh|claude|aws|azure|kube|docker)\b", "mounts a host credential folder"),
    (r"(^|[\s\"'=])(~|\$HOME|\$\{HOME\})[/\\]\.(ssh|aws|azure|kube)\b", "mounts a host credential folder"),
    (r"SSH_AUTH_SOCK", "forwards the SSH agent"),
    # #41 threat model (G4-41-02 stop rule, O-41-01): never, no marker can allow these.
    (r"--privileged|\"privileged\"\s*:\s*true|privileged:\s*true", "privileged container (never; #41 fallback is no sidecar)"),
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

# #41 G6 (N41-01, N41-04): the only capabilities and devices any sandbox service may add.
SANDBOX_ALLOWED_CAPS = {"SETUID", "SETGID", "SYS_CHROOT"}
SANDBOX_ALLOWED_DEVICES = {"/dev/net/tun"}


def check_compose_lists(path: Path) -> None:
    """cap_add and devices entries must be on the allow-lists, whether written as a flow list on
    the same line, a flow list starting on the next line, a flow list spanning several lines, or a
    block list (#41 N41-01, N41-04, N41-16). In overlays (every compose file except the base
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
            report(path, n, "sandbox-config: bind mount in a compose overlay (ADR-0010, #41 N41-16)")
        in_volumes = bool(parents) and parents[-1][1] == "volumes"
        if in_volumes and re.match(r"^\s*-\s*[\"']?[/.~$][^\s\"',]*:", text):
            report(path, n, "sandbox-config: short-syntax host path mount in a compose overlay (ADR-0010, #41 N41-16)")
        km = re.match(r"^\s*[\"']?([\w.-]+)[\"']?\s*:\s*$", text)
        if km:
            parents.append((indent, km.group(1)))


def _check_list_item(path: Path, line: int, key: str, item: str) -> None:
    if key == "cap_add":
        cap = item.upper().removeprefix("CAP_")
        if cap not in SANDBOX_ALLOWED_CAPS:
            report(path, line, f"sandbox-config: cap_add {item} is not in {sorted(SANDBOX_ALLOWED_CAPS)} (ADR-0010, #41 N41-01)")
    else:
        device = item.split(":", 1)[0]
        if device not in SANDBOX_ALLOWED_DEVICES:
            report(path, line, f"sandbox-config: device {item} is not in {sorted(SANDBOX_ALLOWED_DEVICES)} (ADR-0010, #41 N41-04)")


def report(path: Path, line: int, msg: str) -> None:
    problems.append(f"{path.relative_to(ROOT).as_posix()}:{line}: {msg}")


def frontmatter(path: Path) -> tuple[dict[str, str], int]:
    data, body_start, found = parse_frontmatter(path.read_text(encoding="utf-8"))
    for line, msg in found:
        report(path, line, msg)
    return data, body_start


def check_banned(path: Path) -> None:
    for i, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        for pattern, msg, allowed in BANNED:
            if re.search(pattern, line) and not (allowed and re.search(allowed, line)):
                report(path, i, f"banned pattern /{pattern}/: {msg}")


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
# #74 G4-74-06: tool grants that print container environments (generated secrets) or open a shell.
FORBIDDEN_TOOLS = re.compile(r"docker\s+(container\s+)?(inspect|exec|cp)\b|docker\s+compose\s+config\b", re.IGNORECASE)
# #74 G4-74-09: skill frontmatter keys. Skills are trusted instructions; they must not grant tools,
# register hooks or switch models. The last two keys are already used by the issue skill.
SKILL_FRONTMATTER_KEYS = {"name", "description", "argument-hint", "disable-model-invocation"}
# #74 G4-74-10: instructions that must not appear in skills or agents unless the line forbids them.
RISKY_INSTRUCTIONS = re.compile(
    r"docker\s+(container\s+)?inspect|docker\s+exec|docker\s+(volume\s+)?(rm|prune)\b|docker\s+run\b"
    r"|compose\s+down\s+-v|user-secrets\s+list|WithBindMount|docker\.sock|--privileged"
    r"|TESTCONTAINERS_RYUK_DISABLED|sslRequired\"?\s*:\s*\"?none", re.IGNORECASE)
FORBIDDING = re.compile(r"\b(never|do not|not)\b", re.IGNORECASE)


def unforbidden_risk(line: str) -> str | None:
    """The first risky instruction on the line that is not negated in its own clause, else None.
    The negation must come before the match and after the last clause boundary (G6-74-03):
    "never run docker exec" passes; "if it did not start, run docker exec" does not."""
    for m in RISKY_INSTRUCTIONS.finditer(line):
        clause = re.split(r"[.;:!?,]\s", line[:m.start()])[-1]
        if not FORBIDDING.search(clause):
            return m.group(0)
    return None
WORD = re.compile(r"-{0,2}[A-Za-z][\w-]*")
# Verbs the hook denies to agents (agent_boundaries.AGENT_DENIED_COMMANDS). A trailing-* verb that
# is a strict prefix of one of these (e.g. `dotnet p*`) grants it by accident (G6-39-11).
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
    wildcard. `x *` (#114 grammar) completes the last word; `x*` leaves it open."""
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
    if tokens[0] in ("aspire", "pre-commit"):  # #114 lint check 13: `aspire *` includes exec and deploy
        return len(tokens) < 2 or tokens[1].endswith("*")
    if tokens[0] == "docker":  # #74 G4-74-07: `docker volume *`, `docker compose *`, `docker *`
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
    """#76: the generated blocks match the config. A stale block and any exception each become one
    problem (exception class only) so lint never passes silently (G4-76-17)."""
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
    import importlib.util
    spec = importlib.util.spec_from_file_location("roster", SELF.parent / "roster.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def check_boundaries(path: Path, agent_names: dict[str, Path]) -> None:
    """Schema, then agents keys == frontmatter names in both directions (G4-39-06, 07)."""
    if not path.exists():
        report(path.parent, 1, "boundaries.json is missing")
        return
    text = path.read_text(encoding="utf-8-sig")
    try:
        config = parse_boundaries(text)
    except json.JSONDecodeError as exc:
        report(path, exc.lineno, f"boundaries.json is not valid JSON: {exc.msg}")
        return
    except ConfigError as exc:
        report(path, 1, f"boundaries.json schema: {exc}")
        return
    lines = text.splitlines()
    if (ROOT / "docs" / "ai" / "README.md").exists():  # the real repository (test trees have no docs/)
        for key in ("docker", "routing"):
            if key not in config:
                report(path, 1, f"boundaries.json has no '{key}' key; roster.py and the hook need it (#76)")
    deny = [_glob_regex(g) for g in config.get("deny", [])]
    for i, row in enumerate(config.get("routing", {}).get("G4", [])):
        line = next((n for n, l in enumerate(lines, start=1) if f'"agent": "{row["agent"]}"' in l), 1)
        lanes = config["agents"][row["agent"]]
        for glob in route_globs(row["paths"]):
            # G2 D2 and G4-76-24: a route names only paths its agent can actually write.
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
    """#114 checks 10 to 12 and G4-114-05: the hook enforces the Bash patterns read by the same
    _hooklib functions, and only tools its matcher sees, so the freeze covers every tool with side
    effects (SubagentHandback stays the only exemption)."""
    line = next((i for i, l in enumerate(text, start=1) if l.startswith("tools:")), 1)
    for key in sorted(set(data) - AGENT_FRONTMATTER_KEYS):
        report(agent, 1, f"frontmatter key '{key}' is not allowed in an agent (allowed: "
                         f"{', '.join(sorted(AGENT_FRONTMATTER_KEYS))}; #114 G4-114-05)")
    try:
        value = tools_line("\n".join(text))
    except ConfigError as exc:
        report(agent, 1, f"{exc} (#114 G4-114-05)")
        return
    for entry in tool_entries(value):
        if entry == "Bash":
            report(agent, line, "bare 'Bash' grants every command; list Bash(...) patterns (#114 check 11)")
        elif entry.startswith("Bash(") != entry.endswith(")") or entry.count("(") > 1:
            report(agent, line, f"tools entry '{entry[:60]}' is cut by a ',' inside Bash(...) (#114 G4-114-05)")
        elif entry.startswith("Bash("):
            try:
                pattern_body(entry[5:-1])
            except ConfigError as exc:
                report(agent, line, f"{exc} (#114 check 10)")
        elif entry not in AGENT_TOOL_NAMES:
            report(agent, line, f"tool '{entry}' is not one the hook can freeze (allowed: "
                                f"{', '.join(sorted(AGENT_TOOL_NAMES))}, Bash(...); #114 check 12)")


def main() -> int:
    agents = sorted((CLAUDE / "agents").glob("*.md"))
    skill_dirs = sorted(p.parent for p in (CLAUDE / "skills").glob("*/SKILL.md"))
    skills = {d.name for d in skill_dirs}
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
                report(agent, tools_line, f"tools entry '{entry.strip()}' can print container environments or open a shell (#74 R-01)")
        for i, line in enumerate(text[body_start:], start=body_start + 1):
            if risk := unforbidden_risk(line):
                report(agent, i, f"risky instruction '{risk}' without a 'never'/'do not' in its clause (#74 G4-74-10)")
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
            report(skill_md, 2, f"name '{data['name']}' must match {NAME.pattern} (#76 G4-76-13)")
        for key in sorted(set(data) - SKILL_FRONTMATTER_KEYS):
            report(skill_md, 1, f"frontmatter key '{key}' is not allowed in a skill (allowed: {', '.join(sorted(SKILL_FRONTMATTER_KEYS))}; #74 G4-74-09)")
        for i, line in enumerate(skill_md.read_text(encoding="utf-8").splitlines(), start=1):
            if risk := unforbidden_risk(line):
                report(skill_md, i, f"risky instruction '{risk}' without a 'never'/'do not' in its clause (#74 G4-74-10)")
            for m in LOCAL_REF.finditer(line):
                if not (skill_dir / m.group(1)).exists():
                    report(skill_md, i, f"references {m.group(1)}, which does not exist in {skill_dir.name}/")

    check_boundaries(CLAUDE / "boundaries.json", agent_names)
    check_roster()

    text_files = [p for p in CLAUDE.rglob("*") if p.is_file() and p.suffix in {".md", ".json", ".py", ".cs", ".csproj"}
                  and "__pycache__" not in p.parts and p.resolve() != SELF]
    for path in sorted(text_files) + [ROOT / "CLAUDE.md"]:
        check_banned(path)
        if path.suffix == ".md":
            check_skill_refs(path, skills)

    devcontainer = ROOT / ".devcontainer"
    if devcontainer.exists():
        # Only files that can define host mounts, capabilities or privileges. Other files (e.g.
        # managed-settings.json) name in-container paths. Parse-based rewrite: O-1, tracked in #39.
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
                        report(path, i, f"sandbox-config: {msg} (ADR-0010)")
            if path.suffix in {".yaml", ".yml"}:
                check_compose_lists(path)

    for p in problems:
        print(p)
    print(f"claude-lint: {len(problems)} problem(s)" if problems else
          f"claude-lint: OK ({len(agents)} agents, {len(skill_dirs)} skills)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
