"""Shared helpers for the PreToolUse hooks (issue #39, items 1 and 7; G4-39-01 to 05, 24 to 31).

Decision rules:
- stdin is not a JSON object: fail open (the agent is unknown); one stderr line.
- main session (no agent_type): unchanged behaviour; any error fails open.
- agent_type is a listed project agent: any error denies (fail closed), with a fixed reason
  that names only the hook and the exception class, never the command or file content.
- agent_type is set but not listed (built-in or plugin agent): unchanged behaviour.
The listed set is the `agents` keys of .claude/boundaries.json; if that file is unreadable or
fails validation, the frontmatter `name:` values of .claude/agents/*.md; if neither can be read,
every non-empty agent_type counts as listed.

The audit log records deny decisions only, with a fixed field allow-list; it never holds command
text, matched text, exception messages, cwd or transcript paths. Logging runs after the decision
and can never change it.
"""
from __future__ import annotations

import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LOG_DIR = ROOT / ".agent-logs"
LOG_FILE = LOG_DIR / "hooks.jsonl"
LOG_ROTATE_BYTES = 1024 * 1024
MAX_COMMAND = 256 * 1024
MAX_PATH = 4 * 1024
_TOP_KEYS = {"$comment", "deny", "agents"}


class ConfigError(Exception):
    pass


def read_payload() -> dict | None:
    """The hook payload, or None (fail open) when stdin is not a JSON object."""
    try:
        data = json.load(sys.stdin)
    except Exception as exc:  # noqa: BLE001 - any parse failure fails open
        print(f"hook: unreadable payload ({type(exc).__name__}); allowing", file=sys.stderr)
        return None
    if not isinstance(data, dict):
        print("hook: payload is not a JSON object; allowing", file=sys.stderr)
        return None
    return data


def _valid_rel(path: object) -> bool:
    return (isinstance(path, str) and path != "" and not path.startswith(("/", "\\"))
            and not re.match(r"^[A-Za-z]:", path) and ".." not in re.split(r"[/\\]", path))


def validate_boundaries(config: object) -> dict:
    """G4-39-07 schema; raises ConfigError on any deviation."""
    if not isinstance(config, dict) or not set(config) <= _TOP_KEYS or "agents" not in config:
        raise ConfigError("top-level keys must be $comment, deny, agents")
    deny = config.get("deny", [])
    if not isinstance(deny, list) or not all(_valid_rel(g) for g in deny):
        raise ConfigError("deny must be a list of repository-relative globs")
    agents = config["agents"]
    if not isinstance(agents, dict) or not all(
            isinstance(k, str) and k and isinstance(v, list) and all(_valid_rel(g) for g in v)
            for k, v in agents.items()):
        raise ConfigError("agents must map names to lists of repository-relative globs")
    return config


def load_boundaries() -> dict:
    return validate_boundaries(json.loads((ROOT / ".claude" / "boundaries.json").read_text(encoding="utf-8-sig")))


def frontmatter_names() -> set[str]:
    names = set()
    for path in (ROOT / ".claude" / "agents").glob("*.md"):
        m = re.search(r"^name:\s*[\"']?([\w-]+)", path.read_text(encoding="utf-8-sig"), re.MULTILINE)
        if m:
            names.add(m.group(1))
    return names


def listed_agents() -> tuple[set[str] | None, dict | None]:
    """(listed agent names or None if unknown, validated boundaries config or None)."""
    try:
        config = load_boundaries()
        return set(config["agents"]), config
    except Exception:  # noqa: BLE001
        pass
    try:
        names = frontmatter_names()
        return (names or None), None
    except Exception:  # noqa: BLE001
        return None, None


def is_listed(agent: str, listed: set[str] | None) -> bool:
    if not agent:
        return False
    return agent in listed if listed is not None else True


def emit(decision: str, reason: str) -> None:
    out = {"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": decision,
                                  "permissionDecisionReason": reason}}
    try:
        print(json.dumps(out))
    except Exception:  # noqa: BLE001 - writing the JSON failed: exit 2 blocks the call
        print("hook: could not write decision; blocking", file=sys.stderr)
        sys.exit(2)


def fail_closed_reason(hook: str, agent: str, exc: BaseException) -> str:
    return (f"{hook} error (fail closed for {agent}): {type(exc).__name__}. The main session can "
            "repair .claude/boundaries.json or the hook; run python .claude/scripts/lint.py.")


def audit(hook: str, payload: dict, decision: str, rule: str, path: str | None = None) -> None:
    """Append one deny record. Never raises and never affects the decision (G4-39-29)."""
    try:
        record = {
            "ts": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "hook": hook,
            "agent": payload.get("agent_type") or "main",
            "tool": payload.get("tool_name"),
            "decision": decision,
            "rule": rule,
            "session_id": payload.get("session_id"),
            "tool_use_id": payload.get("tool_use_id"),
        }
        if path is not None:
            record["path"] = path
        record = {k: (str(v)[:256] if v is not None else None) for k, v in record.items()}
        LOG_DIR.mkdir(exist_ok=True)
        if LOG_FILE.exists() and LOG_FILE.stat().st_size > LOG_ROTATE_BYTES:
            LOG_FILE.replace(LOG_FILE.with_name("hooks.jsonl.1"))
        with open(LOG_FILE, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(record, ensure_ascii=True) + "\n")
    except Exception as exc:  # noqa: BLE001
        print(f"hook: audit log not written ({type(exc).__name__})", file=sys.stderr)
