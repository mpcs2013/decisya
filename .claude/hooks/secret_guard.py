#!/usr/bin/env python3
"""PreToolUse hook (Bash): block commands that would read .env files or user-secrets.

Permission deny rules on Read do not cover shell commands such as `cat .env`, so this hook covers
the obvious forms for every session and agent. It is a guardrail, not a security boundary
(CLAUDE.md); the sandbox's "no secrets mounted" is the boundary (ADR-0010).

How it decides (issue #39 item 9, G4-39-35 to 47):
1. A small quote-state scanner marks each character as unquoted code, single-quoted,
   double-quoted or comment. Unquoted backslash-newline continuations are joined first.
2. A heredoc body is data (not scanned) only when a `git commit` or `gh` command owns it: the
   `<<` is in unquoted code with no comment before it, it is the only `<<` on the line, nothing
   follows the delimiter, the command starts the line or follows only `cd <path>` / `git add
   <paths>` joined by `;`, `&&` or `||`, and no unquoted `;&|<>`, `$(` or backtick precedes it.
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
import _hooklib as lib  # noqa: E402

HOOK = "secret_guard"
REASON = ("Blocked: this command touches a .env file or user-secrets (CLAUDE.md security "
          "principles). Ask Marco for the value's name, never its content.")

_LEAD = r"(?:^|(?<=[\s/\\'\"=<>`{(,]))"
_TRAIL_CHARS = r"\s'\";|&)>`,}\]:.~+"
_CLASS = r"(?:\?|\[[^\]\n]{1,8}\])"
PATTERNS = [
    ("secret.dotenv", re.compile(_LEAD + r"(\.env(?:\.[\w.-]*)?)(?=$|[" + _TRAIL_CHARS + r"])", re.IGNORECASE)),
    ("secret.dotenv", re.compile(_LEAD + r"\.(?:e|" + _CLASS + r")(?:n|" + _CLASS + r")(?:v|" + _CLASS + r")", re.IGNORECASE)),
    ("secret.dotenv", re.compile(_LEAD + r"\.en?\*|\*\.?env\b|\.e['\"]+n|\.en['\"]+v", re.IGNORECASE)),
    ("secret.user-secrets", re.compile(r"UserSecrets|user-secrets\b|secrets\.(?:j|\?|\*|\[)", re.IGNORECASE)),
    ("secret.compose-config", re.compile(r"docker[- ]compose\b[^\n;&|]*\b(?:config|convert)\b", re.IGNORECASE)),
]

EXEMPT_OWNER = re.compile(r"^\s*(?:git(?:\s+-c\s+\S+)*\s+commit\b|gh\b)")
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


def scan(cmd: str) -> list[str]:
    """Per character: 'c' unquoted code, 's' single-quoted, 'd' double-quoted, '#' comment."""
    states, mode, i = [], "c", 0
    while i < len(cmd):
        ch = cmd[i]
        if mode == "c":
            if ch == "\\" and i + 1 < len(cmd):
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
            if ch == "\\" and i + 1 < len(cmd):
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
    return states


def join_continuations(cmd: str) -> str:
    states = scan(cmd)
    out, i = [], 0
    while i < len(cmd):
        if cmd[i] == "\\" and i + 1 < len(cmd) and cmd[i + 1] == "\n" and states[i] == "c":
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


def _exempt_delimiter(line: str) -> str | None:
    """The heredoc delimiter if this line opens an exempt heredoc (G4-39-36), else None."""
    if line.count("<<") != 1:
        return None
    states = scan(line)
    pos = line.index("<<")
    if line[pos:pos + 3] == "<<<" or states[pos] != "c" or "#" in states[:pos]:
        return None
    m = re.match(r"<<-?\s*(['\"]?)(\w+)\1\s*$", line[pos:])
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
    return m.group(2)


def strip_exempt_heredocs(cmd: str) -> str:
    lines, out, i = cmd.split("\n"), [], 0
    while i < len(lines):
        out.append(lines[i])
        delimiter = _exempt_delimiter(lines[i])
        i += 1
        if delimiter:
            while i < len(lines) and lines[i].strip() != delimiter:
                i += 1  # message body: data, not scanned
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
    return None


def decide(command: str) -> str | None:
    scanned = drop_text_option_values(strip_exempt_heredocs(join_continuations(command)))
    return match_rule(scanned) or match_rule(normalise(scanned))


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
