#!/usr/bin/env python3
"""PreToolUse hook (Write|Edit|MultiEdit|NotebookEdit|Bash) for the project agents.

For a project agent listed in .claude/boundaries.json (payload field agent_type):
- Write/Edit/MultiEdit/NotebookEdit: the target must match one of the agent's globs and none of
  the shared deny globs (the write lanes).
- Bash: package-fetching and destructive commands are denied (issue #39 items 4 and 5, Marco's
  decision 2026-09-25: these bind agents only; the main session keeps asking for permission).
- Any error while deciding denies the call (fail closed, G4-39-01); inputs above the size caps
  are denied as too long to check (G4-39-05).
The main session and agents that are not listed keep the normal permission flow; errors there
fail open. Deny decisions are written to the local audit log (.agent-logs/hooks.jsonl).
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _hooklib as lib  # noqa: E402

HOOK = "agent_boundaries"

# Bash commands no project agent may run (G4-39-11 to 16). Matched per simple command, so the
# verb must follow the program name; arguments in between (e.g. a project path) are allowed.
AGENT_DENIED_COMMANDS = [
    ("agent.dotnet-package", r"\bdotnet\s+add\b.*\bpackage\b"),
    ("agent.dotnet-package", r"\bdotnet\s+package\s+(add|update)\b"),
    ("agent.dotnet-install", r"\bdotnet\s+(new\s+install|tool\s+(install|update)|workload\s+(install|update|restore))\b"),
    ("agent.dotnet-nuget", r"\bdotnet\s+nuget\s+add\b"),
    ("agent.gh-issue-write", r"\bgh\s+issue\s+(delete|transfer|edit|close|reopen|comment|develop|pin|unpin|lock|unlock)\b"),
    ("agent.gh-run-write", r"\bgh\s+run\s+(rerun|cancel|delete|download)\b"),
    ("agent.gh-destructive", r"\bgh\s+(repo\s+(delete|archive|edit|rename)|secret\b|variable\b|api\b|pr\s+merge\b|release\s+delete\b)"),
]
_DENIED = [(rule, re.compile(pattern)) for rule, pattern in AGENT_DENIED_COMMANDS]


def glob_to_regex(pattern: str) -> re.Pattern[str]:
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return re.compile(f"^{out}$")


def decide_write(payload: dict, config: dict, agent: str) -> tuple[str, str, str | None] | None:
    """(rule, reason, repo-relative path) for a denied write, or None."""
    allowed = config["agents"][agent]
    tool_input = payload.get("tool_input") or {}
    target = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not isinstance(target, str) or not target:
        raise ValueError("missing target path")
    if len(target) > lib.MAX_PATH:
        return "input.too-long", f"{agent}: path too long to check.", "<too-long>"
    path = Path(target)
    if not path.is_absolute():
        path = Path(payload.get("cwd") or lib.ROOT) / path
    try:
        rel = path.resolve().relative_to(lib.ROOT).as_posix()
    except ValueError:
        return "boundary.outside-repo", f"{agent} may only write inside the repository.", "<outside-repo>"
    if any(glob_to_regex(g).match(rel) for g in config.get("deny", [])):
        return "boundary.shared-deny", f"{rel} is written only by the main session (orchestrator), not by {agent}.", rel
    if any(glob_to_regex(g).match(rel) for g in allowed):
        return None
    lanes = ", ".join(allowed) if allowed else "nothing"
    return ("boundary.not-in-lane",
            f"{agent} may write only {lanes} (see .claude/boundaries.json); {rel} is outside that. Report the needed change instead.",
            rel)


def decide_bash(payload: dict, agent: str) -> tuple[str, str, None] | None:
    command = (payload.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        raise ValueError("missing command")
    if len(command) > lib.MAX_COMMAND:
        return "input.too-long", f"{agent}: command too long to check.", None
    for rule, pattern in _DENIED:
        if pattern.search(command):
            return (rule, f"{agent} may not run this command (package fetching or a destructive "
                          "GitHub/.NET operation). Report what you need; Marco runs it.", None)
    return None


def main() -> int:
    payload = lib.read_payload()
    if payload is None:
        return 0
    agent = payload.get("agent_type") or ""
    if not agent:
        return 0  # main session: the normal permission flow applies
    listed, config = lib.listed_agents()
    if not lib.is_listed(agent, listed):
        return 0  # built-in or plugin agent: unchanged
    try:
        if payload.get("tool_name") == "Bash":
            result = decide_bash(payload, agent)
        else:
            if config is None:
                raise lib.ConfigError("boundaries.json unreadable or invalid")
            if agent not in config["agents"]:
                raise lib.ConfigError("agent has no boundaries entry")
            result = decide_write(payload, config, agent)
    except Exception as exc:  # noqa: BLE001 - fail closed for a listed agent
        print(f"{HOOK}: {type(exc).__name__} for {agent}; denying", file=sys.stderr)
        lib.emit("deny", lib.fail_closed_reason(HOOK, agent, exc))
        lib.audit(HOOK, payload, "deny-error", f"error.{type(exc).__name__}")
        return 0
    if result:
        rule, reason, rel = result
        lib.emit("deny", reason)
        lib.audit(HOOK, payload, "deny", rule, rel)
    return 0


if __name__ == "__main__":
    sys.exit(main())
