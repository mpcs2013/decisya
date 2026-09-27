"""Shared parsing for lint.py and roster.py (#76, docs/architecture/roster-docs.md D3).

Standard library only; no import-time side effects beyond constants and re.compile (G4-76-16).
"""
from __future__ import annotations

import json
import re

SKILL_REF = re.compile(r"`([a-z0-9][a-z0-9-]*)` skill|\bskill `([a-z0-9][a-z0-9-]*)`|\bthe `([a-z0-9][a-z0-9-]*)`\s+skill")


def parse_frontmatter(text: str) -> tuple[dict[str, str], int, list[tuple[int, str]]]:
    """(key/value pairs, index of the first body line, [(line, problem)]). The rules match what
    Claude Code's YAML loader rejects: unquoted values containing ': ' or starting with a YAML
    indicator, malformed or unclosed quotes, block lists (a line that is not 'key: value')."""
    lines = text.splitlines()
    problems: list[tuple[int, str]] = []
    if not lines or lines[0] != "---":
        return {}, 0, [(1, "missing frontmatter (--- on line 1)")]
    data: dict[str, str] = {}
    for i, line in enumerate(lines[1:], start=2):
        if line == "---":
            return data, i, problems
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        m = re.match(r"^([A-Za-z][\w-]*):\s*(.*)$", line)
        if not m:
            problems.append((i, f"frontmatter line is not 'key: value': {line[:60]}"))
            continue
        key, value = m.groups()
        if value.startswith('"'):
            try:
                value = json.loads(value)
            except json.JSONDecodeError:
                problems.append((i, f"'{key}' is a malformed double-quoted string"))
        elif value.startswith("'"):
            if not value.endswith("'"):
                problems.append((i, f"'{key}' single-quoted string is not closed"))
            value = value[1:-1].replace("''", "'")
        elif ": " in value or value.endswith(":") or value[:1] in "[{&*!|>%@`":
            problems.append((i, f"'{key}' value must be quoted (contains ': ' or a YAML indicator; the file would fail to load)"))
        data[key] = value
    problems.append((1, "frontmatter is not closed with ---"))
    return data, 0, problems


def skill_refs(body: str) -> list[str]:
    """Skill names referenced in a text, in order of first appearance, deduplicated."""
    seen: list[str] = []
    for m in SKILL_REF.finditer(body):
        name = next(g for g in m.groups() if g)
        if name not in seen:
            seen.append(name)
    return seen
