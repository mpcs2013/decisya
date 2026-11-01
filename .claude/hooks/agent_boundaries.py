#!/usr/bin/env python3
"""PreToolUse hook (Write|Edit|MultiEdit|NotebookEdit|Bash) for the project agents.


For a project agent listed in .claude/boundaries.json (payload field agent_type):
- Write/Edit/MultiEdit/NotebookEdit: the target must match one of the agent's globs and none of
  the shared deny globs (the write lanes).
- Bash: every simple command must match one of the agent's Bash(...) patterns from its tools:
  line, the `agent_tools` grants of the installed packs, or the `tools` grants in boundaries.json; then
  package-fetching and destructive commands are denied (these bind agents only; the main session keeps asking
  for permission).
- The first deny freezes the agent run (agent_id): every later Bash or write call of that run is
  denied, and the agent is told to stop and report.
- Any error while deciding denies the call (fail closed); inputs above the size caps
  are denied as too long to check.
The main session and agents that are not listed keep the normal permission flow; errors there
fail open. Deny decisions are written to the local audit log (.agent-logs/hooks.jsonl).
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import re
import shlex
import sys
from pathlib import Path

try:
    # _hooklib from its source file, never from bytecode in __pycache__ (a planted .pyc would run in the hook)
    import importlib.util
    _LIB = Path(__file__).resolve().parent / "_hooklib.py"
    lib = importlib.util.module_from_spec(importlib.util.spec_from_file_location("_hooklib", _LIB))
    sys.modules["_hooklib"] = lib
    exec(compile(_LIB.read_bytes(), str(_LIB), "exec"), lib.__dict__)  # noqa: S102
except Exception as _exc:  # noqa: BLE001 - the listed set is unknown, so deny any subagent
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

# Bash commands no project agent may run. Matched per simple command, so the
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

# Docker is an allow-list, not a deny-list.
# Any `docker` / `docker-compose` invocation by a listed agent is denied unless its subcommand
# matches that agent's list, which is data: the `docker` key of .claude/boundaries.json,
# validated by _hooklib (no inspect/exec/cp/compose config: they print container environments,
# which hold generated secrets). There is no in-code fallback list: an agent
# without an entry gets no Docker command, and an unreadable config denies Docker.
_DOCKER_PROGRAMS = {"docker", "docker-compose"}
_DOCKER_GLOBAL_WITH_VALUE = {"-h", "--host", "--context", "-c", "--config", "-l", "--log-level"}
_DOCKER_GLOBAL_FLAGS = {"-d", "--debug"}
_DOCKER_SOCKET = re.compile(r"docker\.sock|docker_engine", re.IGNORECASE)


def _program(token: str) -> str:
    """`/usr/bin/docker`, `C:/x/docker.exe` and `DOCKER` all read as `docker`."""
    return re.sub(r"\.exe$", "", re.split(r"[/\\]", token)[-1], flags=re.IGNORECASE).lower()


def docker_subcommands(command: str) -> list[str]:
    """The Docker subcommand of EVERY docker invocation in the command, normalised:
    quotes and backslashes are deleted (so `d''ocker` and `do\\cker` read as `docker`); the command
    is split on every shell separator, including `;&|`, newlines, `()`, `$`, backticks, `<>` and
    `{}`, so a docker call inside a subshell, command substitution or redirection is its own
    segment; every `docker`/`docker-compose` token (path-qualified or `.exe`) is checked, not only
    the first. `.exe`, docker's global flags and `docker-compose` (read as `compose`) are
    normalised. A bare `docker` with nothing after it yields an empty subcommand, which no
    allow-list matches. Two views are checked: backslashes deleted (`do\\cker`) and
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


# npm is an allow-list too. npm resolves any unique
# prefix of a command (`npm insta`), camelCase forms and about 40 aliases (`isnt`, `ic`, `x`), so a
# list of banned verbs cannot keep up. A listed agent may run only these verbs, with no flag before
# the verb (`npm --prefix x install`); `audit fix` installs and is denied. npx always fetches what
# it cannot find (non-TTY implies --yes), so it is denied outright (M2, decision A), and so are the
# other package managers (M3). Installs are the owner's.
_NPM_PROGRAMS = {"npm", "npm-cli.js"}
_NPX_PROGRAMS = {"npx", "npx-cli.js"}
_OTHER_PACKAGE_MANAGERS = {"pnpm", "pnpx", "yarn", "yarnpkg", "bun", "bunx", "corepack", "deno"}
# The npm verbs a listed agent may use come from the installed packs (`allowed_verbs`), selected from the reviewed
# list in the project loader; without a pack that selects them, every npm command is denied.
_SCRIPT_SUFFIX = re.compile(r"\.(exe|cmd|ps1|bat)$", re.IGNORECASE)


