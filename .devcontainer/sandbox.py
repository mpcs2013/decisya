#!/usr/bin/env python3
"""Host launcher for the agent sandbox (ADR-0010, issue #36; docs/architecture/agent-sandbox.md,
"Entry point and session model", G4-07). Issue #41
(docs/architecture/agent-sandbox-docker-sidecar.md) adds the opt-in container-engine overlay
(`up --with-docker`).

Always invoked as:  & $env:DECISYA_PYTHON .devcontainer\\sandbox.py <command> [args...]

This file is itself read-only inside the sandbox (the `.devcontainer` overlay). It uses only
the standard library, resolves `docker` and `git` without searching the current directory
(same discipline as precheck.py; see resolve_on_path below), and runs every subprocess with
`cwd` outside the repository.

Subcommands:
  up [--with-docker]
                Runs precheck.py (refuses on failure), then brings the stack up. Plain `up`
                loads only compose.yaml: the container-engine overlay is off, and a
                post-condition (G4-41-08) confirms no `docker` service container and no extra
                network survive. `up --with-docker` (issue #41) refuses while a `claude` process
                is running, refuses below the recorded Docker Engine version floor (G4-41-10),
                then builds a FRESH engine every time (G4-41-04): the `docker` container and both
                engine volumes are removed and recreated, the overlay is brought up, and the
                image allow-list (.devcontainer/engine/images.Dockerfile) is pre-loaded from the
                host before this returns. See docs/runbooks/agent-sandbox.md, "Integration
                tests", and the residual at T-41-01: a kernel exploit reachable from the agent
                reaches all of `C:` while the engine is on. Turn it off (`up`, `down`, `reset` or
                `attach-prep`) when not running integration tests.
  claude [args] Runs Claude Code headlessly inside `workspace`, with the Anthropic API key set
                only in this one child process's environment (G4-11). Refuses if a VS Code
                server process is running inside `workspace` (SB6 / G4-07), or if a claude.ai
                subscription login has left ~/.claude/.credentials.json on the home volume
                (N-01, docs/security/reviews/36.md) -- run `reset` first. Prints the container
                engine ON/OFF banner (G4-41-08) before starting.
  shell         An interactive shell inside `workspace`, without the API key. Same
                credentials-file refusal as `claude` (N-01): a login could otherwise be
                completed by hand from this shell. Same engine banner as `claude`.
  down          Stops the stack (both compose files, always -- G4-41-08). Volumes (and
                therefore ~/.claude, ~/.nuget, build output, and the engine's image store if
                present) are kept.
  reset         `down`, then removes the sandbox-owned named volumes (home, vscode-server,
                egress logs, and the two engine volumes if they exist). Use after a suspected
                prompt injection or credential exposure.
  attach-prep   Refuses if a `claude` process is running inside `workspace`. Otherwise stops
                the stack (both compose files, always -- G4-41-08, T-41-11) and removes the
                vscode-server volume, so no extension code an agent may have planted survives
                into a later read-only attach, and no orphaned `docker` sidecar survives either.
"""
from __future__ import annotations

