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
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLAUDE = ROOT / ".claude"
SELF = Path(__file__).resolve()

# (regex, message, allowed-if-line-matches)
BANNED = [
    (r"dotnet test[^\n`]*\s--filter\s", "use --filter-trait/--filter-not-trait, not --filter (consistency with CI)", None),
    (r"--logger\b", "Microsoft.Testing.Platform rejects --logger (exit 5); use --report-xunit-trx", r"\b[Nn]ever\b"),
    (r"FluentAssertions", "FluentAssertions v8+ needs a paid license; use AwesomeAssertions", r"\b[Nn]ever\b"),
    (r"\bnet11(\.0)?\b", "the repo targets net10.0 (ADR-0009)", None),
    (r"dotnet new xunit\b(?!3)", "dotnet new xunit creates xUnit v2 on VSTest; use module-scaffold assets", r"\b[Nn]ot use\b|\b[Nn]ever\b|\bDo \*\*not\*\*"),
    (r"\bV3(\.\d+)*\s+[Ss]ession|\bV4\s+[Aa]ccess control|\bV5\s+Validation and encoding|\bV7\s+Error handling", "ASVS 4.0 chapter label; ASVS 5.0 numbering differs (see asvs-checklist references)", None),
    (r"\bgit diff main\b", "diff against origin/main...HEAD; local main may be stale", None),
]
EDIT_VERBS = re.compile(r"\b(update[sd]?|keep current|kept current|add a row|a row in|maintain(s|ed)?|append(ed|s)?|edit (statuses|in place))\b", re.IGNORECASE)
SKILL_REF = re.compile(r"`([a-z0-9][a-z0-9-]*)` skill|\bskill `([a-z0-9][a-z0-9-]*)`|\bthe `([a-z0-9][a-z0-9-]*)`\s+skill")
LOCAL_REF = re.compile(r"`((?:\.\./[a-z0-9-]+/)?(?:references|assets|scripts)/[\w./-]+)`")
SCRIPT_REF = re.compile(r"\.claude/(?:scripts|hooks)/[\w.-]+\.py")

problems: list[str] = []


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


def main() -> int:
    agents = sorted((CLAUDE / "agents").glob("*.md"))
    skill_dirs = sorted(p.parent for p in (CLAUDE / "skills").glob("*/SKILL.md"))
    skills = {d.name for d in skill_dirs}
    agent_names = {a.stem for a in agents}

    for agent in agents:
        data, body_start = frontmatter(agent)
        for key in ("name", "description", "tools"):
            if key not in data:
                report(agent, 1, f"frontmatter has no '{key}'")
        if data.get("name") and data["name"] != agent.stem:
            report(agent, 2, f"name '{data['name']}' differs from file name '{agent.stem}'")
        text = agent.read_text(encoding="utf-8").splitlines()
        tools = {t.strip().split("(")[0] for t in data.get("tools", "").split(",")}
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

    boundaries = CLAUDE / "boundaries.json"
    if boundaries.exists():
        for name in json.loads(boundaries.read_text(encoding="utf-8")).get("agents", {}):
            if name not in agent_names:
                report(boundaries, 1, f"boundaries for '{name}', which has no .claude/agents/{name}.md")

    text_files = [p for p in CLAUDE.rglob("*") if p.is_file() and p.suffix in {".md", ".json", ".py", ".cs", ".csproj"}
                  and "__pycache__" not in p.parts and p.resolve() != SELF]
    for path in sorted(text_files) + [ROOT / "CLAUDE.md"]:
        check_banned(path)
        if path.suffix == ".md":
            check_skill_refs(path, skills)

    for p in problems:
        print(p)
    print(f"claude-lint: {len(problems)} problem(s)" if problems else
          f"claude-lint: OK ({len(agents)} agents, {len(skill_dirs)} skills)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