def _package_program(token: str) -> str:
    """`npm.cmd`, `C:/nodejs/npm.ps1` and `NPM` all read as `npm`."""
    return _SCRIPT_SUFFIX.sub("", re.split(r"[/\\]", token)[-1]).lower()


def npm_violation(command: str, allowed_verbs: frozenset[str] | set[str] = frozenset()) -> str | None:
    """The first package-manager call in the command that a listed agent may not run, or None.
    Same normalisation as docker_subcommands: quotes deleted, two backslash views, split on every
    shell separator, every token checked as a possible program. `allowed_verbs` are the npm verbs the installed
    packs allow; empty denies every npm command."""
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
                if verb not in allowed_verbs or (verb == "audit" and "fix" in rest[1:]):
                    return f"npm {verb}".strip()
    return None


def decide_npm(command: str, agent: str, allowed_verbs: frozenset[str] | set[str] = frozenset()) -> tuple[str, str, None] | None:
    if npm_violation(command, allowed_verbs) is None:
        return None
    allowed = ", ".join(f"`npm {v}`" for v in sorted(allowed_verbs)) or "no npm command (no installed pack allows one)"
    return ("agent.npm", f"{agent} may run only {allowed}; installs, npx and other package managers are the "
            "owner's. Report what you need; the owner runs it. For a text search, use the Grep tool, not a shell "
            "command that names npm.", None)


def decide_docker(command: str, agent: str, allow: list[re.Pattern[str]] | None = None) -> tuple[str, str, None] | None:
    """`allow` is the agent's validated allow-list; when None it is loaded here, and any load or
    validation error propagates (main() denies it as deny-error) whenever the command runs Docker."""
    if _DOCKER_SOCKET.search(command):
        return ("agent.docker-socket", f"{agent} may not reach the Docker API directly. Report what you need; "
                "the owner runs it.", None)
    subcommands = docker_subcommands(command)
    if not subcommands:
        return None
    if allow is None:
        allow = lib.docker_patterns(lib.load_boundaries(), agent)
    for sub in subcommands:
        if not lib.docker_allows(allow, sub):
            return ("agent.docker", f"{agent} may not run this Docker command (containers and volumes are "
                    "the owner's). Report what you need; the owner runs it. For a text search, use the Grep tool, "
                    "not a shell command that names docker.", None)
    return None


def policy_views(command: str) -> tuple[str, str]:
    """The command as written, and a copy without quotes/backslashes, `.exe` suffixes and gh's
    global repo flags, so `dotnet "package" add`, `dotnet.exe` and `gh -R x issue close` match
    too."""
    text = re.sub(r"['\"\\]", "", command)
    text = re.sub(r"\b(dotnet|gh|aspire|pre-commit)\.exe\b", r"\1", text, flags=re.IGNORECASE)
    text = re.sub(r"\bgh(?:\s+(?:-R|--repo|--hostname)(?:=|\s+)\S+)+", "gh", text, flags=re.IGNORECASE)
    return command, text


def glob_to_regex(pattern: str, ignore_case: bool = False) -> re.Pattern[str]:
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return re.compile(f"^{out}$", re.IGNORECASE if ignore_case else 0)


