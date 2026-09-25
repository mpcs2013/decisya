#!/usr/bin/env python3
"""PreToolUse hook (Bash): block commands that would read .env files or user-secrets.

Permission deny rules on Read do not cover shell commands such as `cat .env`, so this hook covers
the obvious forms for every session and agent. It is a guardrail, not a security boundary
(CLAUDE.md); the sandbox's "no secrets mounted" is the boundary (ADR-0010).

How it decides (issue #39 item 9, G4-39-35 to 47):
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

Accepted residuals (out of scope; the sandbox boundary covers them): names built at run time
($(printf ...), variables, chr(), base64), recursive readers that never name the file
(grep -r X ., find -exec cat), scripts the agent writes and runs, indirect disclosure through
process or container environments, listing the user-secrets folder via an environment variable.
Known false positives (the main session or Marco runs them): ls/test -f/git check-ignore/echo of
the names outside the exempt text options.

For a project agent listed in .claude/boundaries.json, any error denies the call (fail closed);
the main session fails open. Denies are written to the local audit log (.agent-logs/hooks.jsonl)
without any command text.
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
    print(f"secret_guard: cannot load _hooklib ({type(_exc).__name__})", file=sys.stderr)
    if _agent:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                                 "permissionDecisionReason": f"secret_guard error (fail closed for "
                                                 f"{_agent}): {type(_exc).__name__} loading _hooklib."}}))
    sys.exit(0)

HOOK = "secret_guard"
REASON = ("Blocked: this command touches a .env file or user-secrets (CLAUDE.md security "
          "principles). Ask Marco for the value's name, never its content.")

_LEAD = r"(?:^|(?<=[\s/\\'\"=<>`{(,@:])|(?<=\s-[A-Za-z]))"
_TRAIL_CHARS = r"\s'\";|&)>`,}\]:.~+*?["
_CLASS = r"(?:\?|\[[^\]\n]{1,8}\])"
PATTERNS = [
    ("secret.dotenv", re.compile(_LEAD + r"(\.env(?:\.[\w.-]*)?)(?=$|[" + _TRAIL_CHARS + r"])", re.IGNORECASE)),
    ("secret.dotenv", re.compile(_LEAD + r"\.(?:e|" + _CLASS + r")(?:n|" + _CLASS + r")(?:v|" + _CLASS + r")", re.IGNORECASE)),
    ("secret.dotenv", re.compile(_LEAD + r"\.en?\*|\*\.?env\b|\.e['\"]+n|\.en['\"]+v", re.IGNORECASE)),
    ("secret.user-secrets", re.compile(r"UserSecrets|user-secrets\b|secrets\.(?:j|\?|\*|\[)", re.IGNORECASE)),
]
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
# token as a value. Used to read clusters such as `-rf <file>` (G6-39-04).
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
    that is itself escaped (two backslashes, then a newline) does not continue (G6-39-03)."""
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
    """(delimiter, strip leading tabs) if this line opens an exempt heredoc (G4-39-36), else None.
    Only a quoted delimiter qualifies: with `<<EOF` bash runs `$(...)` in the body (G6-39-02)."""
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
    opens an exempt heredoc (G6-39-01). After any non-exempt heredoc the rest is kept as is."""
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
        # a heredoc on a line that starts or ends inside a quote is never exempt (G6-39-15)
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
    """G4-39-43/44: remove text-option values of a single simple command; else unchanged.
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
        return cmd  # G4-39-44: a file-valued option means nothing in this command is exempt
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


def match_rule(text: str) -> str | None:
    for index, (rule, pattern) in enumerate(PATTERNS):
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


def decide(command: str) -> str | None:
    stripped = strip_exempt_heredocs(command)
    views = (stripped, join_continuations(stripped)) if "\\\n" in stripped else (stripped,)
    for view in views:
        scanned = drop_text_option_values(view)
        rule = match_rule(scanned) or match_rule(normalise(scanned))
        if rule:
            return rule
    return None


def main() -> int:
    payload = lib.read_payload()
    if payload is None:
        return 0
    agent = payload.get("agent_type") or ""
    listed, _ = lib.listed_agents()
    closed = lib.is_listed(agent, listed)
    try:
        command = (payload.get("tool_input") or {}).get("command")
        if not isinstance(command, str):
            raise ValueError("missing command")
        if closed and len(command) > lib.MAX_COMMAND:
            lib.emit("deny", f"{agent}: command too long to check.")
            lib.audit(HOOK, payload, "deny", "input.too-long")
            return 0
        rule = decide(command)
    except Exception as exc:  # noqa: BLE001
        if not closed:
            print(f"{HOOK}: {type(exc).__name__}; allowing (main session)", file=sys.stderr)
            lib.audit(HOOK, payload, "allow-error", f"error.{type(exc).__name__}")
            return 0
        print(f"{HOOK}: {type(exc).__name__} for {agent}; denying", file=sys.stderr)
        lib.emit("deny", lib.fail_closed_reason(HOOK, agent, exc))
        lib.audit(HOOK, payload, "deny-error", f"error.{type(exc).__name__}")
        return 0
    if rule:
        lib.emit("deny", REASON)
        lib.audit(HOOK, payload, "deny", rule)
    return 0


if __name__ == "__main__":
    sys.exit(main())