import json
import os
import platform
import re
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEVCONTAINER_DIR = Path(__file__).resolve().parent
COMPOSE_FILE = DEVCONTAINER_DIR / "compose.yaml"
COMPOSE_DOCKER_FILE = DEVCONTAINER_DIR / "compose.docker.yaml"
IMAGES_DOCKERFILE = DEVCONTAINER_DIR / "engine" / "images.Dockerfile"
PROJECT = "decisya-sandbox"
KEY_ENV_VAR = "DECISYA_SANDBOX_ANTHROPIC_API_KEY"
SANDBOX_VOLUMES = ["decisya-sandbox-home", "decisya-sandbox-vscode", "decisya-sandbox-egress-logs"]
# Issue #41: the opt-in container-engine overlay's own volumes (never in SANDBOX_VOLUMES, so a
# plain `reset` before #41 semantics still works, and so `reset` can tolerate their absence).
ENGINE_VOLUMES = ["decisya-sandbox-engine-storage", "decisya-sandbox-engine-run"]
ENGINE_SERVICE = "docker"
# G4-41-10: recorded at G4 (docs/ai/pipeline/41.md, section 0), 2026-09-24 -- Docker Desktop
# 4.83.0 / Engine 29.6.2, kernel 6.6.87.2-microsoft-standard-WSL2. Bumped with the image-digest
# cadence, never lowered.
ENGINE_VERSION_FLOOR = (29, 6, 2)
# G4-41-06: OCI-reference-shaped grammar for the image allow-list. The repository group can never
# start with "-" (argument-injection guard) and never mixes case; the tag and digest groups are
# exact-length/charset. Any other non-comment, non-blank line in images.Dockerfile fails closed.
IMAGE_FROM_RE = re.compile(
    r"^FROM\s+"
    r"(?P<repo>[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*)"
    r":(?P<tag>[A-Za-z0-9_][A-Za-z0-9._-]{0,127})"
    r"@sha256:(?P<digest>[0-9a-f]{64})"
    r"\s+AS\s+(?P<alias>[A-Za-z0-9_-]+)\s*$"
)
# G4-41-06: sidecar output (image ids, "Loaded image: <ref>") is matched against this before it
# is ever used in a later argv or printed; anything else fails closed and is sanitised for print.
IMAGE_ID_RE = re.compile(r"^(sha256:)?[0-9a-f]{64}$")
LOADED_IMAGE_RE = re.compile(r"^Loaded image:\s*(\S+)\s*$", re.MULTILINE)
CONTROL_CHARS_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f\x1b]")


def sanitize(s: str) -> str:
    """Strips C0/C1 control characters (including ESC) before anything from the sidecar or the
    host daemon is printed or used in a comparison (G4-41-06: untrusted output, not a shell)."""
    return CONTROL_CHARS_RE.sub("", s).strip()
# N-01 (docs/security/reviews/36.md): allowing platform.claude.com for the required interactive
# connectivity check also exposes Claude Code's OAuth token endpoint, so a claude.ai
# subscription /login can complete inside the sandbox and leave this file on the home volume.
# Never read it -- only check whether it exists (`test -e`) -- and never log in inside the
# sandbox; the dedicated, spend-capped API key (G4-11) is the only credential this sandbox uses.
CREDENTIALS_FILE = "/home/vscode/.claude/.credentials.json"


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


def compose_args_both() -> list[str]:
    """Both compose files (issue #41): the container-engine overlay plus #36's base stack. Used
    by every stop path (`down`, `reset`, `attach-prep`) and by `up --with-docker`, never by plain
    `up` (which must load only compose.yaml -- G4-12(a))."""
    return ["compose", "-f", str(COMPOSE_FILE), "-f", str(COMPOSE_DOCKER_FILE), "-p", PROJECT]


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


def credentials_file_present(docker: str) -> bool:
    """True if CREDENTIALS_FILE exists inside `workspace` (N-01). Uses `test -e` only -- the
    file's presence is checked, its content is never read, matching the "agents never read
    secrets" rule even for the host launcher itself. False if the container is not up at all
    (nothing to refuse yet; `up`'s own precheck and the container not existing cover that
    case), so this never blocks a fresh `up`."""
    result = run([docker, *compose_args(), "exec", "-T", "workspace", "test", "-e", CREDENTIALS_FILE], check=False, capture=True)
    assert isinstance(result, subprocess.CompletedProcess)
    return result.returncode == 0


def refuse_if_logged_in(docker: str) -> bool:
    """Returns True (refuse) and prints the N-01 guidance if a claude.ai subscription login
    has left credentials on the home volume. Called before `claude` and `shell` start."""
    if not credentials_file_present(docker):
        return False
    print(
        f"sandbox.py: refusing to start: {CREDENTIALS_FILE} exists inside workspace. This "
        "sandbox uses only the dedicated, spend-capped API key (G4-11) -- never run "
        "'/login' or a claude.ai subscription sign-in inside it (N-01, "
        "docs/security/reviews/36.md). Run '& $env:DECISYA_PYTHON .devcontainer\\sandbox.py "
        "reset' to remove the home volume (and this file with it), then start again with the "
        "API key only.",
        file=sys.stderr,
    )
    return True


