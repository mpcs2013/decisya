#!/usr/bin/env python3
"""PreToolUse hook (Write|Edit|MultiEdit|NotebookEdit): keep each project agent inside its paths.

Reads the hook payload on stdin. When it comes from a subagent listed in
.claude/boundaries.json (field agent_type), the target file must match one of
that agent's globs and none of the shared deny globs; otherwise the call is
denied with a reason the agent sees. Calls from the main session (no
agent_type) and from agents not listed are left to the normal permission flow.
Any error in this script fails open (exit 0, no decision) so a broken hook
never blocks work; the lint and gate checks still apply.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def glob_to_regex(pattern: str) -> re.Pattern[str]:
    out = ""
    i = 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out += ".*"
            i += 2
        elif pattern[i] == "*":
            out += "[^/]*"
            i += 1
        else:
            out += re.escape(pattern[i])
            i += 1
    return re.compile(f"^{out}$")


def decide(payload: dict, config: dict) -> tuple[str, str] | None:
    agent = payload.get("agent_type")
    allowed = config["agents"].get(agent) if agent else None
    if allowed is None:
        return None
    tool_input = payload.get("tool_input") or {}
    target = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not target:
        return None
    path = Path(target)
    if not path.is_absolute():
        path = Path(payload.get("cwd") or ROOT) / path
    try:
        rel = path.resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return "deny", f"{agent} may only write inside the repository (got {target})."
    if any(glob_to_regex(g).match(rel) for g in config.get("deny", [])):
        return "deny", f"{rel} is written only by the main session (orchestrator), not by {agent}."
    if any(glob_to_regex(g).match(rel) for g in allowed):
        return None
    return "deny", f"{agent} may write only {', '.join(allowed)} (see .claude/boundaries.json); {rel} is outside that. Report the needed change instead."


def main() -> int:
    try:
        payload = json.load(sys.stdin)
        config = json.loads((ROOT / ".claude" / "boundaries.json").read_text(encoding="utf-8"))
        result = decide(payload, config)
    except Exception as exc:  # fail open, but say so on stderr
        print(f"agent_boundaries hook error (allowing): {exc}", file=sys.stderr)
        return 0
    if result:
        decision, reason = result
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": decision, "permissionDecisionReason": reason}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
