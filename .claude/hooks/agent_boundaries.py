#!/usr/bin/env python3
"""PreToolUse hook (Write|Edit|MultiEdit|NotebookEdit|Bash) for the project agents.

For a project agent listed in .claude/boundaries.json (payload field agent_type):
- Write/Edit/MultiEdit/NotebookEdit: the target must match one of the agent's globs and none of
  the shared deny globs (the write lanes).
- Bash: every simple command must match one of the agent's Bash(...) patterns from its tools:
  line (#114, T-02); then package-fetching and destructive commands are denied (issue #39 items
  4 and 5, Marco's decision 2026-09-25: these bind agents only; the main session keeps asking
  for permission).
- The first deny freezes the agent run (agent_id): every later Bash or write call of that run is
  denied, and the agent is told to stop and report (#114 D4, D5).
- Any error while deciding denies the call (fail closed, G4-39-01); inputs above the size caps
  are denied as too long to check (G4-39-05).
The main session and agents that are not listed keep the normal permission flow; errors there
fail open. Deny decisions are written to the local audit log (.agent-logs/hooks.jsonl).
"""
from __future__ import annotations

import re
import shlex
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


# npm is an allow-list too (#58 M1; threat model npm-install-guard.md). npm resolves any unique
# prefix of a command (`npm insta`), camelCase forms and about 40 aliases (`isnt`, `ic`, `x`), so a
# list of banned verbs cannot keep up. A listed agent may run only these verbs, with no flag before
# the verb (`npm --prefix x install`); `audit fix` installs and is denied. npx always fetches what
# it cannot find (non-TTY implies --yes), so it is denied outright (M2, decision A), and so are the
# other package managers (M3). Installs are Marco's.
_NPM_PROGRAMS = {"npm", "npm-cli.js"}
_NPX_PROGRAMS = {"npx", "npx-cli.js"}
_OTHER_PACKAGE_MANAGERS = {"pnpm", "pnpx", "yarn", "yarnpkg", "bun", "bunx", "corepack", "deno"}
# No -v/--version (G6-58-01): npm reads a following true/false as the flag's value and the next
# word as the command, so `npm -v false ci` would install.
_NPM_ALLOWED_VERBS = {"run", "run-script", "test", "ls", "outdated", "audit"}
_SCRIPT_SUFFIX = re.compile(r"\.(exe|cmd|ps1|bat)$", re.IGNORECASE)


def _package_program(token: str) -> str:
    """`npm.cmd`, `C:/nodejs/npm.ps1` and `NPM` all read as `npm`."""
    return _SCRIPT_SUFFIX.sub("", re.split(r"[/\\]", token)[-1]).lower()


def npm_violation(command: str) -> str | None:
    """The first package-manager call in the command that a listed agent may not run, or None.
    Same normalisation as docker_subcommands: quotes deleted, two backslash views, split on every
    shell separator, every token checked as a possible program."""
    unquoted = re.sub(r"['\"]", "", command)
    for text in (unquoted.replace("\\", ""), unquoted.replace("\\", "/")):
        for segment in re.split(r"[;&|\n()$`<>{}]+", text):
            tokens = segment.split()
            for i, token in enumerate(tokens):
                program = _package_program(token)
                if program in _NPX_PROGRAMS or program in _OTHER_PACKAGE_MANAGERS:
                    return program
                if program not in _NPM_PROGRAMS:
                    continue
                rest = [t.lower() for t in tokens[i + 1:]]
                verb = rest[0] if rest else ""
                if verb not in _NPM_ALLOWED_VERBS or (verb == "audit" and "fix" in rest[1:]):
                    return f"npm {verb}".strip()
    return None


def decide_npm(command: str, agent: str) -> tuple[str, str, None] | None:
    if npm_violation(command) is None:
        return None
    return ("agent.npm", f"{agent} may run only `npm run`, `npm test`, `npm ls`, `npm outdated` and "
            "`npm audit`; installs, npx and other package managers are Marco's. Report what you need; "
            "Marco runs it. For a text search, use the Grep tool, not a shell command that names npm.", None)


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
                    "Marco's). Report what you need; Marco runs it. For a text search, use the Grep tool, "
                    "not a shell command that names docker.", None)
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