def warn_if_logged_in_after(docker: str) -> None:
    """Checked again after a `claude`/`shell` session ends: a login could have happened during
    an interactive session that started clean. Warns only -- the session already ran -- and
    points at the same fix."""
    if credentials_file_present(docker):
        print(
            f"sandbox.py: WARNING: {CREDENTIALS_FILE} now exists inside workspace. If a "
            "claude.ai subscription login just happened in that session, run "
            "'& $env:DECISYA_PYTHON .devcontainer\\sandbox.py reset' before the next session "
            "(N-01, docs/security/reviews/36.md).",
            file=sys.stderr,
        )


def engine_container_ids(docker: str) -> list[str]:
    """Container ids (any state) labelled as the `docker` service of this project, found by
    label rather than by compose file set, so this works regardless of which `-f` files are
    currently passed (G4-41-08)."""
    result = run(
        [docker, "ps", "-a",
         "--filter", f"label=com.docker.compose.project={PROJECT}",
         "--filter", f"label=com.docker.compose.service={ENGINE_SERVICE}",
         "-q"],
        check=False, capture=True,
    )
    assert isinstance(result, subprocess.CompletedProcess)
    return [line for line in result.stdout.splitlines() if line.strip()]


def workspace_networks(docker: str) -> set[str] | None:
    """The set of Docker network names `workspace` is attached to, or None if `workspace` is not
    up at all (nothing to assert)."""
    result = run([docker, *compose_args(), "ps", "-q", "workspace"], check=False, capture=True)
    assert isinstance(result, subprocess.CompletedProcess)
    cid = result.stdout.strip()
    if not cid:
        return None
    inspected = run([docker, "inspect", cid, "--format", "{{json .NetworkSettings.Networks}}"], check=False, capture=True)
    assert isinstance(inspected, subprocess.CompletedProcess)
    try:
        return set(json.loads(inspected.stdout).keys())
    except (json.JSONDecodeError, AttributeError):
        return None


def assert_engine_off(docker: str) -> int:
    """G4-41-08 post-condition, run after every plain `up`: no `docker` service container
    survives, and `workspace` is on exactly the #36 network. Returns 0 (clean) or 1 (prints why
    and leaves the stack as-is for inspection -- it does not try to fix it itself)."""
    problems: list[str] = []
    leftover = engine_container_ids(docker)
    if leftover:
        problems.append(f"a '{ENGINE_SERVICE}' engine container still exists: {', '.join(leftover)}")
    nets = workspace_networks(docker)
    expected = {f"{PROJECT}_sandbox"}
    if nets is not None and nets != expected:
        problems.append(f"workspace networks are {sorted(nets)}, expected {sorted(expected)}")
    if problems:
        print("sandbox.py: engine-off post-condition failed after 'up' (G4-41-08): " + "; ".join(problems), file=sys.stderr)
        return 1
    return 0


def engine_status_banner(docker: str) -> str:
    return "ON" if engine_container_ids(docker) else "OFF"


def remove_volumes_tolerant(docker: str, volumes: list[str]) -> int:
    """Removes only the named volumes that exist; never fails on one that does not (G4-41-04:
    'tolerates missing volumes', for a machine that never used --with-docker)."""
    result = run([docker, "volume", "ls", "-q"], check=False, capture=True)
    assert isinstance(result, subprocess.CompletedProcess)
    existing = set(result.stdout.split())
    to_remove = [v for v in volumes if v in existing]
    if not to_remove:
        return 0
    return run([docker, "volume", "rm", *to_remove], check=True)  # type: ignore[return-value]


def parse_engine_version(raw: str) -> tuple[int, ...]:
    parts: list[int] = []
    for chunk in raw.split("."):
        m = re.match(r"\d+", chunk)
        parts.append(int(m.group()) if m else 0)
    return tuple(parts)


