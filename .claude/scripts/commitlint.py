#!/usr/bin/env python3
"""Local mirror of CI's commit-message check (issue #39, item 10; G4-39-48 to 56).

CI runs wagoid/commitlint-github-action v6.2.1, which (with no commitlint config in this repo)
uses commitlint 19.2.1 with @commitlint/config-conventional 19.1.0. This script applies the same
rules and prints the same messages, so a message CI would reject is rejected at commit time.
Behaviour is pinned by golden outputs from the real commitlint in .claude/tests/fixtures/commitlint.

Usage:
  python .claude/scripts/commitlint.py <message-file>           (pre-commit commit-msg hook)
  python .claude/scripts/commitlint.py --range <rev>..<rev>     (every commit in a range)
Exit 1 on any error (CI fails on errors), 0 otherwise; warnings are printed but do not fail.
Standard library only; the message is never evaluated, only read.
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

TYPES = ["build", "chore", "ci", "docs", "feat", "fix", "perf", "refactor", "revert", "style", "test"]
HELP = "ⓘ   Get help: https://github.com/conventional-changelog/commitlint/#what-is-commitlint"
ERROR, WARN, INPUT = "✖", "⚠", "⧗"
SCISSORS = "# ------------------------ >8 ------------------------"

HEADER_RE = re.compile(r"^(\w*)(?:\((.*)\))?!?: (.*)$")
# conventional-changelog-conventionalcommits: notes and references start the footer.
FOOTER_START_RE = re.compile(
    r"^(BREAKING CHANGE|BREAKING-CHANGE)[:\s]"
    r"|^(close|closes|closed|fix|fixes|fixed|resolve|resolves|resolved)\s+[\w./-]*#\d+",
    re.IGNORECASE,
)
# @commitlint/is-ignored defaults.
IGNORED_RES = [
    re.compile(r"^((Merge pull request)|(Merge (.*?) into (.*?)|(Merge branch (.*?)))(?:\r?\n)*$)", re.MULTILINE),
    re.compile(r"^(Merge tag (.*?))(?:\r?\n)*$", re.MULTILINE),
    re.compile(r"^(R|r)evert (.*)"),
    re.compile(r"^(amend|fixup|squash)!"),
    re.compile(r"^(Merged (.*?)(in|into) (.*)|Merged PR (.*): (.*))"),
    re.compile(r"^Merge remote-tracking branch(\s*)(.*)"),
    re.compile(r"^Automatic merge(.*)"),
    re.compile(r"^Auto-merged (.*?) into (.*)"),
    re.compile(r"^v?\d+\.\d+\.\d+(-[\w.]+)?(\+[\w.]+)?\s*$"),
]


def js_length(text: str) -> int:
    """Length in UTF-16 code units, as JavaScript's String.length counts it."""
    return len(text.encode("utf-16-le")) // 2


def git_strip(raw: str) -> str:
    """git's default `strip` cleanup: drop comment lines and everything from the scissors line."""
    kept = []
    for line in raw.replace("\r\n", "\n").split("\n"):
        if line == SCISSORS:
            break
        if line.startswith("#"):
            continue
        kept.append(line)
    return "\n".join(kept).rstrip()


def is_ignored(message: str) -> bool:
    return any(r.search(message) for r in IGNORED_RES)


def _words(text: str) -> list[str]:
    return re.findall(r"[A-Z]{2,}(?=[A-Z][a-z]|\d|\W|_|$)|[A-Z]?[a-z]+|[A-Z]+|\d+", text)


def _to_case(text: str, case: str) -> str:
    if case == "sentence-case":
        first = text.split(" ")[0]
        return first[:1].upper() + first[1:].lower() + text[len(first):]
    if case == "start-case":
        return " ".join(w[:1].upper() + w[1:] for w in _words(text))
    if case == "pascal-case":
        return "".join(w[:1].upper() + w[1:].lower() for w in _words(text))
    if case == "upper-case":
        return text.upper()
    raise ValueError(case)


def subject_has_case(subject: str, case: str) -> bool:
    """@commitlint/ensure `case`: quoted and backtick spans are removed first (they may hold
    proper names). A result that is empty or does not start with a letter (digit, emoji, symbol)
    matches no case; the golden fixtures subject-digit and header-non-bmp pin this."""
    text = re.sub(r"[`\"'](.*?)[`\"']", "", subject).strip()
    if text == "" or not text[0].isalpha():
        return False
    return _to_case(text, case) == text