# --- #114 D2 (with G3 G4-114-01 to 04): the per-agent Bash allow-list ---------------------------
# The hook reads the command with shlex and Git Bash runs it, so everything on which the two could
# disagree is denied before matching: substitution, variables, escapes, globs, braces, comments,
# redirections, subshells and background jobs. Every simple command must then match one of the
# agent's patterns, and its arguments may not point outside the repository or use an option that
# writes a chosen file, loads code or changes package sources.
_HARMLESS_REDIRECT = re.compile(r"(?<![^\s;&|])(?:\d?>/dev/null|\d?>&\d)(?=$|[\s;&|])")
_UNQUOTED_DENY = {"<": "output redirection", ">": "output redirection", "(": "subshell or background job",
                  ")": "subshell or background job", "{": "shell expansion", "}": "shell expansion",
                  "*": "shell expansion", "?": "shell expansion", "[": "shell expansion", "]": "shell expansion",
                  "#": "comment"}
_ASSIGNMENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*=")
_DRIVE = re.compile(r"(?:^|[=;,])[A-Za-z]:")
# Option names: dashes stripped, lower case, value cut at the first '=' or ':'.
# G6-114-03/05: dotnet options are an allow-list. MSBuild and the .NET CLI keep adding options that
# write a chosen file (-getResultOutputFile, --artifacts-path, --packages), so a deny-list falls behind.
_DOTNET_ALLOWED = {
    "warnaserror", "c", "configuration", "v", "verbosity", "nologo", "no-restore", "no-incremental", "no-build",
    "no-dependencies", "f", "framework", "r", "runtime", "a", "arch", "os", "m", "maxcpucount", "interactive",
    "project", "filter-trait", "filter-not-trait", "filter-class", "filter-not-class", "filter-method",
    "filter-not-method", "filter-namespace", "filter-not-namespace", "treenode-filter", "list-tests",
    "minimum-expected-tests", "ignore-exit-code", "verify-no-changes", "include", "exclude", "severity",
    "locked-mode", "force-evaluate", "vulnerable", "include-transitive", "outdated", "deprecated", "format",
    "highest-minor", "highest-patch", "version", "info", "list-sdks", "list-runtimes", "help", "h",
}
_EF_ALLOWED = {"project", "p", "startup-project", "s", "context", "c", "idempotent", "no-build", "json",
               "configuration", "framework", "runtime", "r", "verbose", "v", "no-color", "prefix-output",
               "namespace", "from", "to", "help", "h"}  # never -f/--force or --connection (G4-114-04)
_EF_OUTPUT = ("o", "output", "output-dir")
# G6-114-01: cd may only enter the SPA folder (frontend-dev's and the reviewers' `npm` commands).
_CD_TARGETS = ("src/Decisya.Web",)
_OTHER_DENIED = {
    "gitleaks": ("log-opts", "r", "report-path", "diagnostics"),
    "actionlint": ("shellcheck", "pyflakes"),
    "pre-commit": ("c", "config"),
}
_GIT_DENIED = ("ou", "no-i", "upl")  # --output, --no-index, --upload-pack, any abbreviation


def _quote_states(text: str) -> tuple[list[str], str]:
    """Per character 'c' (unquoted), 's' or 'd'; and the mode at the end (unbalanced if not 'c')."""
    states, mode = [], "c"
    for ch in text:
        if mode == "c":
            mode = {"'": "s", '"': "d"}.get(ch, "c")
            states.append(mode)
        else:
            states.append(mode)
            if ch == ("'" if mode == "s" else '"'):
                mode = "c"
    return states, mode


