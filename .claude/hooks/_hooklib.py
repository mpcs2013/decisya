"""Shared helpers for the PreToolUse hooks.


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

a listed agent's Bash patterns come from its tools: line (bash_patterns), and every deny for
a listed agent first writes .agent-logs/frozen/<agent_id>, which denies the rest of that run.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import stat
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
# fixed at import from this file, never from a ROOT a test patches later or from
# CLAUDE_PROJECT_DIR; the loader is imported by absolute path, never by a sys.path name.
PROJECT_FILE = ROOT / ".claude" / "project.json"
_CLAUDECFG_FILE = Path(__file__).resolve().parents[1] / "scripts" / "_claudecfg.py"
LOG_DIR = ROOT / ".agent-logs"
LOG_FILE = LOG_DIR / "hooks.jsonl"
LOG_ROTATE_BYTES = 1024 * 1024
# The longest Bash command of a listed agent the hooks check; a longer one is denied as too long. shlex reads a command
# one character at a time, so the check takes time in proportion to its length: 64 KiB keeps the worst case near a
# tenth of the hook timeout on a slow runner and still holds a full GitHub comment body (65,536 characters).
MAX_COMMAND = 64 * 1024
MAX_PATH = 4 * 1024
_TOP_KEYS = {"$comment", "deny", "agents", "docker", "routing", "tools"}

# the per-agent Docker allow-list is data in boundaries.json.
# A pattern is re.match'ed against the normalised subcommand (agent_boundaries.docker_subcommands).
# Its positive part may only name literal subcommands, which are expanded (at most
# MAX_EXPANSIONS) and must each be in DOCKER_SUBCOMMANDS, an allow-list of reviewed
# subcommands.
# The probes test the exclusions a lookahead adds.
NAME = re.compile(r"[a-z][a-z0-9-]{0,39}")
LABEL = re.compile(r"[A-Za-z0-9 ,.()*+_-]{1,40}")                               # (no : or /)
ROUTE_PATHS = re.compile(r"[A-Za-z0-9 ,.()/*+:_`-]{1,300}")
ROUTE_FREE_TEXT_WORDS = 4
MAX_PATTERN = 200
MAX_EXPANSIONS = 32
# C0/C1 controls, zero-width and bidi characters, BOM.
_BAD_CHARS = re.compile("[\x00-\x1f\x7f-\x9f\u200b-\u200f\u202a-\u202e\u2066-\u2069\ufeff]")
DOCKER_GROUP_WORDS = {"container", "image", "volume", "network", "compose", "system", "context",
                      "plugin", "builder", "buildx"}
FORBIDDEN_DOCKER_VERBS = set(
    "inspect exec cp run rm rmi stop kill restart start create update attach commit export import save "
    "load build push pull login logout top diff prune secret config convert context plugin swarm service "
    "stack node trust manifest checkpoint wait pause unpause rename".split())
# Every subcommand a pattern may allow. Adding one is a reviewed change to this file.
DOCKER_SUBCOMMANDS = {
    "ps", "logs", "port", "images", "version", "info",
    "container ls", "container ps", "container logs", "container port", "image ls", "volume ls", "network ls",
    "compose ps", "compose logs", "compose ls", "compose images", "compose up", "compose down",
}
DOCKER_PROBES = [
    "", "inspect x", "container inspect x", "exec x sh", "container exec x", "cp x:/a b", "run alpine",
    "rm x", "stop x", "volume rm x", "volume prune", "system prune", "compose config", "compose exec x",
    "compose run x", "compose down -v", "compose down -vt1", "compose down --volumes",
    "compose down --rmi all", "logs -f x", "logs --follow x", "compose logs -f",
    "logs --follow=true x", "container logs -tf x", "compose down -tv 1", "container rm x",
    "container export x", "top x", "compose cp x:/a b", "compose rm -f",
]


class ConfigError(Exception):
    pass


def printable(text: str, limit: int = 300) -> str:
    """`text` for a terminal: printable ASCII only, every other character as `?`, at most `limit` characters. Keeps the
    loaders' fixed text intact (`$comment`, `\\b`) while a name quoted from a file cannot move the cursor, clear the
    screen, reorder text or hide characters."""
    return re.sub(r"[^\x20-\x7e]", "?", text)[:limit]


class ProjectFactsError(ConfigError):
    """.claude/project.json cannot be used; str() names the file and the problem, never
    any content of the file, so it is safe in a deny reason."""

    def __init__(self, message: str, kind: str):
        super().__init__(message)
        self.kind = kind


def _load_module(name: str, path: Path):
    """Import a module by absolute path; reuse an already-loaded one only if it is that file."""
    module = sys.modules.get(name)
    if module is not None and Path(getattr(module, "__file__", "") or "").resolve() == path:
        return module
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    try:
        # the source file itself, never bytecode from a __pycache__ folder
        exec(compile(Path(path).read_bytes(), str(path), "exec"), module.__dict__)  # noqa: S102
    except BaseException:
        sys.modules.pop(name, None)
        raise
    return module


def _cfg():
    """The shared loader module (grant rules, project schema); raises ConfigError if it does not load."""
    try:
        return _load_module("_claudecfg", _CLAUDECFG_FILE)
    except Exception as exc:  # noqa: BLE001
        raise ConfigError(f"the loader .claude/scripts/_claudecfg.py does not load ({type(exc).__name__})") from None


def load_project() -> dict:
    """The validated project facts. Raises ProjectFactsError only, so a caller can fail closed."""
    try:
        cfg = _load_module("_claudecfg", _CLAUDECFG_FILE)
    except Exception as exc:  # noqa: BLE001 - the loader itself is missing or broken
        raise ProjectFactsError(".claude/project.json cannot be checked: the loader .claude/scripts/_claudecfg.py "
                                f"does not load ({type(exc).__name__})", "loader") from None
    try:
        return cfg.load_project(PROJECT_FILE)
    except cfg.ProjectConfigError as exc:
        raise ProjectFactsError(str(exc), exc.kind) from None
    except Exception as exc:  # noqa: BLE001 - nothing else may leave the loader
        raise ProjectFactsError(f".claude/project.json could not be loaded ({type(exc).__name__})", "error") from None


def project_reason(hook: str, who: str, exc: BaseException) -> str:
    detail = str(exc) if isinstance(exc, ProjectFactsError) else \
        f".claude/project.json could not be loaded ({type(exc).__name__})"
    return (f"{hook} error (fail closed for {who}): {detail}. The main session can repair .claude/project.json; "
            "run python .claude/scripts/lint.py.")


def docker_allows(patterns: list[re.Pattern[str]], subcommand: str) -> bool:
    """The one match rule for the hook and the validator."""
    return any(p.match(subcommand) for p in patterns)


def _split_lookaheads(pattern: str) -> tuple[str, list[str]]:
    """(positive part, bodies of the `(?!...)` groups), matching parentheses by balance."""
    positive, bodies, i = "", [], 0
    while i < len(pattern):
        if pattern.startswith("(?!", i):
            depth, j = 1, i + 3
            while j < len(pattern) and depth:
                if pattern[j] == "\\":
                    j += 2
                    continue
                depth += {"(": 1, ")": -1}.get(pattern[j], 0)
                j += 1
            if depth:
                raise ConfigError("unbalanced lookahead")
            bodies.append(pattern[i + 3:j - 1])
            i = j
        else:
            positive, i = positive + pattern[i], i + 1
    return positive, bodies


def _expand(text: str) -> list[str]:
    """Literal strings of a positive part without its trailing `\\b` (grammar: lowercase
    words, single spaces, parenthesised alternations; no top-level `|`, no empty alternative)."""
    pos = 0

    def seq(top: bool) -> list[str]:
        nonlocal pos
        out = [""]
        while pos < len(text) and text[pos] not in "|)":
            m = re.compile(r"[a-z][a-z-]*| ").match(text, pos)
            if m:
                out, pos = [o + m.group(0) for o in out], m.end()
            elif text[pos] == "(":
                pos += 1
                alts = [seq(False)]
                while pos < len(text) and text[pos] == "|":
                    pos += 1
                    alts.append(seq(False))
                if pos >= len(text) or text[pos] != ")":
                    raise ConfigError("unclosed group")
                pos += 1
                if len(out) * sum(len(alt) for alt in alts) > MAX_EXPANSIONS:
                    raise ConfigError(f"expands to more than {MAX_EXPANSIONS} subcommands")
                out = [o + a for o in out for alt in alts for a in alt]
            else:
                raise ConfigError(f"character {text[pos]!r} is not allowed outside a lookahead")
        if out == [""]:
            raise ConfigError("empty alternative")
        if top and pos < len(text):
            raise ConfigError("'|' outside parentheses" if text[pos] == "|" else "unbalanced ')'")
        return out

    return seq(True)


_LOOKAHEAD_ATOM = re.compile(r"\\[a-z]|\[(?:[a-z0-9](?:-[a-z0-9])?)+\]|[a-z0-9 =-]|\.|[()|]")


def _check_lookahead(body: str) -> None:
    """at most one leading `.*`; otherwise only `*` on a class like `[a-z]`; no quantified
    group, no `{`, no nested `(?`. the class (letters and digits only, never whitespace or
    `-`) must directly follow `\\s`, a space or `-`, so the runs it can scan are disjoint and
    backtracking stays linear in the command length."""
    if "{" in body or "(?" in body:
        raise ConfigError("lookahead may not contain '{' or a nested '(?'")
    pos, previous = (2 if body.startswith(".*") else 0), None
    while pos < len(body):
        m = _LOOKAHEAD_ATOM.match(body, pos)
        if not m:
            raise ConfigError(f"lookahead character {body[pos]!r} is not allowed")
        atom, pos = m.group(0), m.end()
        if pos < len(body) and body[pos] in "*+?":
            if body[pos] != "*" or not atom.startswith("["):
                raise ConfigError("lookahead quantifier is only allowed as '*' on a class such as [a-z]")
            if previous not in ("\\s", " ", "-"):
                raise ConfigError("a lookahead '[...]*' must directly follow \\s, a space or '-'")
            pos += 1
        previous = atom


def docker_expansions(pattern: str) -> list[str]:
    """The literal subcommands a validated pattern allows (before its exclusions)."""
    positive, _ = _split_lookaheads(pattern)
    if not positive.endswith("\\b"):
        raise ConfigError("must end with \\b")
    return _expand(positive[:-2])


def validate_docker_pattern(pattern: object) -> re.Pattern[str]:
    if not isinstance(pattern, str) or not 0 < len(pattern) <= MAX_PATTERN:
        raise ConfigError(f"pattern must be a string of 1 to {MAX_PATTERN} characters")
    positive, bodies = _split_lookaheads(pattern)
    for body in bodies:
        _check_lookahead(body)
    for expansion in docker_expansions(pattern):
        words = expansion.split(" ")
        if "" in words:
            raise ConfigError("subcommand words must be separated by single spaces")
        if expansion not in DOCKER_SUBCOMMANDS:
            raise ConfigError(f"allows a subcommand that is not on the reviewed list ('{expansion}')")
    try:
        compiled = re.compile(pattern)
    except re.error:
        raise ConfigError("pattern does not compile") from None
    for probe in DOCKER_PROBES:
        if docker_allows([compiled], probe):
            raise ConfigError(f"pattern allows a forbidden form ('{probe}')")
    return compiled


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


def _check_strings(value: object, where: str = "") -> None:
    """No control, zero-width or bidi character in any key or string."""
    if isinstance(value, str):
        if _BAD_CHARS.search(value):
            raise ConfigError(f"{where or 'a value'} contains a control, zero-width or bidi character")
    elif isinstance(value, list):
        for i, item in enumerate(value):
            _check_strings(item, f"{where}[{i}]")
    elif isinstance(value, dict):
        for key, item in value.items():
            _check_strings(key, f"{where}.{key}" if where else "a key")
            _check_strings(item, f"{where}.{key}" if where else str(key))


def validate_boundaries(config: object) -> dict:
    """The schema plus the `docker` and `routing` keys; raises ConfigError on any deviation."""
    if not isinstance(config, dict) or not set(config) <= _TOP_KEYS or "agents" not in config:
        raise ConfigError("top-level keys must be $comment, deny, agents, docker, routing, tools")
    _check_strings(config)
    deny = config.get("deny", [])
    if not isinstance(deny, list) or not all(_valid_rel(g) for g in deny):
        raise ConfigError("deny must be a list of repository-relative globs")
    agents = config["agents"]
    if not isinstance(agents, dict) or not all(
            isinstance(k, str) and k and isinstance(v, list) and all(_valid_rel(g) for g in v)
            for k, v in agents.items()):
        raise ConfigError("agents must map names to lists of repository-relative globs")
    for name in agents:
        if not NAME.fullmatch(name):
            raise ConfigError(f"agents: name '{name}' must match {NAME.pattern}")
    docker = config.get("docker", {})
    if not isinstance(docker, dict):
        raise ConfigError("docker must map agent names to allow-lists")
    for name, entries in docker.items():
        if name not in agents:
            raise ConfigError(f"docker.{name}: not an agent in 'agents'")
        if not isinstance(entries, list) or not entries:
            raise ConfigError(f"docker.{name}: must be a non-empty list")
        for i, entry in enumerate(entries):
            if not isinstance(entry, dict) or set(entry) != {"label", "pattern"}:
                raise ConfigError(f"docker.{name}[{i}]: must have exactly 'label' and 'pattern'")
            if not isinstance(entry["label"], str) or not LABEL.fullmatch(entry["label"]):
                raise ConfigError(f"docker.{name}[{i}].label: must match {LABEL.pattern}")
            try:
                validate_docker_pattern(entry["pattern"])
            except ConfigError as exc:
                raise ConfigError(f"docker.{name}[{i}].pattern: {exc}") from None
    tools = config.get("tools", {})
    problem = _cfg().grants_problem(tools)
    if problem:
        raise ConfigError(f"tools: {problem}")
    for name in tools:
        if name not in agents:
            raise ConfigError(f"tools.{name}: not an agent in 'agents'")
    routing = config.get("routing", {})
    if not isinstance(routing, dict) or not set(routing) <= {"G4"}:
        raise ConfigError("routing may only have the key 'G4'")
    rows = routing.get("G4", [])
    if not isinstance(rows, list):
        raise ConfigError("routing.G4 must be a list")
    for i, row in enumerate(rows):
        if not isinstance(row, dict) or set(row) != {"agent", "summary", "paths"}:
            raise ConfigError(f"routing.G4[{i}]: must have exactly 'agent', 'summary' and 'paths'")
        if row["agent"] not in agents:
            raise ConfigError(f"routing.G4[{i}].agent: not an agent in 'agents'")
        if not isinstance(row["summary"], str) or not LABEL.fullmatch(row["summary"]):
            raise ConfigError(f"routing.G4[{i}].summary: must match {LABEL.pattern}")
        paths = row["paths"]
        if not isinstance(paths, str) or not ROUTE_PATHS.fullmatch(paths) or paths.count("`") % 2:
            raise ConfigError(f"routing.G4[{i}].paths: must match {ROUTE_PATHS.pattern} with paired backticks")
        if not all(_valid_rel(span) for span in route_globs(paths)):
            raise ConfigError(f"routing.G4[{i}].paths: every code span must be a repository-relative glob")
        free = re.sub(r"`[^`]*`", " ", paths)
        if re.search(r"[:/.]", free) or any(len(seg.split()) > ROUTE_FREE_TEXT_WORDS for seg in free.split(",")):
            raise ConfigError(f"routing.G4[{i}].paths: text outside code spans is at most {ROUTE_FREE_TEXT_WORDS} "
                              "words per item, without ':', '/' or '.'")
    return config


def route_globs(paths: str) -> list[str]:
    """The backticked globs of a routing `paths` cell."""
    return re.findall(r"`([^`]*)`", paths)


def docker_patterns(config: dict, agent: str) -> list[re.Pattern[str]]:
    """The validated allow-list of one agent; [] when it has none (no in-code fallback)."""
    return [validate_docker_pattern(e["pattern"]) for e in config.get("docker", {}).get(agent, [])]


def _no_duplicate_keys(pairs: list[tuple[str, object]]) -> dict:
    keys = [k for k, _ in pairs]
    duplicates = sorted({k for k in keys if keys.count(k) > 1})
    if duplicates:
        raise ConfigError(f"duplicate key '{duplicates[0]}'")
    return dict(pairs)


def parse_boundaries(text: str) -> dict:
    """The one loader for the hook, lint and roster: duplicate keys are errors."""
    return validate_boundaries(json.loads(text, object_pairs_hook=_no_duplicate_keys))


def load_boundaries() -> dict:
    return parse_boundaries((ROOT / ".claude" / "boundaries.json").read_text(encoding="utf-8-sig"))


def frontmatter_names() -> set[str]:
    """The `name:` of every agent file. Raises ConfigError when one is missing or is not a plain agent name (a folded
    value, a dot, upper case): the caller then treats every agent as listed, so no agent file escapes the hooks."""
    names = set()
    for path in (ROOT / ".claude" / "agents").glob("*.md"):
        m = re.search(r"^name:[ \t]*(.*?)[ \t]*$", path.read_text(encoding="utf-8-sig"), re.MULTILINE)
        value = m.group(1).strip("\"'") if m else ""
        if not NAME.fullmatch(value):
            raise ConfigError("an agent file has no plain name")
        names.add(value)
    return names


def listed_agents() -> tuple[set[str] | None, dict | None]:
    """(listed agent names or None if unknown, validated boundaries config or None). The project's agents are the
    boundaries.json entries AND the names in .claude/agents/*.md, so an agent file without an entry is listed (and
    denied for want of one) instead of passing as a built-in agent."""
    try:
        config = load_boundaries()
    except Exception:  # noqa: BLE001
        config = None
    if config is not None:
        try:
            return set(config["agents"]) | frontmatter_names(), config
        except Exception:  # noqa: BLE001
            return None, config  # unknown: every agent is treated as listed
    try:
        names = frontmatter_names()
        return (names or None), None
    except Exception:  # noqa: BLE001
        return None, None


def is_listed(agent: str, listed: set[str] | None) -> bool:
    if not agent:
        return False
    # compared without case: a name that differs only in case from an agent file is that agent, never a built-in
    return agent.lower() in {name.lower() for name in listed} if listed is not None else True


# --- Bash allow-list source and freeze on deny ---
# D1: each listed agent's allowed Bash patterns are the Bash(...) entries of the `tools:` line in
# .claude/agents/<agent>.md. Claude Code applies only tool names to subagents, so the hook is what
# enforces the patterns (T-02). D2 grammar: exact, or a prefix ending in " *" (legacy ":*" reads as
# " *"); no other "*", and never the no-space "x*" (`git diff*` would include `git difftool`).
AGENT_TOOL_NAMES = {"Read", "Grep", "Glob", "Write", "Edit", "MultiEdit", "NotebookEdit"}  # plus Bash(...)
AGENT_FRONTMATTER_KEYS = {"name", "description", "tools", "model"}
_PATTERN_BODY = re.compile(r"[A-Za-z0-9._/=-]+(?: [A-Za-z0-9._/=-]+)*")
AGENT_ID = re.compile(r"[A-Za-z0-9_-]{1,128}")
FREEZE_NOTICE = ("This agent run is now frozen: every further Bash, Write or Edit call will be denied. Stop now. "
                 "Report to your caller the command or path you tried, this reason, and what you needed it for. "
                 "Do not retry, rephrase, split it, or work around it with another tool, program or location.")


# Programs that run other programs or code: a tools-line Bash pattern may not start with one (by its bare name,
# without path, suffix or version), since the pattern would let the agent run anything. The one exception is an
# interpreter running exactly one core script, such as `python .claude/scripts/lint.py`.
RUNNERS = frozenset({
    "python", "py", "pythonw", "node", "nodejs", "deno", "bun", "bunx", "bash", "sh", "zsh", "dash", "ksh", "fish",
    "pwsh", "powershell", "cmd", "env", "xargs", "npx", "pnpx", "uv", "uvx", "pipx", "ruby", "perl", "php", "lua",
    "java", "wsl", "busybox", "sudo", "doas", "nohup", "timeout", "nice", "time", "exec", "eval", "source", ".",
    "make", "osascript", "cscript", "wscript", "mshta", "rundll32", "start", "find", "awk", "gawk", "mawk", "sed",
    "script", "watch", "ssh", "command", "builtin", "tar",
})
# git on a tools line: only subcommands that read or fetch; rebase -x, bisect run, submodule foreach, config (which can
# set core.fsmonitor or a pager), difftool and the like run other programs
GIT_TOOLS_SUBCOMMANDS = frozenset({
    "status", "diff", "log", "show", "blame", "grep", "ls-files", "ls-tree", "rev-parse", "rev-list", "cat-file",
    "describe", "shortlog", "merge-base", "fetch", "branch", "tag", "remote", "reflog", "for-each-ref",
})
# dotnet subcommands that run a script, an app or a tool directly
DOTNET_RUNNERS = frozenset({"fsi", "run", "exec"})
_CORE_SCRIPT = re.compile(r"\.claude/scripts/[A-Za-z0-9_-]+\.py")


def program_name(word: str) -> str:
    """A command word's bare program name: no folder, no .exe/.cmd/.bat/.com/.ps1/.sh suffix, no version, lower case."""
    name = re.split(r"[/\\]", word)[-1].lower()
    name = re.sub(r"\.(exe|cmd|bat|com|ps1|sh)$", "", name)
    return re.sub(r"[0-9.]+$", "", name) or name


def _reviewed_uv(words: list[str], wildcard: bool) -> bool:
    """uv runs other programs, so only its reviewed forms pass: an exact command of the loader's EXACT_GRANTS, or a
    pattern that starts with a reviewed `uv run` prefix (GRANT_PREFIXES; the hook then checks the tool's options)."""
    cfg = _cfg()
    if not wildcard and " ".join(words) in cfg.EXACT_GRANTS:
        return True
    return cfg.uv_run_tool(words) is not None


def runner_problem(pattern: str) -> str | None:
    """Why a normalised tools-line pattern reaches a program that runs other programs, or None."""
    wildcard = pattern.endswith(" *")
    words = (pattern[:-2] if wildcard else pattern).split()
    name = program_name(words[0])
    if name in RUNNERS:
        if name in ("python", "py") and not wildcard and len(words) == 2 and _CORE_SCRIPT.fullmatch(words[1]):
            return None
        if words[0] == "uv" and _reviewed_uv(words, wildcard):
            return None
        return f"Bash({pattern}) starts with {name}, which runs other programs; name the exact command instead"
    if name == "git" and (len(words) == 1 or words[1].startswith("-")):
        return f"Bash({pattern}) lets git take a leading option (-c, -C, --exec-path) or any subcommand"
    if name == "git" and words[1] not in GIT_TOOLS_SUBCOMMANDS:
        return f"Bash({pattern}): git {words[1]} is not a subcommand an agent may declare (it can run other programs)"
    if name == "dotnet" and len(words) > 1 and (words[1] in DOTNET_RUNNERS or words[1:3] in (["tool", "run"], ["tool", "exec"])
                                                or (words[1] == "tool" and len(words) == 2 and wildcard)):
        return f"Bash({pattern}) runs a script, an app or a tool directly"
    if name == "dotnet" and len(words) == 1 and wildcard:
        return f"Bash({pattern}) leaves every dotnet subcommand open"
    return None


def pattern_body(body: str) -> str:
    """The normalised body of one Bash(...) entry; raises ConfigError if it breaks the D2 grammar or starts with a
    program that runs other programs."""
    text = body.strip()
    if text.endswith(":*"):
        text = text[:-2].rstrip() + " *"
    prefix = text[:-2] if text.endswith(" *") else text
    if "*" in prefix or not _PATTERN_BODY.fullmatch(prefix) or len(text) > MAX_PATTERN:
        raise ConfigError(f"Bash({body}) must be an exact command or end in ' *' (letters, digits, ._/=- only)")
    pattern = " ".join(prefix.split()) + (" *" if text.endswith(" *") else "")
    problem = runner_problem(pattern)
    if problem:
        raise ConfigError(problem)
    return pattern


def tools_line(text: str) -> str:
    """The single-line `tools:` value of an agent file's frontmatter; raises ConfigError otherwise."""
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        raise ConfigError("agent file has no frontmatter")
    end = next((i for i, line in enumerate(lines[1:], start=1) if line.strip() == "---"), None)
    if end is None:
        raise ConfigError("agent frontmatter is not closed")
    found = [line for line in lines[1:end] if re.match(r"tools\s*:", line)]
    if len(found) != 1:
        raise ConfigError("agent frontmatter needs exactly one single-line tools:")
    return found[0].split(":", 1)[1].strip()


def tool_entries(value: str) -> list[str]:
    """The comma-separated entries of a tools value (a ',' inside Bash(...) is a lint error)."""
    return [e.strip() for e in value.split(",") if e.strip()]


def bash_patterns(agent: str) -> list[str]:
    """The agent's normalised Bash patterns; [] when it declares none. Raises (the hook then denies)
    on a bad name, a missing file or tools line, a bare `Bash` or a pattern that breaks D2."""
    if not NAME.fullmatch(agent):
        raise ConfigError("agent name is not valid")
    text = (ROOT / ".claude" / "agents" / f"{agent}.md").read_text(encoding="utf-8-sig")
    patterns = []
    for entry in tool_entries(tools_line(text)):
        if entry == "Bash":
            raise ConfigError("bare Bash grants every command")
        m = re.fullmatch(r"Bash\((.*)\)", entry)
        if m:
            patterns.append(pattern_body(m.group(1)))
    return patterns


def granted_patterns(agent: str, config: dict | None, project: dict) -> list[str]:
    """Extra Bash patterns for one agent: the installed packs' `agent_tools` (validated by the project loader) and
    the project's boundaries.json `tools`. Without a valid boundaries config there are no project grants (fewer
    rights, never more). The deny lists of the hook still apply to every granted command."""
    grants = list(project.get("pack_agent_tools", {}).get(agent, []))
    if config is not None:
        grants += config.get("tools", {}).get(agent, [])
    for grant in grants:
        if _cfg().grant_problem(grant):  # checked once more where it is used
            raise ConfigError("a granted Bash pattern is not allowed")
    return [pattern_body(g) for g in grants]


def pattern_allows(pattern: str, words: list[str]) -> bool:
    """Word by word: a quoted 'git diff' is one word and matches neither `git diff`
    nor `git diff *`, because bash would run it as a single program name."""
    if pattern.endswith(" *"):
        prefix = pattern[:-2].split(" ")
        return words[:len(prefix)] == prefix
    return words == pattern.split(" ")


def frozen_dir() -> Path:
    return LOG_DIR / "frozen"


def freeze_state(payload: dict) -> str | None:
    """None when this run may continue; otherwise the deny rule. Fails closed: only a missing
    marker means "not frozen" (os.lstat, never Path.exists(), which hides errors; D4)."""
    agent_id = payload.get("agent_id")
    if not isinstance(agent_id, str) or not AGENT_ID.fullmatch(agent_id):
        return "freeze.no-agent-id"
    try:
        # The folder first: on Windows, lstat of a path under a file raises FileNotFoundError, not
        # NotADirectoryError, so a file in its place would otherwise read as "not frozen".
        if not stat.S_ISDIR(os.lstat(frozen_dir()).st_mode):
            return "freeze.unreadable"
        os.lstat(frozen_dir() / agent_id)
    except FileNotFoundError:
        return None
    except Exception:  # noqa: BLE001 - NotADirectoryError, PermissionError, ...: deny
        return "freeze.unreadable"
    return "freeze.frozen"


def freeze(payload: dict, rule: str) -> str | None:
    """Write this run's marker before the deny is emitted. None on success (or when it already
    exists), "write-failed" otherwise; the deny stands either way (T114-07)."""
    agent_id = payload.get("agent_id")
    if not isinstance(agent_id, str) or not AGENT_ID.fullmatch(agent_id):
        return "write-failed"
    try:
        frozen_dir().mkdir(parents=True, exist_ok=True)
        marker = {"ts": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                  "agent_type": str(payload.get("agent_type"))[:64], "tool": str(payload.get("tool_name"))[:32],
                  "rule": rule[:64]}
        with open(frozen_dir() / agent_id, "x", encoding="utf-8") as fh:
            fh.write(json.dumps(marker) + "\n")
    except FileExistsError:
        return None
    except Exception as exc:  # noqa: BLE001
        print(f"hook: freeze marker not written ({type(exc).__name__})", file=sys.stderr)
        return "write-failed"
    return None


def frozen_reason(agent: str) -> str:
    return (f"{agent}: this agent run is frozen after an earlier deny. Stop now and report to your "
            "caller: the denied command or path, the reason you were given, and what you needed. Do not retry "
            "or rephrase. Read, Grep and Glob still work for writing your report.")


def deny_listed(hook: str, payload: dict, rule: str, reason: str, path: str | None = None,
                decision: str = "deny") -> None:
    """Every deny for a listed agent: freeze the run first, then emit the reason with FREEZE_NOTICE,
    then audit. A run without a valid agent_id has nothing to key a marker on."""
    state = None if rule == "freeze.no-agent-id" else freeze(payload, rule)
    text = reason if rule.startswith("freeze.") else f"{reason} {FREEZE_NOTICE}"
    emit("deny", text)
    audit(hook, payload, decision, rule, path, freeze=state)


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


def audit(hook: str, payload: dict, decision: str, rule: str, path: str | None = None,
          freeze: str | None = None) -> None:
    """Append one deny record. Never raises and never affects the decision."""
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
        if payload.get("agent_id") is not None:
            record["agent_id"] = payload.get("agent_id")  # which run a deny froze
        if path is not None:
            record["path"] = path
        if freeze is not None:
            record["freeze"] = freeze
        record = {k: (str(v)[:256] if v is not None else None) for k, v in record.items()}
        LOG_DIR.mkdir(exist_ok=True)
        if LOG_FILE.exists() and LOG_FILE.stat().st_size > LOG_ROTATE_BYTES:
            LOG_FILE.replace(LOG_FILE.with_name("hooks.jsonl.1"))
        with open(LOG_FILE, "a", encoding="utf-8") as fh:
            fh.write(json.dumps(record, ensure_ascii=True) + "\n")
    except Exception as exc:  # noqa: BLE001
        print(f"hook: audit log not written ({type(exc).__name__})", file=sys.stderr)
