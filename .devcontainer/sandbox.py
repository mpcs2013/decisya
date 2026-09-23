#!/usr/bin/env python3
"""Host launcher for the agent sandbox (ADR-0010, issue #36; docs/architecture/agent-sandbox.md,
"Entry point and session model", G4-07).

Always invoked as:  & $env:DECISYA_PYTHON .devcontainer\\sandbox.py <command> [args...]

This file is itself read-only inside the sandbox (the `.devcontainer` overlay). It uses only
the standard library, resolves `docker` and `git` without searching the current directory
(same discipline as precheck.py; see resolve_on_path below), and runs every subprocess with
`cwd` outside the repository.

Subcommands:
  up            Runs precheck.py (refuses on failure), then brings the stack up.
  claude [args] Runs Claude Code headlessly inside `workspace`, with the Anthropic API key set
                only in this one child process's environment (G4-11). Refuses if a VS Code
                server process is running inside `workspace` (SB6 / G4-07).
  shell         An interactive shell inside `workspace`, without the API key.
  down          Stops the stack. Volumes (and therefore ~/.claude, ~/.nuget, build output) are
                kept.
  reset         `down`, then removes the sandbox-owned named volumes (home, vscode-server,
                egress logs). Use after a suspected prompt injection or credential exposure.
  attach-prep   Refuses if a `claude` process is running inside `workspace`. Otherwise stops
                the stack and removes the vscode-server volume, so no extension code an agent
                may have planted survives into a later read-only attach.
"""
from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
COMPOSE_FILE = Path(__file__).resolve().parent / "compose.yaml"
PROJECT = "decisya-sandbox"
KEY_ENV_VAR = "DECISYA_SANDBOX_ANTHROPIC_API_KEY"
SANDBOX_VOLUMES = ["decisya-sandbox-home", "decisya-sandbox-vscode", "decisya-sandbox-egress-logs"]


def resolve_on_path(name: str) -> str | None:
    """Same discipline as precheck.py: never searches the current directory (Windows'
    executable lookup does, unless NoDefaultCurrentDirectoryInExePath is set; T-05), and never
    trusts a directory that is inside the repository.

    On Windows this must never return an extensionless match: WSL/Git-for-Windows ship
    extensionless shell-script shims named exactly "docker" or "git" next to the real
    "docker.exe"/"git.exe" on some PATH entries, and passing one of those to subprocess.run
    (which does not go through cmd.exe) fails with WinError 193 ("not a valid Win32
    application") -- or worse, could succeed against the wrong tool. So on Windows this tries
    only ".exe" and ".com", in that order, and never a bare name and never ".bat"/".cmd"
    (cmd.exe re-parses those, which is a bigger footgun than the OSError, T-05)."""
    exts = [".exe", ".com"] if os.name == "nt" else [""]
    for raw_entry in os.environ.get("PATH", "").split(os.pathsep):
        entry = raw_entry.strip('"')
        if not entry or entry == ".":
            continue
        try:
            directory = Path(entry).resolve()
        except OSError:
            continue
        if not directory.is_dir():
            continue
        try:
            directory.relative_to(REPO_ROOT)
            continue
        except ValueError:
            pass
        for ext in exts:
            candidate = directory / f"{name}{ext}"
            if candidate.is_file():
                return str(candidate)
    return None


def outside_cwd() -> str:
    return tempfile.mkdtemp(prefix="decisya-sandbox-launcher-")


def require(name: str) -> str:
    path = resolve_on_path(name)
    if path is None:
        print(f"sandbox.py: '{name}' not found on PATH (resolved without searching the current directory)", file=sys.stderr)
        sys.exit(1)
    return path


def compose_args() -> list[str]:
    return ["compose", "-f", str(COMPOSE_FILE), "-p", PROJECT]


def run(args: list[str], *, check: bool, capture: bool = False) -> subprocess.CompletedProcess[str] | int:
    cwd = outside_cwd()
    if capture:
        return subprocess.run(args, cwd=cwd, capture_output=True, text=True, check=False)
    result = subprocess.run(args, cwd=cwd, check=False)
    if check and result.returncode != 0:
        sys.exit(result.returncode)
    return result.returncode