def _words(segment: str) -> list[str]:
    lexer = shlex.shlex(segment, posix=True)
    lexer.whitespace_split, lexer.commenters = True, ""
    return list(lexer)


def _option_name(token: str) -> str:
    return re.split(r"[=:]", token.lstrip("-"), maxsplit=1)[0].lower()


def _option_value(tokens: list[str], i: int) -> str | None:
    m = re.match(r"-+[^=:]+[=:](.*)", tokens[i])
    if m:
        return m.group(1)
    return tokens[i + 1] if i + 1 < len(tokens) else None


def _outside(token: str) -> bool:
    """G4-114-02: a path outside the repository anywhere in the token, not only at its start."""
    if token.startswith(("@", "/")) or _DRIVE.search(token):
        return True
    body = token[2:] if token.startswith(":/") else token  # git's ":/" means the repository top
    return any(piece.strip().startswith(("/", "\\", "~")) or ".." in re.split(r"[/\\]", piece)
               for piece in re.split(r"[=;,:]", body))


def _in_lane(path: Path, payload: dict, config: dict | None, agent: str) -> bool:
    if config is None:
        raise lib.ConfigError("boundaries.json unreadable; an output path cannot be checked")
    return decide_write({"tool_input": {"file_path": str(path)}, "cwd": str(lib.ROOT)}, config, agent) is None


def _option_problem(tokens: list[str], cwd: Path, payload: dict, config: dict | None, agent: str) -> str | None:
    program, args = tokens[0], tokens[1:]
    if program == "dotnet":
        if "--" in args:
            return "option --"
        ef = args[:1] == ["ef"]
        for i, token in enumerate(tokens[1:], start=1):
            if not token.startswith("-"):
                continue
            name = _option_name(token)
            if ef:
                if name in _EF_OUTPUT:  # the ef-migration skill's --output-dir / -o: allowed inside the lane
                    value = _option_value(tokens, i)
                    project = next((_option_value(tokens, j) for j, t in enumerate(tokens)
                                    if _option_name(t) in ("p", "project") and t.startswith("-")), None)
                    base = cwd / project if (args[1:3] == ["migrations", "add"] and project) else cwd
                    target = base / value / "Migration.cs" if args[1:3] == ["migrations", "add"] else cwd / (value or "")
                    if not value or not _in_lane(target, payload, config, agent):
                        return "output outside your write lane"
                elif name not in _EF_ALLOWED:
                    return f"option {name}"
                continue
            if name not in _DOTNET_ALLOWED:
                return f"option {name}"
        return None
    if program == "git":
        if any(t.startswith("--") and _option_name(t).startswith(_GIT_DENIED) for t in args):
            return "git option"
        if args[:1] == ["fetch"] and any(":" in t or t.startswith("+") for t in args[1:]):
            return "fetch refspec"  # G4-114-04: no write to local refs
        return None
    if program == "aspire" and args[:1] == ["publish"]:
        denied = ("o", "output-path")
    else:
        denied = _OTHER_DENIED.get(program, ())
    for token in args:
        if token.startswith("-") and any(_option_name(token) == d or (len(d) > 1 and _option_name(token).startswith(d))
                                         for d in denied):
            return f"option {_option_name(token)}"
    return None


def _clean(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9._ -]", "", text)[:40] or "command"