AUDIT_DIR = ".agent-logs"  # lib.LOG_DIR, relative to the repository root


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
    parts = rel.split("/")
    # Windows writes `x.props::$DATA` and `x.props.` to `x.props`, so these forms would pass a glob meant for it
    if ":" in rel or any(p.endswith((".", " ")) for p in parts):
        return ("boundary.path-form", f"{agent}: the path {rel} uses a form Windows rewrites (a ':' stream "
                "or a name ending in a dot or space); write the plain path.", "<path-form>")
    # floors, whatever boundaries.json says: the audit log and the freeze markers that stop an agent run, and git's
    # own folders, whose config and hooks git runs (a lane such as tests/** would otherwise reach tests/.git/config)
    if parts[0].lower() == AUDIT_DIR:
        return "boundary.audit-log", f"{rel} holds the hook audit log and freeze markers; no agent writes there.", rel
    if any(p.lower() == ".git" for p in parts):
        return "boundary.git-dir", f"{agent}: no agent writes into a .git folder or file ({rel}).", rel
    # deny globs match without case (Windows, MSBuild and NuGet find NuGet.Config as nuget.config); lanes keep their
    # case, which errs towards denying
    if any(glob_to_regex(g, ignore_case=True).match(rel) for g in config.get("deny", [])):
        return "boundary.shared-deny", f"{rel} is written only by the main session (orchestrator), not by {agent}.", rel
    if any(glob_to_regex(g).match(rel) for g in allowed):
        return None
    lanes = ", ".join(allowed) if allowed else "nothing"
    return ("boundary.not-in-lane",
            f"{agent} may write only {lanes} (see .claude/boundaries.json); {rel} is outside that. Report the needed change instead.",
            rel)


# --- The per-agent Bash allow-list ---------------------------
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
# dotnet options are an allow-list from the installed packs (`allowed_options`), selected from the reviewed list in
# the project loader; without a pack that selects them, no dotnet option is allowed.
_EF_ALLOWED = {"project", "p", "startup-project", "s", "context", "c", "idempotent", "no-build", "json",
               "configuration", "framework", "runtime", "r", "verbose", "v", "no-color", "prefix-output",
               "namespace", "from", "to", "help", "h"}  # never -f/--force or --connection
_EF_OUTPUT = ("o", "output", "output-dir")
# an agent may only `cd` into a listed target, as the first command of an `&&` chain; the
# targets are `agent_boundaries.cd_targets` in .claude/project.json.
_OTHER_DENIED = {
    "gitleaks": ("log-opts", "r", "report-path", "diagnostics"),
    "actionlint": ("shellcheck", "pyflakes"),
    "pre-commit": ("c", "config"),
}
_GIT_DENIED = ("ou", "no-i", "upl", "open-files-in-pager")  # --output, --no-index, --upload-pack, grep -O


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
    """A path outside the repository anywhere in the token, not only at its start."""
    if token.startswith(("@", "/")) or _DRIVE.search(token):
        return True
    body = token[2:] if token.startswith(":/") else token  # git's ":/" means the repository top
    return any(piece.strip().startswith(("/", "\\", "~")) or ".." in re.split(r"[/\\]", piece)
               for piece in re.split(r"[=;,:]", body))


def _in_lane(path: Path, payload: dict, config: dict | None, agent: str) -> bool:
    if config is None:
        raise lib.ConfigError("boundaries.json unreadable; an output path cannot be checked")
    return decide_write({"tool_input": {"file_path": str(path)}, "cwd": str(lib.ROOT)}, config, agent) is None


