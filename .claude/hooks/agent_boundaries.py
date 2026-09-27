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
try:
    import _hooklib as lib  # noqa: E402
except Exception as _exc:  # noqa: BLE001 - G6-39-10: the listed set is unknown, so deny any subagent
    import json
    try:
        _agent = json.load(sys.stdin).get("agent_type") or ""
    except Exception:  # noqa: BLE001
        _agent = ""
    print(f"agent_boundaries: cannot load _hooklib ({type(_exc).__name__})", file=sys.stderr)
    if _agent:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                                 "permissionDecisionReason": f"agent_boundaries error (fail closed for "
                                                 f"{_agent}): {type(_exc).__name__} loading _hooklib."}}))
    sys.exit(0)

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
    ("agent.gh-extension", r"\bgh\s+(extension|extensions|ext)\s+(install|upgrade|create)\b"),
    ("agent.aspire-add", r"\baspire\s+(add|update)\b"),
    ("agent.pre-commit-remote", r"\bpre-commit\s+(try-repo|autoupdate)\b"),
]
_DENIED = [(rule, re.compile(pattern, re.IGNORECASE)) for rule, pattern in AGENT_DENIED_COMMANDS]

# Docker is an allow-list, not a deny-list (#74 G4-74-05; threat model agent-roster.md R-02).
# Any `docker` / `docker-compose` invocation by a listed agent is denied unless its subcommand
# matches that agent's list, which is data: the `docker` key of .claude/boundaries.json (#76 D1),
# validated by _hooklib (no inspect/exec/cp/compose config: they print container environments,
# which hold generated secrets, R-01). There is no in-code fallback list (G4-76-08): an agent
# without an entry gets no Docker command, and an unreadable config denies Docker (G4-76-07).
_DOCKER_PROGRAMS = {"docker", "docker-compose"}
_DOCKER_GLOBAL_WITH_VALUE = {"-h", "--host", "--context", "-c", "--config", "-l", "--log-level"}
_DOCKER_GLOBAL_FLAGS = {"-d", "--debug"}
_DOCKER_SOCKET = re.compile(r"docker\.sock|docker_engine", re.IGNORECASE)


def _program(token: str) -> str:
    """`/usr/bin/docker`, `C:/x/docker.exe` and `DOCKER` all read as `docker`."""
    return re.sub(r"\.exe$", "", re.split(r"[/\\]", token)[-1], flags=re.IGNORECASE).lower()


def docker_subcommands(command: str) -> list[str]:
    """The Docker subcommand of EVERY docker invocation in the command, normalised (G6-74-01/02):
    quotes and backslashes are deleted (so `d''ocker` and `do\\cker` read as `docker`); the command
    is split on every shell separator, including `;&|`, newlines, `()`, `$`, backticks, `<>` and
    `{}`, so a docker call inside a subshell, command substitution or redirection is its own
    segment; every `docker`/`docker-compose` token (path-qualified or `.exe`) is checked, not only
    the first. `.exe`, docker's global flags and `docker-compose` (read as `compose`) are
    normalised. A bare `docker` with nothing after it yields an empty subcommand, which no
    allow-list matches. Two views are checked (G6-74-10): backslashes deleted (`do\\cker`) and
    backslashes read as `/` (`C:\\...\\docker.exe`)."""
    unquoted = re.sub(r"['\"]", "", command)
    found = []
    for text in (unquoted.replace("\\", ""), unquoted.replace("\\", "/")):
        found += _docker_subcommands_in(text)
    return found


def _docker_subcommands_in(text: str) -> list[str]:
    found = []
    for segment in re.split(r"[;&|\n()$`<>{}]+", text):
        tokens = segment.split()
        programs = [_program(t) for t in tokens]
        for i, program in enumerate(programs):
            if program not in _DOCKER_PROGRAMS:
                continue
            rest = [t.lower() for t in tokens[i + 1:]]
            if program == "docker-compose":
                rest = ["compose", *rest]
            while rest and rest[0].startswith("-"):
                flag = rest[0].split("=", 1)[0]
                if flag in _DOCKER_GLOBAL_WITH_VALUE:
                    rest = rest[1:] if "=" in rest[0] else rest[2:]
                elif flag in _DOCKER_GLOBAL_FLAGS:
                    rest = rest[1:]
                else:
                    break
            found.append(" ".join(rest))
    return found


def decide_docker(command: str, agent: str, allow: list[re.Pattern[str]] | None = None) -> tuple[str, str, None] | None:
    """`allow` is the agent's validated allow-list; when None it is loaded here, and any load or
    validation error propagates (main() denies it as deny-error) whenever the command runs Docker."""
    if _DOCKER_SOCKET.search(command):
        return ("agent.docker-socket", f"{agent} may not reach the Docker API directly. Report what you need; "
                "Marco runs it.", None)
    subcommands = docker_subcommands(command)
    if not subcommands:
        return None
    if allow is None:
        allow = lib.docker_patterns(lib.load_boundaries(), agent)
    for sub in subcommands:
        if not lib.docker_allows(allow, sub):
            return ("agent.docker", f"{agent} may not run this Docker command (containers and volumes are "
                    "Marco's). Report what you need; Marco runs it.", None)
    return None


def policy_views(command: str) -> tuple[str, str]:
    """The command as written, and a copy without quotes/backslashes, `.exe` suffixes and gh's
    global repo flags, so `dotnet "package" add`, `dotnet.exe` and `gh -R x issue close` match
    too (G6-39-09)."""
    text = re.sub(r"['\"\\]", "", command)
    text = re.sub(r"\b(dotnet|gh|aspire|pre-commit)\.exe\b", r"\1", text, flags=re.IGNORECASE)
    text = re.sub(r"\bgh(?:\s+(?:-R|--repo|--hostname)(?:=|\s+)\S+)+", "gh", text, flags=re.IGNORECASE)
    return command, text


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


def decide_bash(payload: dict, agent: str, config: dict | None = None) -> tuple[str, str, None] | None:
    command = (payload.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        raise ValueError("missing command")
    if len(command) > lib.MAX_COMMAND:
        return "input.too-long", f"{agent}: command too long to check.", None
    docker = decide_docker(command, agent, lib.docker_patterns(config, agent) if config is not None else None)
    if docker:
        return docker
    views = policy_views(command)
    for rule, pattern in _DENIED:
        if any(pattern.search(view) for view in views):
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
            result = decide_bash(payload, agent, config)
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