def refuse_below_version_floor(docker: str) -> bool:
    """G4-41-10: prints the Docker Engine version and kernel version, refuses (returns True)
    below the floor recorded at G4. Never lowered; bumped with the image-digest cadence."""
    server = run([docker, "version", "--format", "{{.Server.Version}}"], check=False, capture=True)
    kernel = run([docker, "info", "--format", "{{.KernelVersion}}"], check=False, capture=True)
    assert isinstance(server, subprocess.CompletedProcess) and isinstance(kernel, subprocess.CompletedProcess)
    server_version = sanitize(server.stdout)
    kernel_version = sanitize(kernel.stdout)
    print(f"sandbox.py: Docker Engine {server_version}, kernel {kernel_version} "
          f"(floor: Engine {'.'.join(map(str, ENGINE_VERSION_FLOOR))})")
    if parse_engine_version(server_version) < ENGINE_VERSION_FLOOR:
        print(
            f"sandbox.py: refusing --with-docker: Docker Engine {server_version} is below the "
            f"recorded floor {'.'.join(map(str, ENGINE_VERSION_FLOOR))} (G4-41-10). Update Docker "
            "Desktop and run 'wsl --update' first (docs/runbooks/agent-sandbox.md).",
            file=sys.stderr,
        )
        return True
    return False


def parse_images_dockerfile(path: Path) -> list[tuple[str, str, str, str]] | None:
    """Strict, fail-closed parse of an image allow-list file (G4-41-06): every non-comment,
    non-blank line must be a fully pinned `FROM <repo>:<tag>@sha256:<digest> AS <alias>` line, in
    the OCI reference grammar. Returns None (refuse) on the first line that does not match."""
    entries: list[tuple[str, str, str, str]] = []
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as e:
        print(f"sandbox.py: cannot read {path}: {e}", file=sys.stderr)
        return None
    for i, raw_line in enumerate(lines, start=1):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        m = IMAGE_FROM_RE.match(line)
        if not m:
            print(f"sandbox.py: refusing: {path}:{i} is not a pinned 'FROM <repo>:<tag>@sha256:<digest> AS <alias>' line: {sanitize(line)[:120]}", file=sys.stderr)
            return None
        entries.append((m.group("repo"), m.group("tag"), m.group("digest"), m.group("alias")))
    return entries


def detect_docker_platform() -> str:
    machine = platform.machine().lower()
    if machine in ("amd64", "x86_64"):
        return "linux/amd64"
    if machine in ("arm64", "aarch64"):
        return "linux/arm64"
    print(f"sandbox.py: refusing: unrecognised host architecture '{machine}' for --platform on docker save", file=sys.stderr)
    sys.exit(1)


