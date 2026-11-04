#!/usr/bin/env python3
"""PreToolUse hook (Bash): block commands that would read .env files or user-secrets.


Permission deny rules on Read do not cover shell commands such as `cat .env`, so this hook covers
the obvious forms for every session and agent. It is a guardrail, not a security boundary
(CLAUDE.md). Host sessions are the default, and there nothing stands behind it: any
code that runs as the user can read these files. Only an optional sandbox run has a
boundary, because no host secret is mounted there.

How it decides:
1. A small quote-state scanner marks each character as unquoted code, single-quoted,
   double-quoted or comment. Lines are read in order with the quote state carried across them,
   and escaping backslash-newline continuations are joined as bash does (never in a heredoc body).
2. A heredoc body is data (not scanned) only when a `git commit` or `gh pr|issue
   create|comment|edit` command owns it: the line starts outside any quote or heredoc body, the
   `<<` is in unquoted code with no comment before it, it is the only `<<` on the line, the
   delimiter is quoted (so bash expands nothing in the body) and nothing follows it, the command
   starts the line or follows only `cd <path>` / `git add <paths>` joined by `;`, `&&` or `||`,
   and no unquoted `;&|<>`, `$(` or backtick precedes it. The body ends at the first line that
   equals the delimiter exactly. After any other heredoc, the rest of the command is scanned.
3. In a single simple command of `git log|show|commit`, `gh issue|pr create|comment|edit`,
   `grep`, `rg` or `git grep`, the values of text options (e.g. --grep, -m, --title, the grep
   pattern) are not scanned when they contain no `$` or backtick. File-valued options, paths and
   everything after `--` are always scanned.
4. The name patterns are matched on the remaining text and on a normalised copy (literal string
   concatenations joined, quotes and backslashes removed, one-letter classes unwrapped).
`.env.example` (that exact name, any case) is allowed.

5. A blocked folder is also refused when written with a leading or inner wildcard (`*.ops`) or as its 8.3 short
   name, and a recursive search (grep -r, rg with hidden files, find -exec) is refused when it starts at the
   repository root or above it, judged from the payload's working folder and literal `cd`s (root_search).
6. A command that concerns a reference folder (secret_guard.reference_folders, ADR 0012: it names one, runs inside one,
   or cleans or stashes with git) passes only as a plain read: every command a listed reader named by its bare first
   word, with nothing outside single quotes that the shell would expand, run or redirect. Anything else asks the owner
   in the main session (in the modes where a hook's ask is known to prompt; denied in any other) and is denied for a
   subagent. It runs after the checks above, in its own error handler, which refuses the plain pass.

Accepted residuals (out of scope for a pattern guard; accepted for host sessions,
and only a sandbox run removes the files they would reach): names built at run time
($(printf ...), variables, chr(), base64), other recursive readers that never name the file (tar, cp -r, a
search after a `cd` the text cannot follow), scripts the agent writes and runs, indirect disclosure through
process or container environments, listing the user-secrets folder via an environment variable.
Known false positives (the main session or the owner runs them): ls/test -f/git check-ignore/echo of
the names outside the exempt text options.

For a project agent listed in .claude/boundaries.json, any error denies the call (fail closed);
the main session fails open. One exception: when .claude/project.json cannot be
loaded, every session is denied, because the blocked folders come from it; the main session can
still repair the file with its edit tools. Denies are written to the local audit log
(.agent-logs/hooks.jsonl) without any command text.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import fnmatch
import os
import re
import shlex
import sys
from pathlib import Path

try:
    # _hooklib from its source file, never from bytecode in __pycache__ (a planted .pyc would run in the hook)
    import importlib.util
    _LIB = Path(__file__).resolve().parent / "_hooklib.py"
    lib = sys.modules.get("_hooklib")
    # loaded by agent_boundaries in the same process (its deny message asks this guard): the same file, kept
    if lib is None or Path(getattr(lib, "__file__", "") or "").resolve() != _LIB:
        lib = importlib.util.module_from_spec(importlib.util.spec_from_file_location("_hooklib", _LIB))
        sys.modules["_hooklib"] = lib
        exec(compile(_LIB.read_bytes(), str(_LIB), "exec"), lib.__dict__)  # noqa: S102
except Exception as _exc:  # noqa: BLE001 - the listed set is unknown, so deny any subagent
    import json
    try:
        _agent = json.load(sys.stdin).get("agent_type") or ""
    except Exception:  # noqa: BLE001
        _agent = ""
    print(f"secret_guard: cannot load _hooklib ({type(_exc).__name__})", file=sys.stderr)
    if _agent:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                                 "permissionDecisionReason": f"secret_guard error (fail closed for "
                                                 f"{_agent}): {type(_exc).__name__} loading _hooklib."}}))
    sys.exit(0)

HOOK = "secret_guard"

_LEAD = r"(?:^|(?<=[\s/\\'\"=<>`{(,@:])|(?<=\s-[A-Za-z]))"
_TRAIL_CHARS = r"\s'\";|&)>`,}\]:.~+*?["
_CLASS = r"(?:\?|\[[^\]\n]{1,8}\])"
# The core floor: project facts can add patterns, never remove these. match_rule's
# `index == 1` rule points at the second entry.
CORE_PATTERNS = [
    ("secret.dotenv", re.compile(_LEAD + r"(\.env(?:\.[\w.-]*)?)(?=$|[" + _TRAIL_CHARS + r"])", re.IGNORECASE)),
    ("secret.dotenv", re.compile(_LEAD + r"\.(?:e|" + _CLASS + r")(?:n|" + _CLASS + r")(?:v|" + _CLASS + r")", re.IGNORECASE)),
    ("secret.dotenv", re.compile(_LEAD + r"\.en?\*|\*\.?env\b|\.e['\"]+n|\.en['\"]+v", re.IGNORECASE)),
    ("secret.user-secrets", re.compile(r"UserSecrets|user-secrets\b|secrets\.(?:j|\?|\*|\[)", re.IGNORECASE)),
]


SHARED_PREFIX = ".local"  # local-only folders are named .local.<name>: the prefix alone tells none apart


def patterns_for(project: dict) -> list[tuple[str, re.Pattern[str]]]:
    """CORE_PATTERNS plus one pattern per local-only folder: the folder's first part,
    also with a glob or class standing in for the dot or a letter after it. For a folder
    `.<a>.<b>` the stem is `.<a>`. For `.local.<b>` it is `.local`, the dot and the first
    letter of `<b>`, each of which a glob or class may stand in for: a blocked `.local.<b>`
    also blocks `.local.*` and every `.local.` folder whose name starts with the same
    letter, but not a sibling that starts with another, nor the user's own `~/.local/`. A leading or inner
    wildcard and an 8.3 short name are caught by _OtherSpellings, a recursive search from the root by root_search.
    Not caught, as for every folder: a name built at run time (a variable or command holding it)."""
    extra = []
    for name in project["secret_guard"]["blocked_folders"]:
        stem = name[:name.index(".", 1)]
        if stem.lower() == SHARED_PREFIX:
            # after the dot (or a class, brace or variable in its place): the letter, or anything that is not the
            # plain first letter of another name, such as a glob, quote, escape, backtick or group. The end of the
            # text or a space after it starts no name, so a line that ends in ".local." passes; a closing quote there
            # does too, while a quote that a name follows still counts
            letter = re.escape(name[len(stem) + 1])
            tail = (r"(?:\*|(?:\.|\{|\$|" + _CLASS + r")(?:" + letter + r"|(?=[^A-Za-z0-9_<\s'\"-])"
                    r"|['\"](?=[^\s'\"])))")
        else:
            tail = r"(?:\.|\?|\[[^\]\n]{1,8}\]|\*)"
        extra.append(("secret.local-folder", re.compile(_LEAD + re.escape(stem) + tail, re.IGNORECASE)))
    if project["secret_guard"]["blocked_folders"]:
        extra.append(("secret.local-folder", _OtherSpellings(project["secret_guard"]["blocked_folders"])))
    return CORE_PATTERNS + extra


_WORD_SPLIT = re.compile(r"[\s'\"`;|&()<>=]+")
_GLOB_CHARS = re.compile(r"[*?\[]")
_BRACE = re.compile(r"\{([^{}]{0,200})\}")
_POSIX_CLASS = re.compile(r"\[[^\]]*\[:[a-z]+:\][^\]]*\]")  # [[:alpha:]] and the like: any one character
MAX_EXPANSIONS = 64
_ALIAS = re.compile(r"([^~/\\.]{1,6})~[0-9]{1,6}(?:\.([^/\\.]{0,3}))?", re.IGNORECASE)
MIN_GLOB_LITERALS = 3  # a glob keeps at least this much of the name, so `*` alone or `*s` never counts


class _Spelled:
    """What match_rule reads from a match: the text found."""

    def __init__(self, text: str):
        self.text = text

    def groups(self) -> tuple:
        return ()

    def group(self, _index: int = 0) -> str:
        return self.text


class _OtherSpellings:
    """Spellings of a blocked folder that its prefix pattern cannot see (issue #32 item 11): a path
    part with a leading or inner wildcard that matches the whole name and keeps at least MIN_GLOB_LITERALS of its
    characters (`*.ops`, `?local.ops`, `.lo*ops`), and an 8.3 short name whose base and extension fit the name's
    (`LOCAL~1.OPS`). Compared without case, by text only: nothing is looked up on disk."""

    def __init__(self, names: list[str]):
        self.names = [n.lower() for n in names]

    def finditer(self, text: str):
        for word in _WORD_SPLIT.split(text):
            for spelled in _expand_braces(word):
                for part in re.split(r"[/\\]", spelled)[:64]:
                    if part and len(part) <= 255 and self._names(part.lower()):
                        yield _Spelled(part)

    def _names(self, part: str) -> bool:
        if part in self.names:
            return True  # the name itself, as a brace expansion spells it out
        if _GLOB_CHARS.search(part):
            # as the shell reads a glob: [^x] negates like [!x], a POSIX class is any one character
            glob = _POSIX_CLASS.sub("?", part.replace("[^", "[!"))
            chunks = [c for c in re.split(r"\[[^\]]*\]|[*?]", glob) if c]
            if sum(map(len, chunks)) >= MIN_GLOB_LITERALS:
                for name in self.names:
                    # every literal piece must be in the name before the glob is matched: fnmatch compiles each
                    if all(c in name for c in chunks) and fnmatch.fnmatchcase(name, glob):
                        return True
        alias = _ALIAS.fullmatch(part)
        if alias:
            base, ext = re.sub(r"[^a-z0-9]", "", alias.group(1)), alias.group(2) or ""
            for name in self.names:
                name_base, _, name_ext = name.lstrip(".").rpartition(".")
                squeezed = re.sub(r"[^a-z0-9]", "", name_base)
                if ext and name_ext.startswith(ext) and squeezed[:2] == base[:2]:
                    return True
        return False


def _expand_braces(word: str) -> list[str]:
    """A word with its brace alternatives expanded as bash would (`.lo{c,}al` gives `.local` and `.loal`), at most
    MAX_EXPANSIONS spellings; a word without braces is itself."""
    spellings = [word]
    for _ in range(8):
        grown = []
        for spelled in spellings:
            m = _BRACE.search(spelled)
            if not m or "," not in m.group(1):
                grown.append(spelled)
                continue
            grown += [spelled[:m.start()] + option + spelled[m.end():] for option in m.group(1).split(",")]
        if grown == spellings or len(grown) > MAX_EXPANSIONS:
            break
        spellings = grown
    return spellings


def blocked_reason(project: dict) -> str:
    names = ["a .env file", "user-secrets", *(f"{f}/" for f in project["secret_guard"]["blocked_folders"])]
    listed = names[0] if len(names) == 1 else ", ".join(names[:-1]) + " or " + names[-1]
    return (f"Blocked: this command touches {listed} (CLAUDE.md security principles). "
            "Ask the owner for the value's name, never its content.")


def _project_patterns() -> list[tuple[str, re.Pattern[str]]]:
    return patterns_for(lib.load_project())

# Checked per command segment in match_rule: one regex over the whole text was quadratic (a
# crafted 255 KB command took 95 s, past the hook timeout, which lets the call through).
COMPOSE = re.compile(r"docker[- ]compose\b", re.IGNORECASE)
COMPOSE_VERB = re.compile(r"\b(?:config|convert)\b", re.IGNORECASE)

EXEMPT_OWNER = re.compile(r"^\s*(?:git(?:\s+-c\s+\S+)*\s+commit\b|gh\s+(?:pr|issue)\s+(?:create|comment|edit)\b)")
SAFE_PREFIX = re.compile(r"^\s*(?:cd\s+[^\s;&|<>$`()]+|git\s+add(?:\s+[^\s;&|<>$`()]+)+)\s*$")
TEXT_OPTIONS = {
    ("git", "log"): {"--grep", "-S", "-G", "--author"},
    ("git", "show"): {"--grep", "-S", "-G", "--author"},
    ("git", "commit"): {"-m", "--message"},
    ("gh", "issue"): {"--title", "--body", "-t", "-b"},
    ("gh", "pr"): {"--title", "--body", "-t", "-b"},
}
GH_TEXT_VERBS = {"create", "comment", "edit"}
PATTERN_PROGRAMS = {"grep", "rg"}
# grep/rg/git grep options that take a separate value: the value is never the pattern.
PATTERN_VALUE_OPTIONS = {"-A", "-B", "-C", "-m", "--max-count", "-g", "--glob", "-t", "--type", "-T",
                         "--type-not", "-d", "-D", "--include", "--exclude", "--exclude-dir", "-M", "-j"}
FILE_OPTIONS = {"-f", "--file", "-F", "--body-file", "--template"}
# Short options per program: letters that name a file, and letters that take the rest of their
# token as a value. Used to read clusters such as `-rf <file>`.
SHORT_FILE = {"grep": "f", "rg": "f", "git grep": "f", "git commit": "Ft", "gh": "F"}
SHORT_VALUE = {"grep": "ABCmedD", "rg": "ABCmegtTMj", "git grep": "ABCmeO", "git commit": "mcC",
               "git log": "SGn", "git show": "SGn", "gh": "tbRBHlaApj"}


def scan(cmd: str, mode: str = "c") -> list[str]:
    """Per character: 'c' unquoted code, 's' single-quoted, 'd' double-quoted, '#' comment."""
    return scan_line(cmd, mode)[0]


def scan_line(cmd: str, mode: str = "c") -> tuple[list[str], str, bool]:
    """(states, mode at the end, ends with an escaping backslash so continues on the next line)."""
    states, i = [], 0
    while i < len(cmd):
        ch = cmd[i]
        if mode == "c":
            if ch == "\\":
                if i + 1 == len(cmd):
                    return states + ["c"], mode, True
                states += ["c", "c"]
                i += 2
                continue
            if ch == "'":
                mode = "s"
                states.append("s")
            elif ch == '"':
                mode = "d"
                states.append("d")
            elif ch == "#" and (i == 0 or cmd[i - 1] in " \t\n;&|("):
                mode = "#"
                states.append("#")
            else:
                states.append("c")
        elif mode == "s":
            states.append("s")
            if ch == "'":
                mode = "c"
        elif mode == "d":
            if ch == "\\":
                if i + 1 == len(cmd):
                    return states + ["d"], mode, True
                states += ["d", "d"]
                i += 2
                continue
            states.append("d")
            if ch == '"':
                mode = "c"
        else:  # comment
            states.append("#" if ch != "\n" else "c")
            if ch == "\n":
                mode = "c"
        i += 1
    return states, ("c" if mode == "#" else mode), False


def join_continuations(cmd: str) -> str:
    """Remove escaping backslash-newline pairs outside single quotes and comments. A backslash
    that is itself escaped (two backslashes, then a newline) does not continue."""
    states = scan(cmd)
    out, i = [], 0
    while i < len(cmd):
        if cmd[i] == "\\" and states[i] in "cd" and i + 1 < len(cmd):
            if cmd[i + 1] != "\n":
                out.append(cmd[i:i + 2])
            i += 2
            continue
        out.append(cmd[i])
        i += 1
    return "".join(out)


def _split_unquoted(text: str, states: list[str]) -> list[str]:
    """Split a line on unquoted `;`, `&&` and `||`."""
    parts, start, i = [], 0, 0
    while i < len(text):
        if states[i] == "c" and (text[i] == ";" or text[i:i + 2] in ("&&", "||")):
            parts.append(text[start:i])
            i += 1 if text[i] == ";" else 2
            start = i
            continue
        i += 1
    parts.append(text[start:])
    return parts


def _exempt_delimiter(line: str) -> tuple[str, bool] | None:
    """(delimiter, strip leading tabs) if this line opens an exempt heredoc, else None.
    Only a quoted delimiter qualifies: with `<<EOF` bash runs `$(...)` in the body."""
    if line.count("<<") != 1:
        return None
    states = scan(line)
    pos = line.index("<<")
    if line[pos:pos + 3] == "<<<" or states[pos] != "c" or "#" in states[:pos]:
        return None
    m = re.match(r"<<(-?)\s*(['\"])(\w+)\2\s*$", line[pos:])
    if not m:
        return None
    segments = _split_unquoted(line[:pos], states[:pos])
    owner, prefixes = segments[-1], segments[:-1]
    if not EXEMPT_OWNER.match(owner) or not all(SAFE_PREFIX.match(p) for p in prefixes):
        return None
    offset = pos - len(owner)
    for j, ch in enumerate(owner):
        if states[offset + j] == "c" and (ch in ";&|<>`" or owner[j:j + 2] == "$("):
            return None
    return m.group(3), m.group(1) == "-"


def _opens_heredoc(line: str, mode: str = "c") -> bool:
    states = scan(line, mode)
    return any(states[p] == "c" and line[p:p + 2] == "<<" and line[p:p + 3] != "<<<" and (p == 0 or line[p - 1] != "<")
               for p in range(len(line) - 1))


def strip_exempt_heredocs(cmd: str) -> str:
    """Drop exempt heredoc bodies. Lines are read as bash reads them: quote state carries across
    lines, continuations are joined, and a line inside a quote or another heredoc's body never
    opens an exempt heredoc. After any non-exempt heredoc the rest is kept as is."""
    lines, out, i, mode = cmd.split("\n"), [], 0, "c"
    while i < len(lines):
        start_mode, parts = mode, [lines[i]]
        i += 1
        _, mode, cont = scan_line(parts[0], start_mode)
        while cont and i < len(lines):  # scan only the new piece: the state carries over the join
            parts[-1] = parts[-1][:-1]
            parts.append(lines[i])
            i += 1
            _, mode, cont = scan_line(parts[-1], mode)
        line = "".join(parts)
        out.append(line)
        if "<<" not in line or not _opens_heredoc(line, start_mode):
            continue
        # a heredoc on a line that starts or ends inside a quote is never exempt
        exempt = _exempt_delimiter(line) if start_mode == mode == "c" else None
        if exempt is None:
            out.extend(lines[i:])  # another heredoc: its body and everything after it are scanned
            break
        delimiter, tabs = exempt
        while i < len(lines) and (lines[i].lstrip("\t") if tabs else lines[i]) != delimiter:
            i += 1  # message body: data, not scanned
        if i < len(lines):
            out.append(lines[i])
            i += 1
    return "\n".join(out)


def _is_simple(cmd: str) -> bool:
    text = cmd.strip()
    if "\n" in text:
        return False
    states = scan(text)
    return not any(states[i] == "c" and (ch in ";&|<>`" or text[i:i + 2] == "$(") for i, ch in enumerate(text))


def drop_text_option_values(cmd: str) -> str:
    """Remove text-option values of a single simple command; else unchanged.
    Commands over 16 KiB are scanned in full: shlex tokenising is quadratic on one huge token
    (a 1 MiB command took 47 s), and the exemption only matters for ordinary commands."""
    if len(cmd) > 16 * 1024 or not _is_simple(cmd):
        return cmd
    try:
        tokens = shlex.split(cmd, posix=True)
        raw = shlex.split(cmd, posix=False)
    except ValueError:
        return cmd
    if len(tokens) != len(raw) or not tokens:
        return cmd
    prog = tokens[0]
    key = tuple(tokens[:2])
    if prog == "git" and len(tokens) > 1 and tokens[1] == "grep":
        options, pattern_program = {"-e", "--regexp"}, True
    elif prog in PATTERN_PROGRAMS:
        options, pattern_program = {"-e", "--regexp"}, True
    elif key in TEXT_OPTIONS and (prog != "gh" or (len(tokens) > 2 and tokens[2] in GH_TEXT_VERBS)):
        options, pattern_program = TEXT_OPTIONS[key], False
    else:
        return cmd
    if any(t in FILE_OPTIONS or t.startswith(("--file=", "--body-file=", "--template=")) for t in tokens):
        return cmd  # a file-valued option means nothing in this command is exempt
    family = f"git {tokens[1]}" if prog == "git" and len(tokens) > 1 else prog
    if any(_short_file_option(t, family) for t in tokens[1:]):
        return cmd  # the same for a file option inside a cluster, e.g. `grep -rf <file>`
    kept, i, saw_e, pattern_done, after_dashdash = [], 0, False, False, False
    start = 2 if prog == "git" or prog == "gh" else 1
    kept = tokens[:start]
    i = start
    while i < len(tokens):
        tok, raw_tok = tokens[i], raw[i]
        if after_dashdash:
            kept.append(tok)
        elif tok == "--":
            after_dashdash = True
            kept.append(tok)
        elif tok in options and i + 1 < len(tokens):
            saw_e = saw_e or tok in {"-e", "--regexp"}
            if "$" in raw[i + 1] or "`" in raw[i + 1]:
                kept += [tok, tokens[i + 1]]
            i += 1
        elif any(tok.startswith(o + "=") or (len(o) == 2 and tok.startswith(o) and len(tok) > 2) for o in options):
            saw_e = saw_e or tok.startswith(("-e", "--regexp"))
            if "$" in raw_tok or "`" in raw_tok:
                kept.append(tok)
        elif pattern_program and tok in PATTERN_VALUE_OPTIONS and i + 1 < len(tokens):
            kept += [tok, tokens[i + 1]]
            i += 1
        elif pattern_program and not tok.startswith("-") and not saw_e and not pattern_done:
            pattern_done = True
            if "$" in raw_tok or "`" in raw_tok:
                kept.append(tok)
        else:
            kept.append(tok)
        i += 1
    return " ".join(kept)


def _short_file_option(token: str, family: str) -> bool:
    """True if a short-option cluster such as `-rf` or `-aF` contains a file-valued option."""
    if not re.fullmatch(r"-[A-Za-z]\S+", token):
        return False
    for letter in token[1:]:
        if letter in SHORT_FILE.get(family, ""):
            return True
        if letter in SHORT_VALUE.get(family, "") or not letter.isalpha():
            return False  # the rest of the token is this option's value
    return False


def normalise(text: str) -> str:
    text = re.sub(r"'\s*\+\s*'", "", text)
    text = re.sub(r"\"\s*\+\s*\"", "", text)
    text = re.sub(r"['\"\\]", "", text)
    return re.sub(r"\[(\w)\]", r"\1", text)


def match_rule(text: str, patterns: list[tuple[str, re.Pattern[str]]] | None = None) -> str | None:
    """`patterns` None loads .claude/project.json now (never a default); main() always passes them."""
    if patterns is None:
        patterns = _project_patterns()
    for index, (rule, pattern) in enumerate(patterns):
        for m in pattern.finditer(text):
            name = m.group(1) if m.groups() and m.group(1) else m.group(0)
            if rule == "secret.dotenv" and name.lower() == ".env.example":
                continue
            if index == 1 and not re.search(r"[?\[]", name):
                continue  # the glob/class pattern only counts with a glob character in it
            return rule
    for segment in re.split(r"[\n;&|]", text):
        m = COMPOSE.search(segment)
        if m and COMPOSE_VERB.search(segment, m.end()):
            return "secret.compose-config"
    return None


def decide(command: str, patterns: list[tuple[str, re.Pattern[str]]] | None = None) -> str | None:
    """`patterns` None loads .claude/project.json now (never a default); main() always passes them."""
    if patterns is None:
        patterns = _project_patterns()
    stripped = strip_exempt_heredocs(command)
    views = (stripped, join_continuations(stripped)) if "\\\n" in stripped else (stripped,)
    for view in views:
        scanned = drop_text_option_values(view)
        rule = match_rule(scanned, patterns) or match_rule(normalise(scanned), patterns)
        if rule:
            return rule
    return None


# --- recursive readers that start at the repository root (issue #32 item 11) ---
# A search that reads every file under the root reaches .env files and local-only folders without naming them. By
# text only, these forms are refused when a start path is the root or a folder above it: grep with -r/-R/--recursive,
# rg with hidden files (--hidden, -., -uu, --unrestricted twice; by default rg skips hidden and ignored files), and
# find that runs a reading program on what it finds (-exec, -execdir, -ok, -okdir, or a later `| xargs` in the same
# pipeline), and `git grep --no-index` or `--untracked`. The working folder comes from the hook's payload and every
# literal `cd` before the search (Set-Location and sl under PowerShell); a cd joined by `&&` holds for its chain,
# while after `;`, `||`, a pipe or a subshell the earlier folder stays possible too, since the cd may not hold.
# Commands are split only at unquoted separators; wrappers (time, timeout, nice, ionice, env, sudo, command, exec,
# builtin, nohup, stdbuf, watch, winpty, busybox, xargs), shell keywords, grouping, `sh -c`, eval, `cmd /c`,
# `pwsh -Command` and `env -S` are looked through. An option the lists below do not know may take the
# next word as its value, so both readings are checked and either one reaching the root refuses the search. A path
# built at run time, a `cd` to one and other recursive readers (tar, cp -r, a script) stay a documented limit.
_ASSIGNMENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*=")
_SEARCHERS = re.compile(r"grep|rg|find|xargs|cd|sh\b|bash|zsh|eval|cmd|pwsh|powershell|env|set-location|sl\b|"
                        r"push-location|pushd", re.IGNORECASE)
_CD = {"cd", "chdir", "pushd", "set-location", "sl", "push-location"}  # the PowerShell names take -Path / -LiteralPath
_KEYWORDS = {"if", "then", "else", "elif", "while", "until", "do", "!", "{", "(", "nohup"}
# wrapper: the options that take a value, and how many plain words it takes before the command
_WRAPPERS = {"time": ({"-f", "--format", "-o", "--output"}, 0), "timeout": ({"-s", "--signal", "-k", "--kill-after"}, 1), "nice": ({"-n", "--adjustment"}, 0),
             "env": ({"-u", "--unset", "-C", "--chdir", "-S", "--split-string"}, 0), "sudo": ({"-u", "-g", "-h", "-p",
             "-C", "-D", "-r", "-t", "-U", "--user", "--group"}, 0), "command": (set(), 0), "exec": ({"-a"}, 0),
             "stdbuf": ({"-i", "-o", "-e"}, 0), "xargs": ({"-a", "-d", "-E", "-I", "-L", "-n", "-P", "-s",
             "--arg-file", "--delimiter", "--max-args", "--max-procs", "--max-chars", "--replace"}, 0),
             "builtin": (set(), 0), "winpty": (set(), 0), "busybox": (set(), 0),
             "ionice": ({"-c", "-n", "-p", "-P", "-u", "--class", "--classdata"}, 0),
             "watch": ({"-n", "--interval", "-d", "-q", "--equexit"}, 0)}
_SHELLS = {"sh", "bash", "zsh", "dash", "ksh"}
_FIND_RUNS = {"-exec", "-execdir", "-ok", "-okdir"}
# programs that run on found files without printing their content: a find or xargs that runs only these reads nothing
_NOT_READING = {"rm", "rmdir", "echo", "ls", "stat", "touch", "chmod", "chown", "wc", "du", "basename", "dirname",
                "realpath", "readlink", "true", "false", "printf"}
_GREP_FLAGS = {"--recursive", "--dereference-recursive", "--ignore-case", "--no-ignore-case", "--invert-match",
               "--word-regexp", "--line-regexp", "--count", "--files-with-matches", "--files-without-match",
               "--only-matching", "--quiet", "--silent", "--no-messages", "--byte-offset", "--line-number",
               "--with-filename", "--no-filename", "--initial-tab", "--null", "--null-data", "--text",
               "--extended-regexp", "--fixed-strings", "--basic-regexp", "--perl-regexp", "--line-buffered",
               "--binary", "--no-group-separator", "--help", "--version"}
_GREP_VALUES = {"--regexp", "--file", "--max-count", "--label", "--after-context", "--before-context", "--context",
                "--binary-files", "--devices", "--directories", "--exclude", "--exclude-from", "--exclude-dir",
                "--include", "--group-separator", "--color", "--colour"}
_GREP_SHORT_VALUES = set("efABCmdD")
_RG_VALUES = {"--regexp", "--file", "--after-context", "--before-context", "--context", "--color", "--colors",
              "--context-separator", "--dfa-size-limit", "--encoding", "--engine", "--field-context-separator",
              "--field-match-separator", "--glob", "--iglob", "--ignore-file", "--max-columns", "--max-count",
              "--max-depth", "--max-filesize", "--path-separator", "--pre", "--pre-glob", "--regex-size-limit",
              "--replace", "--sort", "--sortr", "--threads", "--type", "--type-add", "--type-clear", "--type-not",
              "--hostname-bin", "--hyperlink-format", "--generate", "--json-path"}
_RG_SHORT_VALUES = set("efABCEMTdgjmrt")
_RG_FLAGS = {"--hidden", "--no-hidden", "--no-ignore", "--no-ignore-vcs", "--no-ignore-parent", "--no-ignore-dot",
             "--no-ignore-global", "--no-ignore-exclude", "--no-ignore-files", "--no-ignore-messages", "--unrestricted",
             "--follow", "--no-follow", "--ignore-case", "--smart-case", "--case-sensitive", "--fixed-strings",
             "--word-regexp", "--line-regexp", "--count", "--count-matches", "--files", "--files-with-matches",
             "--files-without-match", "--invert-match", "--line-number", "--no-line-number", "--heading",
             "--no-heading", "--with-filename", "--no-filename", "--json", "--no-json", "--multiline",
             "--multiline-dotall", "--only-matching", "--pcre2", "--no-pcre2", "--quiet", "--stats", "--text",
             "--trim", "--vimgrep", "--null", "--null-data", "--search-zip", "--binary", "--no-messages",
             "--no-config", "--one-file-system", "--passthru", "--byte-offset", "--column", "--no-column", "--crlf",
             "--debug", "--trace", "--help", "--version", "--block-buffered", "--line-buffered", "--no-require-git",
             "--glob-case-insensitive", "--type-list", "--mmap", "--no-mmap", "--pretty", "--no-unicode",
             "--include-zero", "--max-columns-preview", "--sort-files", "--auto-hybrid-regex", "--no-pre",
             "--ignore-file-case-insensitive"}
_DRIVE_PATH = re.compile(r"^/(?:cygdrive/)?([A-Za-z])(?=/|$)")  # Git Bash and Cygwin: /c/x is C:/x


def _split_commands(command: str, powershell: bool) -> list[tuple[str, str]]:
    """(command text, the separator before it) for each command, split only at unquoted, unescaped `;`, `&`, `|`
    and line ends. The separator is "&&", "||", "|" (a pipe, `|&` too) or ";" (`;`, `&` and a line end)."""
    out, current, sep, quote, i = [], [], ";", "", 0
    escape = "`" if powershell else "\\"
    while i < len(command):
        ch = command[i]
        if quote:
            current.append(ch)
            if ch == escape and quote == '"' and i + 1 < len(command):
                current.append(command[i + 1])
                i += 1
            elif ch == quote:
                quote = ""
        elif ch == escape and i + 1 < len(command):
            current += [ch, command[i + 1]]
            i += 1
        elif ch in "'\"":
            quote = ch
            current.append(ch)
        elif ch in ";&|\n" and not (ch == "&" and (command[i - 1:i] == ">" or command[i + 1:i + 2] == ">")):
            out.append(("".join(current), sep))
            pair = command[i:i + 2]
            if pair in ("&&", "||"):
                current, sep = [], pair
                i += 1
            elif ch == "|":
                current, sep = [], "|"
                i += 1 if pair == "|&" else 0  # |& pipes stderr too
            else:
                current, sep = [], ";"
        else:
            current.append(ch)
        i += 1
    out.append(("".join(current), sep))
    return out


_BASH_WORD = re.compile(r"""(?:[^\s'"\\]+|\\.|'[^']*'?|"(?:[^"\\]|\\.)*"?)+""")
_PS_WORD = re.compile(r"""(?:[^\s'"`]+|`.|'(?:[^']|'')*'?|"(?:[^"`]|`.)*"?)+""")


def _words(segment: str, powershell: bool = False) -> list[str]:
    """The words of one command with quotes and escapes removed as its shell would (bash, or PowerShell, where a
    backslash is a plain character and a backtick escapes), in linear time; a comment ends the command."""
    words = []
    for m in (_PS_WORD if powershell else _BASH_WORD).finditer(segment):
        word = m.group(0)
        if word.startswith("#"):
            break
        if powershell:
            word = re.sub(r"'((?:[^']|'')*)'?|\"((?:[^\"`]|`.)*)\"?|`(.)",
                          lambda q: q.group(1).replace("''", "'") if q.group(1) is not None else
                          re.sub(r"`(.)", r"\1", q.group(2)) if q.group(2) is not None else q.group(3), word)
        else:
            word = re.sub(r"'([^']*)'?|\"((?:[^\"\\]|\\.)*)\"?|\\(.)",
                          lambda q: q.group(1) if q.group(1) is not None else
                          re.sub(r"\\(.)", r"\1", q.group(2)) if q.group(2) is not None else q.group(3), word)
        words.append(word)
    return words


def _ungrouped(word: str) -> str:
    """A word without the `)` or `}` that closes a group (`(grep -r x .)`), keeping a brace of its own (`{.,src}`)."""
    while word.endswith(")") and word.count(")") > word.count("("):
        word = word[:-1]
    while word.endswith("}") and word.count("}") > word.count("{"):
        word = word[:-1]
    return word


def _program(words: list[str]) -> tuple[str, list[str], list[tuple[str, bool]]]:
    """(program, its words, the command strings it runs: a shell's -c, eval, cmd /c, pwsh -Command, env -S, each
    with whether PowerShell reads it) after keywords, grouping, assignments and wrappers."""
    words = [w for w in words]
    for _ in range(16):
        while words and (words[0] in _KEYWORDS or _ASSIGNMENT.match(words[0])):
            words = words[1:]
        if words and words[0][:1] in "({!" and len(words[0]) > 1:
            words[0] = words[0].lstrip("({!")
            continue
        if not words:
            return "", [], []
        name = re.split(r"[/\\]", words[0])[-1].lower().removesuffix(".exe")
        rest = words[1:]
        if name in _SHELLS:
            scripts = [(b, False) for a, b in zip(rest, rest[1:]) if a == "-c" or (a.startswith("-") and a.endswith("c")
                                                                                  and not a.startswith("--"))]
            return name, rest, scripts
        if name == "eval":
            return name, rest, [(" ".join(rest), False)]
        if name == "cmd":
            after = next((i for i, w in enumerate(rest) if w.lower() in ("/c", "/k")), None)
            return name, rest, [(" ".join(rest[after + 1:]), False)] if after is not None else []
        if name in ("pwsh", "powershell"):
            after = next((i for i, w in enumerate(rest) if w.lower() in ("-c", "-command", "/c", "/command")), None)
            return name, rest, [(" ".join(rest[after + 1:]), True)] if after is not None else []
        if name == "env":
            split = next((b for a, b in zip(rest, rest[1:]) if a in ("-S", "--split-string")), None)
            if split is not None:
                return name, rest, [(split, False)]
        if name not in _WRAPPERS:
            return name, [_ungrouped(w) for w in rest], []
        values, plain_words = _WRAPPERS.get(name, (set(), 0))
        i = 0
        while i < len(rest) and rest[i].startswith("-"):
            i += 2 if rest[i] in values else 1
        while i < len(rest) and _ASSIGNMENT.match(rest[i]) and name == "env":
            i += 1
        words = rest[i + plain_words:]
    return "", [], []


def _long(word: str, known: set[str], abbreviate: bool) -> str:
    """A long option's full name: GNU tools take any unique prefix of one (grep --rec is --recursive)."""
    name = word.split("=", 1)[0]
    if name in known or not abbreviate:
        return name
    matches = [k for k in known if k.startswith(name)]
    return matches[0] if len(matches) == 1 else name


def _readings(words: list[str], flags: set[str], values: set[str], short_values: set[str],
              abbreviate: bool) -> tuple[list[list[str]], set[str], set[str]]:
    """(the positional words under each reading, short letters seen, long options seen). A long option that neither
    list knows may take the next word as its value: then there are two readings, without and with that."""
    known = flags | values
    positional_a, positional_b, letters, longs, i, skip_b = [], [], set(), set(), 0, False
    while i < len(words):
        word = words[i]
        if word == "--":
            positional_a += words[i + 1:]
            positional_b += words[i + 1:] if not skip_b else words[i + 2:]
            break
        if word.startswith("--") and len(word) > 2:
            name = _long(word, known, abbreviate)
            longs.add(name if "=" not in word else name + "=" + word.split("=", 1)[1])
            if "=" not in word and name in values:
                i += 2
                continue
            if "=" not in word and name not in flags:
                skip_b = True  # unknown: reading b takes the next word as its value
                i += 1
                continue
        elif word.startswith("-") and len(word) > 1 and word != "-":
            for j, letter in enumerate(word[1:]):
                letters.add(letter)
                if letter in short_values:
                    if j == len(word) - 2:
                        i += 1
                    break
        else:
            positional_a.append(word)
            if skip_b:
                skip_b = False
            else:
                positional_b.append(word)
        i += 1
    return [positional_a, positional_b], letters, longs


def _resolve(path: str, cwd: str) -> str | None:
    """A path as text, joined to the working folder: a Git Bash drive path read as one, `~` and `~+` expanded. None
    when the text cannot tell it (built at run time)."""
    if os.name == "nt" and path.startswith("/"):
        path = _DRIVE_PATH.sub(lambda m: m.group(1) + ":", path, count=1)
    if path == "~+" or path.startswith(("~+/", "~+\\")):
        path = cwd + path[2:]
    if not path or any(c in path for c in "$`") or path.startswith("~-"):
        return None
    return os.path.normcase(os.path.normpath(os.path.join(cwd, os.path.expanduser(path))))


def _reaches_root(path: str, cwd: str, root: str) -> bool:
    for spelled in _expand_braces(path):  # {.,src} is two paths
        if "{" in spelled:
            continue  # more braces than are expanded: not told by text
        trimmed = spelled.rstrip("/\\") or spelled  # `.*/` is the glob `.*`
        head, tail = os.path.split(trimmed)
        if any(c in tail for c in "*?["):
            if not tail.startswith("."):
                continue  # a glob the shell expands without dot files
            trimmed = head or "."  # `.*` and `.[!.]*` reach the dot files of their folder
        if any(c in trimmed for c in "*?["):
            continue
        resolved = _resolve(trimmed, cwd)
        if resolved is not None and (resolved == root or root.startswith(resolved.rstrip(os.sep) + os.sep)):
            return True
    return False


_GIT_GREP_FLAGS = _GREP_FLAGS | {"--no-index", "--untracked", "--no-exclude-standard", "--exclude-standard",
                                 "--cached", "--recurse-submodules", "--full-name", "--all-match"}


def _search_paths(program: str, rest: list[str]) -> list[list[str]] | None:
    """The start paths under each reading of a recursive reader, or None when the command reads nothing recursively
    with hidden files."""
    if program in ("grep", "egrep", "fgrep"):
        readings, letters, longs = _readings(rest, _GREP_FLAGS, _GREP_VALUES, _GREP_SHORT_VALUES, True)
        pairs = list(zip(rest, rest[1:]))
        recursive = bool(letters & {"r", "R"}) or bool(longs & {"--recursive", "--dereference-recursive",
                                                                "--directories=recurse"}) \
            or any(a in ("-d", "--directories") and b == "recurse" for a, b in pairs) \
            or any(_long(a, _GREP_VALUES, True) == "--directories" and b == "recurse" for a, b in pairs)
        given = "e" in letters or "f" in letters or bool(longs & {"--regexp", "--file"})
        return [r if given else r[1:] for r in readings] if recursive else None
    if program == "rg":
        readings, letters, longs = _readings(rest, _RG_FLAGS, _RG_VALUES, _RG_SHORT_VALUES, False)
        u = sum(w.count("u") for w in rest if w.startswith("-") and not w.startswith("--"))
        hidden = "." in letters or "--hidden" in longs or u + rest.count("--unrestricted") >= 2
        given = "e" in letters or "f" in letters or bool(longs & {"--regexp", "--file"})
        return [r if given else r[1:] for r in readings] if hidden else None
    if program == "git" and rest[:1] == ["grep"]:
        # git grep reads only tracked files, unless it is told to read the working tree's other files too
        args = rest[1:]
        if not any(w in ("--no-index", "--untracked") for w in args):
            return None
        readings, letters, longs = _readings(args, _GIT_GREP_FLAGS, _GREP_VALUES, _GREP_SHORT_VALUES, True)
        given = "e" in letters or "f" in letters or bool(longs & {"--regexp", "--file"})
        return [r if given else r[1:] for r in readings]
    return None


def _find_reads(rest: list[str]) -> bool:
    for i, word in enumerate(rest):
        if word in _FIND_RUNS and i + 1 < len(rest):
            if re.split(r"[/\\]", rest[i + 1])[-1].lower() not in _NOT_READING:
                return True
    return False


def _cd_target(rest: list[str], powershell: bool) -> str | None:
    """The folder a cd names: options (-P, -L, --, PowerShell's -Path) skipped; None for one the text cannot follow
    (home, back, built at run time)."""
    words = [w for w in rest if w != "--"]
    if powershell:
        words = [w for w in words if w.lower() not in ("-path", "-literalpath", "-passthru")]
    else:
        words = [w for w in words if w not in ("-P", "-L", "-e", "-@", "-LP", "-PL")]
    target = words[0] if words else ""
    if not target or target.startswith("-") or any(c in target for c in "$`*?[{"):
        return None
    return target


def root_search(command: str, cwd: object, root: Path, powershell: bool = False, depth: int = 0) -> bool:
    """Whether the command runs a recursive reader from the repository root or a folder above it. A `cd` is followed
    as a set of the folders the next command may run in: a cd joined by `&&` holds for its chain, and after any other
    separator (it may have failed, or run in a subshell or a pipe) the earlier folders stay possible too."""
    if not isinstance(cwd, str) or not os.path.isabs(cwd) or depth > 3:
        return False
    # the cheap filter first, on the text with every line continuation and PowerShell escape taken out (gr\<newline>ep
    # is grep), so a long command that names no searcher costs one pass
    probe = command.replace("`", "") if powershell else command.replace("\\\n", "")
    if not _SEARCHERS.search(normalise(probe)):
        return False  # names no searcher, no cd and no shell: nothing to follow
    if not powershell and "\\\n" in command:
        command = join_continuations(command)
    top = os.path.normcase(os.path.normpath(str(root)))
    possible = {os.path.normcase(os.path.normpath(cwd))}  # every folder a later command may run in
    chain = set(possible)  # the folders of the current && chain
    commands = _split_commands(command, powershell)
    words_of = [_words(text, powershell) for text, _ in commands]
    # later[i]: a later command of i's pipeline is `xargs <a reading program>`, worked out once from the end
    reading_xargs = []
    for words in words_of:
        while words and (words[0] in _KEYWORDS or _ASSIGNMENT.match(words[0])):
            words = words[1:]
        is_xargs = bool(words) and re.split(r"[/\\]", words[0])[-1].lower().removesuffix(".exe") == "xargs"
        inner = _program(words)[0] if is_xargs else ""  # the program xargs runs; none means echo
        reading_xargs.append(bool(inner) and inner not in _NOT_READING)
    later = [False] * len(commands)
    for i in range(len(commands) - 2, -1, -1):
        later[i] = commands[i + 1][1] == "|" and (reading_xargs[i + 1] or later[i + 1])
    for index, words in enumerate(words_of):
        if commands[index][1] != "&&":
            chain = set(possible)
        program, rest, scripts = _program(words)
        if any(root_search(script, here, root, ps, depth + 1) for script, ps in scripts for here in chain):
            return True
        if program in _CD:
            target = _cd_target(rest, powershell)
            if target is None:
                return False  # a cd the text cannot follow: the rest is not checked
            moved = {_resolve(target, here) for here in chain}
            if None in moved or any(len(m) > 4096 for m in moved):
                return False  # longer than any real path: a crafted chain of cds is not followed further
            chain = moved
            possible |= moved
            if len(possible) > 16:
                return False
            continue
        if program == "find":
            starts = []
            for word in rest:
                if word.startswith(("-", "(", "!")):
                    break
                starts.append(word)
            if (_find_reads(rest) or later[index]) and any(_reaches_root(p, here, top) for p in (starts or ["."])
                                                          for here in chain):
                return True
            continue
        readings = _search_paths(program, rest)
        if readings is not None and any(_reaches_root(p, here, top) for paths in readings for p in (paths or ["."])
                                        for here in chain):
            return True
    return False


ROOT_SEARCH_REASON = ("Blocked: a recursive search from the repository root, or above it, reads .env files and "
                      "local-only folders without naming them (CLAUDE.md security principles). Search a named "
                      "subfolder instead.")

# Reference folders (ADR 0012): read freely, and every other command that concerns one asks the owner; a subagent is
# denied. A reader passes only as the bare first word of every command, in a form with nothing the shell expands.
REFERENCE_READERS = frozenset({"cat", "head", "tail", "ls", "wc", "grep", "egrep", "fgrep", "diff", "sha256sum"})
REFERENCE_PS_READERS = frozenset({"get-content", "get-childitem", "select-string", "get-item", "test-path",
                                  "get-filehash", "gc", "type", "cat", "gci", "dir", "ls", "sls", "gi"})
# outside single quotes: no variable, escape, substitution, grouping, script block, brace list, output redirect or
# comment (`#`, `<#`: the splitter reads quotes inside a comment as quotes); and no line end anywhere, since a quote in
# a comment or a heredoc body would hide the next line from the splitter while the shell runs it
_REFERENCE_FORBIDDEN = frozenset("$`(){}\\>#")
_REFERENCE_PS_FORBIDDEN = frozenset("$`(){}@>#")
# the modes where a hook's ask is known to prompt (Claude Code's documentation names these two); any other is denied
REFERENCE_ASK_MODES = frozenset({"default", "auto"})
REFERENCE_RULE = "secret.reference-folder"
REFERENCE_REASON = ("This command concerns a reference folder (secret_guard.reference_folders): plain reads pass, "
                    "anything else asks the owner (ADR 0012).")
REFERENCE_DENY_REASON = ("Blocked: this command concerns a reference folder (secret_guard.reference_folders). A "
                         "subagent never changes one, and only plain reads pass (ADR 0012); read it with the Read "
                         "tool, or report what you need.")
REFERENCE_MODE_REASON = ("Blocked: this command concerns a reference folder (secret_guard.reference_folders), and "
                         "in this permission mode the guard cannot ask the owner, so it refuses (ADR 0012). Switch "
                         "to the default or auto mode, or read the folder with plain commands.")


def _names_reference(command: str, names: list[str], powershell: bool) -> bool:
    """Whether the text names a reference folder as a path part, in any case, also through quotes or escapes that
    the shell removes, a wildcard spelling or an 8.3 short name."""
    def edge(ch: str) -> bool:  # the name ends here: no letter, digit, `_`, `.` or `-` continues it
        return not ch or not (ch.isalnum() or ch in ("_", ".", "-"))

    def ends(low: str, end: int) -> bool:  # also `.local.ref. -Recurse` or `.local.ref. '`: Windows drops those
        if edge(low[end:end + 1]):
            return True
        j = end
        while j < len(low) and low[j] == ".":
            j += 1
        if j > end and edge(low[j:j + 1]):
            return True  # a run of dots, then anything that ends a word
        while j < len(low) and low[j] in ". ":
            j += 1
        return j > end and low[j:j + 1] in ("", "/", "\\", "'", '"')

    lowered = [n.lower() for n in names]
    texts = [command, normalise(command)] + ([command.replace("`", "")] if powershell else [])
    for text in texts:
        low = text.lower()
        for name in lowered:
            for m in re.finditer(re.escape(name), low):
                if edge(low[m.start() - 1] if m.start() else "") and ends(low, m.end()):
                    return True
    return next(_OtherSpellings(names).finditer(command), None) is not None


def _cwd_in_reference(cwd: object, names: list[str]) -> bool:
    """Whether the call's working folder is a reference folder or lies inside one, as the boundaries hook places a
    file (lib.in_reference_folder: any case, short names, links, the trailing dots and spaces Windows drops)."""
    if not isinstance(cwd, str) or not cwd:
        return False
    return lib.in_reference_folder(cwd, names) is not None


def _git_cleans(command: str, powershell: bool, depth: int = 0) -> bool:
    """Whether a git command with `clean` or `stash` among its words runs (both can delete git-ignored files)."""
    for text, _ in _split_commands(command, powershell):
        program, rest, scripts = _program(_words(text, powershell))
        if program == "git" and any(w in ("clean", "stash") for w in rest):
            return True
        if depth < 3 and any(_git_cleans(script, ps, depth + 1) for script, ps in scripts):
            return True
    return False


def _ps_states(command: str) -> list[str]:
    """Per character, as PowerShell reads quotes: 's' single-quoted, 'd' double-quoted, 'c' code. A backtick is
    refused outside single quotes, so its escapes need no reading here."""
    states, mode = [], "c"
    for ch in command:
        if mode == "c":
            mode = "s" if ch == "'" else "d" if ch == '"' else "c"
            states.append(mode)
        else:
            states.append(mode)
            if ch == ("'" if mode == "s" else '"'):
                mode = "c"
    return states


def reference_reads_only(command: str, powershell: bool) -> bool:
    """Whether every command is a listed reader, named by its bare first word, with nothing outside single quotes
    that the shell would expand, run or redirect, and in the PowerShell tool only ASCII characters."""
    if len(command) > lib.MAX_COMMAND or "\n" in command or "\r" in command:
        return False
    if powershell:
        if "--%" in command:
            return False  # stop-parsing: PowerShell reads the rest of the line without quotes
        if not command.isascii():
            return False  # PowerShell also takes typographic quotes as quotes
        states, forbidden, readers = _ps_states(command), _REFERENCE_PS_FORBIDDEN, REFERENCE_PS_READERS
    else:
        states, forbidden, readers = scan(command), _REFERENCE_FORBIDDEN, REFERENCE_READERS
    if any(ch in forbidden and state != "s" for ch, state in zip(command, states)):
        return False
    for text, _ in _split_commands(command, powershell):
        words = _words(text, powershell)
        if words and (words[0].lower() if powershell else words[0]) not in readers:
            return False
    return True


def reference_refused(command: str, project: dict, cwd: object, powershell: bool) -> bool:
    """Whether a command concerns a reference folder (names it, runs inside it, or cleans or stashes with git) and
    is not a plain read: the guard then asks the owner, or denies a subagent. git is also looked for line by line,
    since a quote in a comment or a heredoc body would hide a later line from the splitter."""
    names = project["secret_guard"].get("reference_folders", [])
    if not names:
        return False
    if not (_names_reference(command, names, powershell) or _cwd_in_reference(cwd, names)
            or any(_git_cleans(view, powershell) for view in line_views(command, powershell))):
        return False
    return not reference_reads_only(command, powershell)


def line_views(command: str, powershell: bool) -> list[str]:
    """The whole command and, for one of several lines, each line of it and each line of a copy with every line
    continuation joined (a backslash, or PowerShell's backtick, before the line end). A quote inside a comment or a
    heredoc body would otherwise hide a later line from the splitter, which the shell still executes; the extra views
    can only add a decision."""
    if "\n" not in command and "\r" not in command:
        return [command]
    joined = re.sub(r"`\r?\n" if powershell else r"\\\r?\n", "", command)
    return [command, *command.splitlines(), *joined.splitlines()]


def main() -> int:
    payload = lib.read_payload()
    if payload is None:
        return 0
    agent = payload.get("agent_type") or ""
    listed, _ = lib.listed_agents()
    closed = lib.is_listed(agent, listed)
    # the facts, the patterns and the reason are built in one handler that denies every
    # session; nothing below runs without them, so a broken file never reaches the main session's
    # fail-open path.
    try:
        project = lib.load_project()
        patterns = patterns_for(project)
        reason = blocked_reason(project)
    except Exception as exc:  # noqa: BLE001
        kind = exc.kind if isinstance(exc, lib.ProjectFactsError) else "error"
        text = lib.project_reason(HOOK, agent or "main session", exc)
        print(f"{HOOK}: .claude/project.json unusable ({kind}); denying", file=sys.stderr)
        if closed:
            lib.deny_listed(HOOK, payload, f"config.project-{kind}", text, decision="deny-error")
        else:
            lib.emit("deny", text)
            lib.audit(HOOK, payload, "deny-error", f"config.project-{kind}")
        return 0
    try:
        command = (payload.get("tool_input") or {}).get("command")
        if not isinstance(command, str):
            raise ValueError("missing command")
        if closed and len(command) > lib.MAX_COMMAND:
            lib.deny_listed(HOOK, payload, "input.too-long", f"{agent}: command too long to check.")
            return 0
        rule = decide(command, patterns)
        if not rule and payload.get("tool_name") == "PowerShell":  # `s inside a name is just s to PowerShell
            rule = decide(command.replace("`", ""), patterns)
        powershell = payload.get("tool_name") == "PowerShell"
        if not rule and root_search_lines(command, payload.get("cwd"), lib.ROOT, powershell):
            rule, reason = "secret.root-search", ROOT_SEARCH_REASON
    except Exception as exc:  # noqa: BLE001
        if closed:
            print(f"{HOOK}: {type(exc).__name__} for {agent}; denying", file=sys.stderr)
            lib.deny_listed(HOOK, payload, f"error.{type(exc).__name__}", lib.fail_closed_reason(HOOK, agent, exc),
                            decision="deny-error")
            return 0
        print(f"{HOOK}: {type(exc).__name__}; allowing (main session)", file=sys.stderr)
        allow_error = f"error.{type(exc).__name__}"  # recorded below unless the reference check decides the call
        rule = None
    else:
        allow_error = None
    if rule:
        if closed:  # a secret-guard deny freezes a listed agent's run too
            lib.deny_listed(HOOK, payload, rule, reason)
        else:
            lib.emit("deny", reason)
            lib.audit(HOOK, payload, "deny", rule)
        return 0
    if project["secret_guard"].get("reference_folders") and reference_decision(payload, project, agent, closed):
        return 0
    if allow_error:
        lib.audit(HOOK, payload, "allow-error", allow_error)
    return 0


def root_search_lines(command: str, cwd: object, root: Path, powershell: bool) -> bool:
    """root_search on each of the command's line_views: the whole command and, for one of several lines, each line
    with and without its continuations joined. A line of a multi-line quoted text that is itself a recursive search
    from the root is refused too: write such a text from a file (`-F`, `--body-file`)."""
    return any(root_search(view, cwd, root, powershell) for view in line_views(command, powershell))


def reference_decision(payload: dict, project: dict, agent: str, closed: bool) -> bool:
    """ADR 0012, in its own handler: an error refuses the plain pass and never undoes a deny made before. True when
    it decided the call (an ask, or a deny)."""
    command = (payload.get("tool_input") or {}).get("command")
    try:
        refused = isinstance(command, str) and reference_refused(
            command, project, payload.get("cwd"), payload.get("tool_name") == "PowerShell")
    except Exception as exc:  # noqa: BLE001
        print(f"{HOOK}: reference check {type(exc).__name__}; refusing the plain pass", file=sys.stderr)
        refused = True
    if not refused:
        return False
    mode = payload.get("permission_mode")
    if closed:
        lib.deny_listed(HOOK, payload, REFERENCE_RULE, REFERENCE_DENY_REASON)
    elif agent:  # any other subagent: an ask is not known to reach the owner from there
        lib.emit("deny", REFERENCE_DENY_REASON)
        lib.audit(HOOK, payload, "deny", REFERENCE_RULE)
    elif "permission_mode" not in payload or (isinstance(mode, str) and mode in REFERENCE_ASK_MODES):
        lib.emit("ask", REFERENCE_REASON)
        lib.audit(HOOK, payload, "ask", REFERENCE_RULE)
    else:
        lib.emit("deny", REFERENCE_MODE_REASON)
        lib.audit(HOOK, payload, "deny", REFERENCE_RULE)
    return True


if __name__ == "__main__":
    sys.exit(main())