def allowlist_problem(command: str, agent: str, patterns: list[str], payload: dict,
                      config: dict | None) -> tuple[str, str] | None:
    """(rule, class) when a listed agent may not run this command, else None (#114 D2)."""
    text = command.replace("\r\n", "\n")
    if re.search(r"[\x00-\x08\x0b-\x1f\x7f]", text):  # S-114-05: a lone CR too
        return "allowlist.control", "a control character"
    if "`" in text or "$" in text:
        return "allowlist.substitution", "command substitution"
    states, mode = _quote_states(text)
    if mode != "c":
        return "allowlist.quotes", "unbalanced quotes"
    if any(ch == "\\" and st != "s" for ch, st in zip(text, states)):
        return "allowlist.escape", "an escape character"
    chars = list(text)
    for m in _HARMLESS_REDIRECT.finditer(text):  # G4-114-01: only a whole word is removed
        if all(st == "c" for st in states[m.start():m.end()]):
            chars[m.start():m.end()] = " " * (m.end() - m.start())
    text = "".join(chars)
    segments, start, i = [], 0, 0
    while i < len(text):
        ch = text[i]
        if states[i] == "c":
            if ch in _UNQUOTED_DENY:
                cls = _UNQUOTED_DENY[ch]
                return f"allowlist.{cls.split()[0]}", cls
            pair = text[i:i + 2]
            if pair in ("&&", "||"):
                segments.append((text[start:i], pair))
                i += 2
                start = i
                continue
            if ch == "&":
                return "allowlist.background", "subshell or background job"
            if ch in ";|\n":
                segments.append((text[start:i], ch))
                start = i + 1
        i += 1
    segments.append((text[start:], ""))
    root = lib.ROOT.resolve()
    allowed_dirs = {root, *((root / d).resolve() for d in _CD_TARGETS)}
    cwd = Path(payload.get("cwd") or lib.ROOT).resolve()
    if cwd not in allowed_dirs:  # G6-114-01: never a folder the agent may have written project files into
        return "allowlist.cwd", "a working directory other than the repository root or the SPA folder"
    for n, (segment, separator) in enumerate(segments):
        tokens = _words(segment)
        if not tokens:
            continue
        if _ASSIGNMENT.match(tokens[0]):
            return "allowlist.assignment", "environment assignment"
        if tokens[0] == "cd":
            if (n != 0 or separator != "&&" or cwd != root or len(tokens) != 2
                    or tokens[1].rstrip("/") not in _CD_TARGETS):
                return "allowlist.cd", "cd other than into src/Decisya.Web"
            cwd = (root / tokens[1]).resolve()
            continue
        if not any(lib.pattern_allows(p, tokens) for p in patterns):
            return "allowlist.program", _clean(tokens[0])
        if any(_outside(t) for t in tokens[1:]):
            return "allowlist.path", "argument outside the repository"
        problem = _option_problem(tokens, cwd, payload, config, agent)
        if problem:
            return "allowlist.option", _clean(problem)
    return None


def decide_bash(payload: dict, agent: str, config: dict | None = None) -> tuple[str, str, None] | None:
    command = (payload.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        raise ValueError("missing command")
    if len(command) > lib.MAX_COMMAND:
        return "input.too-long", f"{agent}: command too long to check.", None
    problem = allowlist_problem(command, agent, lib.bash_patterns(agent), payload, config)
    if problem:
        rule, cls = problem
        return (rule, f"{agent}: {cls} is outside your declared Bash patterns (tools: line in "
                f".claude/agents/{agent}.md). For reading or searching files, use the Read, Glob or Grep tool.", None)
    docker = decide_docker(command, agent, lib.docker_patterns(config, agent) if config is not None else None)
    if docker:
        return docker
    npm = decide_npm(command, agent)
    if npm:
        return npm
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
        state = lib.freeze_state(payload)  # #114 D4: a frozen run gets nothing more, whatever the call
        if state:
            reasons = {"freeze.no-agent-id": f"{agent}: the hook payload has no valid agent_id, so this call "
                                             "cannot be tied to an agent run.",
                       "freeze.unreadable": f"{agent}: the freeze state of this agent run cannot be read.",
                       "freeze.frozen": lib.frozen_reason(agent)}
            lib.deny_listed(HOOK, payload, state, reasons[state])
            return 0
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
        lib.deny_listed(HOOK, payload, f"error.{type(exc).__name__}", lib.fail_closed_reason(HOOK, agent, exc),
                        decision="deny-error")
        return 0
    if result:
        rule, reason, rel = result
        lib.deny_listed(HOOK, payload, rule, reason, rel)
    return 0


if __name__ == "__main__":
    sys.exit(main())