def preload_images(docker: str) -> int:
    """G4-41-04/05/06/07/13: pulls each allow-listed image on the HOST (full internet, the same
    trust path as the sandbox's own base images), then streams it into the sidecar with
    `docker save | podman load` and tags it under the exact name the allow-list names. Runs only
    `podman load`, `podman tag` and `podman image inspect`/`podman images` inside the sidecar, as
    the service user, never `-u`/`--privileged` (G4-41-05). Every failure is fatal."""
    entries = parse_images_dockerfile(IMAGES_DOCKERFILE)
    if entries is None:
        return 1
    if not entries:
        print(f"sandbox.py: {IMAGES_DOCKERFILE} has no image entries; nothing to pre-load")
        return 0
    docker_platform = detect_docker_platform()
    for repo, tag, digest, _alias in entries:
        ref = f"{repo}:{tag}@sha256:{digest}"
        target = f"{repo}:{tag}"
        print(f"sandbox.py: pre-loading {target} ({ref})")
        pull_rc = run([docker, "pull", ref], check=False)
        if pull_rc != 0:
            print(f"sandbox.py: refusing: 'docker pull {ref}' failed on the host", file=sys.stderr)
            return 1
        cwd = outside_cwd()
        save_proc = subprocess.Popen([docker, "save", "--platform", docker_platform, ref], cwd=cwd, stdout=subprocess.PIPE)
        assert save_proc.stdout is not None
        load_result = subprocess.run(
            [docker, *compose_args_both(), "exec", "-T", ENGINE_SERVICE, "podman", "load"],
            stdin=save_proc.stdout, capture_output=True, text=True, cwd=outside_cwd(), check=False,
        )
        save_proc.stdout.close()
        save_rc = save_proc.wait()
        if save_rc != 0 or load_result.returncode != 0:
            print(f"sandbox.py: refusing: loading {ref} into the sidecar failed:\n{sanitize(load_result.stdout)}\n{sanitize(load_result.stderr)}", file=sys.stderr)
            return 1
        loaded_match = LOADED_IMAGE_RE.search(load_result.stdout)
        if not loaded_match:
            print(f"sandbox.py: refusing: could not find 'Loaded image: <ref>' in podman load output:\n{sanitize(load_result.stdout)}", file=sys.stderr)
            return 1
        loaded_name = sanitize(loaded_match.group(1))
        # G4-41-06: the loaded reference is untrusted output; it is used only as a `podman tag`
        # source argument (after `--`), never interpolated into a shell, and only if it looks
        # like a plausible image reference (no whitespace, no leading '-').
        if not loaded_name or loaded_name.startswith("-") or any(c.isspace() for c in loaded_name):
            print(f"sandbox.py: refusing: sidecar reported an implausible loaded image name: {sanitize(loaded_name)!r}", file=sys.stderr)
            return 1
        tag_rc = run([docker, *compose_args_both(), "exec", "-T", ENGINE_SERVICE, "podman", "tag", "--", loaded_name, target], check=False)
        if tag_rc != 0:
            print(f"sandbox.py: refusing: 'podman tag {loaded_name} {target}' failed in the sidecar", file=sys.stderr)
            return 1
        print(f"sandbox.py: loaded {target}")
    return 0


def cmd_up(args: list[str]) -> int:
    if args not in ([], ["--with-docker"]):
        print("sandbox.py: up: unknown arguments (only --with-docker is accepted)", file=sys.stderr)
        return 2
    with_docker = args == ["--with-docker"]
    python = sys.executable
    precheck = Path(__file__).resolve().parent / "precheck.py"
    precheck_result = subprocess.run([python, str(precheck)], cwd=outside_cwd(), check=False)
    if precheck_result.returncode != 0:
        print("sandbox.py: precheck failed; the sandbox was not started", file=sys.stderr)
        return precheck_result.returncode
    docker = require("docker")
    if with_docker:
        return cmd_up_with_docker(docker)
    rc = run([docker, *compose_args(), "up", "-d", "--build", "--wait", "--remove-orphans"], check=True)
    assert isinstance(rc, int)
    if rc != 0:
        return rc
    return assert_engine_off(docker)


def cmd_up_with_docker(docker: str) -> int:
    """Issue #41: `up --with-docker`. Refuses while `claude` runs in workspace (G4-41-04) and
    below the version floor (G4-41-10). Then a FRESH engine every time: the `docker` container
    and both engine volumes are removed and recreated before the overlay is brought up, and every
    allow-listed image is pre-loaded before this returns -- no agent session ever touches a
    carried-over store (T-41-04, T-41-05)."""
    if process_running_in_workspace(docker, "claude"):
        print(
            "sandbox.py: refusing --with-docker: a 'claude' process is running inside workspace "
            "(G4-41-04). Stop the session first.",
            file=sys.stderr,
        )
        return 1
    if refuse_below_version_floor(docker):
        return 1
    # Fresh engine (G4-41-04): stop and remove the docker container AND workspace -- workspace
    # holds a read-only mount of decisya-sandbox-engine-run, so the volume cannot be removed
    # while it exists (G4 evidence: "volume is in use", Docker refuses). workspace's own
    # persistent state (home, vscode-server, nuget) lives on separate named volumes untouched by
    # this step, and `claude` was just confirmed not running, so recreating it here is safe. Then
    # remove and recreate both engine volumes, tolerating their absence (first-ever --with-docker
    # on this machine).
    run([docker, *compose_args_both(), "rm", "-f", "-s", ENGINE_SERVICE, "workspace"], check=False)
    rc = remove_volumes_tolerant(docker, ENGINE_VOLUMES)
    assert isinstance(rc, int)
    if rc != 0:
        return rc
    rc = run([docker, *compose_args_both(), "up", "-d", "--build", "--wait", "--remove-orphans"], check=True)
    assert isinstance(rc, int)
    if rc != 0:
        return rc
    rc = preload_images(docker)
    if rc != 0:
        return rc
    print(
        "sandbox.py: container engine: ON. A plain 'up', 'down', 'reset' or 'attach-prep' turns "
        "it off. While it is on, a kernel exploit run by the agent can reach all of C: (T-41-01, "
        "docs/security/threat-models/agent-sandbox-docker-sidecar.md) -- enable it only for "
        "integration runs."
    )
    return 0


