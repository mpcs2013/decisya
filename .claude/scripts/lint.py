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
from _hooklib import ConfigError, validate_boundaries  # noqa: E402  (one schema for lint and hook)

# (regex, message, allowed-if-line-matches)
BANNED = [
    (r"dotnet test[^\n`]*\s--filter\s", "use --filter-trait/--filter-not-trait, not --filter (consistency with CI)", None),
    (r"--logger\b", "Microsoft.Testing.Platform rejects --logger (exit 5); use --report-xunit-trx", r"\b[Nn]ever\b"),
    (r"FluentAssertions", "FluentAssertions v8+ needs a paid license; use AwesomeAssertions", r"\b[Nn]ever\b"),
    (r"\bnet11(\.0)?\b", "the repo targets net10.0 (ADR-0009)", None),
    (r"dotnet new xunit\b(?!3)", "dotnet new xunit creates xUnit v2 on VSTest; use module-scaffold assets", r"\b[Nn]ot use\b|\b[Nn]ever\b|\bDo \*\*not\*\*"),
    (r"\bV3(\.\d+)*\s+[Ss]ession|\bV4\s+[Aa]ccess control|\bV5\s+Validation and encoding|\bV7\s+Error handling", "ASVS 4.0 chapter label; ASVS 5.0 numbering differs (see asvs-checklist references)", None),
    (r"\bgit diff main\b", "diff against origin/main...HEAD; local main may be stale", None),
    (r"dotnet test[^\n`]*--filter-(not-)?trait", "a solution-wide trait filter exits 8 when any test project has no match; add --project <test project> (or --ignore-exit-code 8)", r"--project\s|--ignore-exit-code 8"),
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
SKILL_REF = re.compile(r"`([a-z0-9][a-z0-9-]*)` skill|\bskill `([a-z0-9][a-z0-9-]*)`|\bthe `([a-z0-9][a-z0-9-]*)`\s+skill")
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
    lines = path.read_text(encoding="utf-8").splitlines()
    if not lines or lines[0] != "---":
        report(path, 1, "missing frontmatter (--- on line 1)")
        return {}, 0
    data: dict[str, str] = {}
    for i, line in enumerate(lines[1:], start=2):
        if line == "---":
            return data, i
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        m = re.match(r"^([A-Za-z][\w-]*):\s*(.*)$", line)
        if not m:
            report(path, i, f"frontmatter line is not 'key: value': {line[:60]}")
            continue
        key, value = m.groups()
        if value.startswith('"'):
            try:
                value = json.loads(value)
            except json.JSONDecodeError:
                report(path, i, f"'{key}' is a malformed double-quoted string")
        elif value.startswith("'"):
            if not value.endswith("'"):
                report(path, i, f"'{key}' single-quoted string is not closed")
            value = value[1:-1].replace("''", "'")
        elif ": " in value or value.endswith(":") or value[:1] in "[{&*!|>%@`":
            report(path, i, f"'{key}' value must be quoted (contains ': ' or a YAML indicator; the file would fail to load)")
        data[key] = value
    report(path, 1, "frontmatter is not closed with ---")
    return data, 0


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
WORD = re.compile(r"-{0,2}[A-Za-z][\w-]*")


def wildcard_grant(entry: str) -> bool:
    """True when a Bash(gh ...) or Bash(dotnet ...) tool entry leaves the verb to a wildcard."""
    m = re.fullmatch(r"\s*Bash\((.*)\)\s*", entry)
    if not m:
        return False
    tokens = m.group(1).split()
    if not tokens or not re.match(r"(gh|dotnet)(\*|$)", tokens[0]):
        return False
    if tokens[0] not in ("gh", "dotnet"):
        return True  # Bash(gh*) / Bash(dotnet*)
    fixed = 2 if tokens[0] == "gh" or (len(tokens) > 1 and tokens[1] in DOTNET_GROUPS) else 1
    if len(tokens) <= fixed:
        return True
    words, verb = tokens[1:fixed], tokens[fixed]
    return not all(WORD.fullmatch(w) for w in words) or not WORD.fullmatch(verb.removesuffix("*"))


def check_boundaries(path: Path, agent_names: dict[str, Path]) -> None:
    """Schema, then agents keys == frontmatter names in both directions (G4-39-06, 07)."""
    if not path.exists():
        report(path.parent, 1, "boundaries.json is missing")
        return
    text = path.read_text(encoding="utf-8-sig")
    try:
        config = validate_boundaries(json.loads(text))
    except json.JSONDecodeError as exc:
        report(path, exc.lineno, f"boundaries.json is not valid JSON: {exc.msg}")
        return
    except ConfigError as exc:
        report(path, 1, f"boundaries.json schema: {exc}")
        return
    lines = text.splitlines()
    for name in config["agents"]:
        if name not in agent_names:
            line = next((i for i, l in enumerate(lines, start=1) if f'"{name}"' in l), 1)
            report(path, line, f"boundaries for '{name}', but no .claude/agents/*.md has name '{name}'")
    for name, agent in sorted(agent_names.items()):
        if name not in config["agents"]:
            report(agent, 2, f"agent '{name}' has no boundaries.json entry (use [] for an agent that never writes)")


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
        tools = {t.strip().split("(")[0] for t in data.get("tools", "").split(",")}
        for entry in data.get("tools", "").split(","):
            if wildcard_grant(entry):
                line = next((i for i, l in enumerate(text, start=1) if l.startswith("tools:")), 1)
                report(agent, line, f"tools entry '{entry.strip()}' grants gh/dotnet with a wildcard verb; list verbs explicitly")
        for i, line in enumerate(text[body_start:], start=body_start + 1):
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
        for i, line in enumerate(skill_md.read_text(encoding="utf-8").splitlines(), start=1):
            for m in LOCAL_REF.finditer(line):
                if not (skill_dir / m.group(1)).exists():
                    report(skill_md, i, f"references {m.group(1)}, which does not exist in {skill_dir.name}/")

    check_boundaries(CLAUDE / "boundaries.json", agent_names)

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