def process_running_in_workspace(docker: str, pattern: str) -> bool:
    """True if a process matching `pattern` (pgrep -f) is running inside `workspace`. False
    (not "unknown") if the container is not up at all, so `up` on a stopped stack is never
    blocked by a stale check."""
    result = run([docker, *compose_args(), "exec", "-T", "workspace", "pgrep", "-f", pattern], check=False, capture=True)
    assert isinstance(result, subprocess.CompletedProcess)
    return result.returncode == 0


def cmd_up(_: list[str]) -> int:
    python = sys.executable
    precheck = Path(__file__).resolve().parent / "precheck.py"
    precheck_result = subprocess.run([python, str(precheck)], cwd=outside_cwd(), check=False)
    if precheck_result.returncode != 0:
        print("sandbox.py: precheck failed; the sandbox was not started", file=sys.stderr)
        return precheck_result.returncode
    docker = require("docker")
    return run([docker, *compose_args(), "up", "-d", "--build", "--wait"], check=True)  # type: ignore[return-value]


def cmd_claude(args: list[str]) -> int:
    docker = require("docker")
    if process_running_in_workspace(docker, "vscode-server"):
        print("sandbox.py: refusing to start Claude Code: a VS Code server process is running "
              "inside workspace. Run 'attach-prep' first, or close the attach (G4-07).", file=sys.stderr)
        return 1
    key = os.environ.get(KEY_ENV_VAR)
    if not key:
        print(f"sandbox.py: {KEY_ENV_VAR} is not set in this host user's environment. See "
              "docs/runbooks/agent-sandbox.md for how to set it (never as ANTHROPIC_API_KEY, "
              "so host Claude sessions never pick it up).", file=sys.stderr)
        return 1
    # The key lives only in this one child process's environment (docker compose exec -e),
    # never in compose.yaml, an env file, or `docker inspect workspace` (G4-11).
    child_env = {**os.environ, "ANTHROPIC_API_KEY": key}
    cwd = outside_cwd()
    result = subprocess.run(
        [docker, *compose_args(), "exec", "-e", "ANTHROPIC_API_KEY", "workspace", "claude", *args],
        cwd=cwd, env=child_env, check=False,
    )
    return result.returncode


def cmd_shell(_: list[str]) -> int:
    docker = require("docker")
    cwd = outside_cwd()
    result = subprocess.run([docker, *compose_args(), "exec", "workspace", "bash"], cwd=cwd, check=False)
    return result.returncode


def cmd_down(_: list[str]) -> int:
    docker = require("docker")
    return run([docker, *compose_args(), "down"], check=True)  # type: ignore[return-value]


def cmd_reset(args: list[str]) -> int:
    rc = cmd_down(args)
    if rc != 0:
        return rc
    docker = require("docker")
    return run([docker, "volume", "rm", *SANDBOX_VOLUMES], check=True)  # type: ignore[return-value]


def cmd_attach_prep(_: list[str]) -> int:
    docker = require("docker")
    if process_running_in_workspace(docker, "claude"):
        print("sandbox.py: refusing: a 'claude' process is running inside workspace. Stop the "
              "session first (SB6 / G4-07 mutual exclusion).", file=sys.stderr)
        return 1
    rc = run([docker, *compose_args(), "down"], check=True)  # type: ignore[assignment]
    if rc != 0:
        return rc  # type: ignore[return-value]
    rc = run([docker, "volume", "rm", "decisya-sandbox-vscode"], check=True)  # type: ignore[assignment]
    print(
        "sandbox.py: attach-prep done. ~/.vscode-server has been removed. To attach "
        "read-only: open this folder in VS Code, 'Reopen in Container' using "
        ".devcontainer/devcontainer.json, install no extensions, and close the attach before "
        "the next 'claude' session (docs/runbooks/agent-sandbox.md)."
    )
    return rc  # type: ignore[return-value]


COMMANDS = {
    "up": cmd_up,
    "claude": cmd_claude,
    "shell": cmd_shell,
    "down": cmd_down,
    "reset": cmd_reset,
    "attach-prep": cmd_attach_prep,
}


def main(argv: list[str]) -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    if not argv or argv[0] not in COMMANDS:
        print(__doc__, file=sys.stderr)
        return 2
    return COMMANDS[argv[0]](argv[1:])


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