def parse(message: str) -> tuple[str, str, str, list[str]]:
    lines = message.split("\n")
    header, rest = lines[0], lines[1:]
    footer_at = next((i for i, line in enumerate(rest) if FOOTER_START_RE.match(line)), None)
    body_lines = rest if footer_at is None else rest[:footer_at]
    footer_lines = [] if footer_at is None else rest[footer_at:]
    body = "\n".join(body_lines).strip("\n")
    footer = "\n".join(footer_lines).strip("\n")
    return header, body, footer, rest


def lint(message: str) -> tuple[list[tuple[str, str]], list[tuple[str, str]]]:
    header, body, footer, rest = parse(message)
    m = HEADER_RE.match(header)
    ctype, subject = (m.group(1), m.group(3)) if m else ("", "")
    errors: list[tuple[str, str]] = []
    warnings: list[tuple[str, str]] = []

    if body and rest and rest[0].strip():
        warnings.append(("body-leading-blank", "body must have leading blank line"))
    if any(js_length(line) > 100 for line in body.split("\n")) if body else False:
        errors.append(("body-max-line-length", "body's lines must not be longer than 100 characters"))
    if footer:
        footer_at = next(i for i, line in enumerate(rest) if FOOTER_START_RE.match(line))
        if footer_at > 0 and rest[footer_at - 1].strip():
            warnings.append(("footer-leading-blank", "footer must have leading blank line"))
        if any(js_length(line) > 100 for line in footer.split("\n")):
            errors.append(("footer-max-line-length", "footer's lines must not be longer than 100 characters"))
    if js_length(header) > 100:
        errors.append(("header-max-length", f"header must not be longer than 100 characters, current length is {js_length(header)}"))
    if header != header.lstrip():
        errors.append(("header-trim", "header must not start with whitespace"))
    elif header != header.rstrip():
        errors.append(("header-trim", "header must not end with whitespace"))
    if subject:
        cases = ["sentence-case", "start-case", "pascal-case", "upper-case"]
        if any(subject_has_case(subject, c) for c in cases):
            errors.append(("subject-case", "subject must not be sentence-case, start-case, pascal-case, upper-case"))
    else:
        errors.append(("subject-empty", "subject may not be empty"))
    if subject.endswith("."):
        errors.append(("subject-full-stop", "subject may not end with full stop"))
    if ctype:
        if ctype != ctype.lower():
            errors.append(("type-case", "type must be lower-case"))
        if ctype not in TYPES:
            errors.append(("type-enum", f"type must be one of [{', '.join(TYPES)}]"))
    else:
        errors.append(("type-empty", "type may not be empty"))
    return errors, warnings


def report(message: str) -> tuple[str, int]:
    """Returns commitlint's formatted output and the exit code for one message."""
    message = git_strip(message)
    if not message.strip() or is_ignored(message):
        return "", 0
    errors, warnings = lint(message)
    if not errors and not warnings:
        return "", 0
    header, body, footer, _ = parse(message)
    shown = "\n\n".join(part for part in (header, body, footer) if part)
    out = [f"{INPUT}   input: {shown}"]
    out += [f"{ERROR}   {msg} [{rule}]" for rule, msg in errors]
    out += [f"{WARN}   {msg} [{rule}]" for rule, msg in warnings]
    sign = ERROR if errors else WARN
    out += ["", f"{sign}   found {len(errors)} problems, {len(warnings)} warnings", HELP, "", ""]
    return "\n".join(out), 1 if errors else 0


def main(argv: list[str]) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if len(argv) == 2 and argv[0] == "--range":
        revs = subprocess.run(["git", "rev-list", "--reverse", argv[1]], capture_output=True, text=True, check=False)
        if revs.returncode != 0:
            print(f"commitlint: git rev-list failed for {argv[1]}", file=sys.stderr)
            return 2
        worst = 0
        for rev in revs.stdout.split():
            msg = subprocess.run(["git", "show", "-s", "--format=%B", rev], capture_output=True, text=True,
                                 encoding="utf-8", check=False).stdout
            out, code = report(msg)
            if out:
                sys.stdout.write(f"commit {rev[:7]}\n{out}")
            worst = max(worst, code)
        return worst
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    out, code = report(Path(argv[0]).read_text(encoding="utf-8-sig", errors="replace"))
    sys.stdout.write(out)
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