def _option_problem(tokens: list[str], cwd: Path, payload: dict, config: dict | None, agent: str,
                    project: dict) -> str | None:
    program, args = tokens[0], tokens[1:]
    if program == "uv":
        # only a reviewed `uv run` prefix or a reviewed exact command; the tool's options come from the installed
        # packs' selection of REVIEWED_OPTIONS[tool], so nothing that loads code, writes a chosen file or fetches
        cfg = lib._cfg()
        if " ".join(tokens) in cfg.EXACT_GRANTS:
            return None
        run = cfg.uv_run_tool(tokens)
        if run is None:
            return "uv form"
        tool, rest = run
        if tool in cfg.UV_RUN_ROOT_ONLY and cwd != lib.ROOT.resolve():
            return f"{tool} after cd"
        allowed = project["pack_allowed_options"].get(tool, ())
        # The tools read a single-dash word as a cluster of short options (pytest's -co=x.ini is -c o=x.ini) and an
        # attached value as that option's value, so only `--name`, `--name=value` or exactly `-X` pass, compared with
        # case, and a bare word only where the tool reads it as a path to check (never for pip-audit, which would
        # install the project it names).
        for token in rest:
            if token == "--":
                return "option --"
            if token.startswith("--"):
                name = token[2:].split("=", 1)[0]
                if len(name) < 2 or name not in allowed:
                    return f"option {name}"
            elif token.startswith("-"):
                if len(token) != 2 or token[1] not in allowed:
                    return "option cluster or short option"
            elif tool in cfg.UV_RUN_NO_ARGUMENTS:
                return f"{tool} argument"
        return None
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
            if name not in project["pack_allowed_options"].get("dotnet", ()):
                return f"option {name}"
        return None
    if program == "git":
        if any(t.startswith("--") and _option_name(t).startswith(_GIT_DENIED) for t in args):
            return "git option"
        if args[:1] == ["fetch"] and any(":" in t or t.startswith("+") for t in args[1:]):
            return "fetch refspec"  # no write to local refs
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
                      config: dict | None, cd_targets: tuple[str, ...] | None = None,
                      project: dict | None = None) -> tuple[str, str] | None:
    """(rule, class) when a listed agent may not run this command, else None. `cd_targets` or `project`
    None loads .claude/project.json now (never a default)."""
    if project is None:
        project = lib.load_project()
    if cd_targets is None:
        cd_targets = tuple(project["agent_boundaries"]["cd_targets"])
    text = command.replace("\r\n", "\n")
    if re.search(r"[\x00-\x08\x0b-\x1f\x7f]", text):  # a lone CR too
        return "allowlist.control", "a control character"
    if "`" in text or "$" in text:
        return "allowlist.substitution", "command substitution"
    states, mode = _quote_states(text)
    if mode != "c":
        return "allowlist.quotes", "unbalanced quotes"
    if any(ch == "\\" and st != "s" for ch, st in zip(text, states)):
        return "allowlist.escape", "an escape character"
    chars = list(text)
    for m in _HARMLESS_REDIRECT.finditer(text):  # only a whole word is removed
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
    allowed_dirs = {root, *((root / d).resolve() for d in cd_targets)}
    cwd = Path(payload.get("cwd") or lib.ROOT).resolve()
    if cwd not in allowed_dirs:  # never a folder the agent may have written project files into
        return "allowlist.cwd", "a working directory other than the repository root"
    for n, (segment, separator) in enumerate(segments):
        tokens = _words(segment)
        if not tokens:
            continue
        if _ASSIGNMENT.match(tokens[0]):
            return "allowlist.assignment", "environment assignment"
        if tokens[0] == "cd":
            if (n != 0 or separator != "&&" or cwd != root or len(tokens) != 2
                    or tokens[1].rstrip("/") not in cd_targets):
                return "allowlist.cd", "cd (agents run every command from the repository root)"
            cwd = (root / tokens[1]).resolve()
            continue
        if not any(lib.pattern_allows(p, tokens) for p in patterns):
            return "allowlist.program", _clean(tokens[0])
        if any(_outside(t) for t in tokens[1:]):
            return "allowlist.path", "argument outside the repository"
        problem = _option_problem(tokens, cwd, payload, config, agent, project)
        if problem:
            return "allowlist.option", _clean(problem)
    return None


# a pattern that runs tests (the packs' test grants, or a project's own): only then does the deny message say how
_TEST_PATTERN = re.compile(r"^(?:dotnet|npm) test\b|^npm run(?:-script)? test\b|(?:^|\s)pytest(?:\s|$)")
# the agent's own tool for a denied shell form, named in the deny message so the next agent does not try another form
# POSIX names match in lower case only (so `git log HEAD` is no `head`); Windows names in any case
_WORD = r"(?:^|[\s;&|(`])"
_END = r"(?:\.exe)?(?=[\s;&|)`]|$)"
_INSTEAD = (
    (re.compile(_WORD + r"(?:grep|egrep|fgrep|rg|(?i:findstr|select-string|sls))" + _END), "search with the Grep tool"),
    (re.compile(_WORD + r"(?:find|ls|tree|(?i:dir|get-childitem|gci))" + _END), "list files with the Glob tool"),
    (re.compile(_WORD + r"(?:cat|head|tail|more|less|(?i:type|get-content))" + _END), "read a file with the Read tool"),
    # a redirect that writes a file: never 2>&1, ->, => or >>&, nor one to /dev/null
    (re.compile(_WORD + r"(?:sed|awk|perl|tee|(?i:set-content|add-content|out-file))" + _END
                + r"|<<|(?<![-=>&])>{1,2}(?![&>])(?!\s*/dev/null\b)"),
     "change a file with the Edit or Write tool, if your tools line lists them"),
)