def cmd_claude(args: list[str]) -> int:
    docker = require("docker")
    if process_running_in_workspace(docker, "vscode-server"):
        print("sandbox.py: refusing to start Claude Code: a VS Code server process is running "
              "inside workspace. Run 'attach-prep' first, or close the attach (G4-07).", file=sys.stderr)
        return 1
    if refuse_if_logged_in(docker):
        return 1
    key = os.environ.get(KEY_ENV_VAR)
    if not key:
        print(f"sandbox.py: {KEY_ENV_VAR} is not set in this host user's environment. See "
              "docs/runbooks/agent-sandbox.md for how to set it (never as ANTHROPIC_API_KEY, "
              "so host Claude sessions never pick it up).", file=sys.stderr)
        return 1
    print(f"sandbox.py: container engine: {engine_status_banner(docker)} (G4-41-08)")
    # The key lives only in this one child process's environment (docker compose exec -e),
    # never in compose.yaml, an env file, or `docker inspect workspace` (G4-11).
    child_env = {**os.environ, "ANTHROPIC_API_KEY": key}
    cwd = outside_cwd()
    result = subprocess.run(
        [docker, *compose_args(), "exec", "-e", "ANTHROPIC_API_KEY", "workspace", "claude", *args],
        cwd=cwd, env=child_env, check=False,
    )
    warn_if_logged_in_after(docker)
    return result.returncode


def cmd_shell(_: list[str]) -> int:
    docker = require("docker")
    if refuse_if_logged_in(docker):
        return 1
    print(f"sandbox.py: container engine: {engine_status_banner(docker)} (G4-41-08)")
    cwd = outside_cwd()
    result = subprocess.run([docker, *compose_args(), "exec", "workspace", "bash"], cwd=cwd, check=False)
    warn_if_logged_in_after(docker)
    return result.returncode


def cmd_down(_: list[str]) -> int:
    docker = require("docker")
    # Both compose files, always (G4-41-08): the engine and its network are removed regardless
    # of whether this session ever used --with-docker.
    return run([docker, *compose_args_both(), "down", "--remove-orphans"], check=True)  # type: ignore[return-value]


def cmd_reset(args: list[str]) -> int:
    rc = cmd_down(args)
    if rc != 0:
        return rc
    docker = require("docker")
    return remove_volumes_tolerant(docker, SANDBOX_VOLUMES + ENGINE_VOLUMES)  # type: ignore[return-value]


def cmd_attach_prep(_: list[str]) -> int:
    docker = require("docker")
    if process_running_in_workspace(docker, "claude"):
        print("sandbox.py: refusing: a 'claude' process is running inside workspace. Stop the "
              "session first (SB6 / G4-07 mutual exclusion).", file=sys.stderr)
        return 1
    # Both compose files, always (G4-41-08, T-41-11): an earlier version of this command used
    # only compose.yaml, which left an orphaned `docker` sidecar (and its nested containers)
    # running with the engine network intact.
    rc = run([docker, *compose_args_both(), "down", "--remove-orphans"], check=True)  # type: ignore[assignment]
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