def instead(command: str) -> str:
    """The tools to use instead of the forms in a denied command, as a sentence, or "" when none is recognised."""
    tools = [tool for pattern, tool in _INSTEAD if pattern.search(command)]
    return f" For this command: {'; '.join(tools)}." if tools else ""


def decide_bash(payload: dict, agent: str, config: dict | None = None,
                cd_targets: tuple[str, ...] | None = None, project: dict | None = None) -> tuple[str, str, None] | None:
    """`project` None loads .claude/project.json now (never a default)."""
    command = (payload.get("tool_input") or {}).get("command")
    if not isinstance(command, str):
        raise ValueError("missing command")
    if len(command) > lib.MAX_COMMAND:
        return "input.too-long", f"{agent}: command too long to check.", None
    if project is None:
        project = lib.load_project()
    patterns = lib.bash_patterns(agent) + lib.granted_patterns(agent, config, project)
    problem = allowlist_problem(command, agent, patterns, payload, config, cd_targets, project)
    if problem:
        rule, cls = problem
        # name the allowed way, for the report: the run is frozen, and the next agent should not try another form
        if not patterns:
            way = ("The allowed way, for your report: you have no Bash patterns, so make no shell call of any kind, "
                   "not even a placeholder or echo; read and search with the Read, Glob and Grep tools.")
        else:
            way = ("The allowed way, for your report: read and search files with the Read, Glob and Grep tools, and "
                   "change them with the Edit and Write tools if your tools line lists them, never through the shell.")
            if any(_TEST_PATTERN.search(p) for p in patterns):
                way += (" Run tests as one command from your patterns, as written, in the foreground and for your own "
                        "test classes where the pattern takes a filter: no pipe, redirect, background run, sleep or "
                        "chained command.")
        way += instead(command)
        return (rule, f"{agent}: {cls} is outside your declared Bash patterns (tools: line in "
                f".claude/agents/{agent}.md, plus the grants of the installed packs and of .claude/boundaries.json). "
                + way, None)
    docker = decide_docker(command, agent, lib.docker_patterns(config, agent) if config is not None else None)
    if docker:
        return docker
    npm = decide_npm(command, agent, set(project["pack_allowed_verbs"].get("npm", ())))
    if npm:
        return npm
    views = policy_views(command)
    for rule, pattern in _DENIED:
        if any(pattern.search(view) for view in views):
            return (rule, f"{agent} may not run this command (package fetching or a destructive "
                          "GitHub/.NET operation). Report what you need; the owner runs it.", None)
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
        state = lib.freeze_state(payload)  # a frozen run gets nothing more, whatever the call
        if state:
            reasons = {"freeze.no-agent-id": f"{agent}: the hook payload has no valid agent_id, so this call "
                                             "cannot be tied to an agent run.",
                       "freeze.unreadable": f"{agent}: the freeze state of this agent run cannot be read.",
                       "freeze.frozen": lib.frozen_reason(agent)}
            lib.deny_listed(HOOK, payload, state, reasons[state])
            return 0
        # without the project facts no call of a listed agent is decided (Write included).
        project = lib.load_project()
        cd_targets = tuple(project["agent_boundaries"]["cd_targets"])
        if payload.get("tool_name") in ("PowerShell", "Monitor"):  # one shell, one parser: Bash only
            result = ("allowlist.other-shell", f"{agent}: a listed agent runs commands through Bash only, never "
                      f"{payload.get('tool_name')}; use the Bash form of the command.", None)
        elif payload.get("tool_name") == "Bash":
            result = decide_bash(payload, agent, config, cd_targets, project)
        else:
            if config is None:
                raise lib.ConfigError("boundaries.json unreadable or invalid")
            if agent not in config["agents"]:
                raise lib.ConfigError("agent has no boundaries entry")
            result = decide_write(payload, config, agent)
    except lib.ProjectFactsError as exc:
        print(f"{HOOK}: .claude/project.json unusable ({exc.kind}) for {agent}; denying", file=sys.stderr)
        lib.deny_listed(HOOK, payload, f"config.project-{exc.kind}", lib.project_reason(HOOK, agent, exc),
                        decision="deny-error")
        return 0
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
