#!/usr/bin/env python3
"""Public-by-default scan: find literals that must not appear in shared files.

Usage:  python .claude/scripts/literals.py [--project .claude/project.json] <file or folder> ...
Exit 0 = nothing found; exit 1 = findings (each printed as path:line: category); exit 2 = input that cannot be used.

Files that Norma publishes are copied into other repositories, so they must not carry one project's facts.
Generic patterns, which name no project:
  ipv4        a dotted-quad address
  ipv6        an address in the full or the :: form
  email       an email address
  machine     a user's home or profile folder on Windows, macOS or Linux, or a network share path
  foreign-id  another repository's gate or decision id (a letter-digit-number form such as a gate id, or an ADR
              number), an issue reference in parentheses, or a repository-qualified issue reference
  url         a link that is not on the short generic list (example domains, the commit-message tool's page and the
              secret scanner's repository)
Probes, read at run time from the project file given with --project, so the scan never names a project: the values
that identify that project (blocked folders, `cd` targets, pack solution names, the names in gates.approvers),
compared without regard to case. A probe found in a shared file is a finding. Messages print the
category and position, never the matching text; a file whose own name matches a pattern is printed as a placeholder.
Every file under a folder is scanned (any suffix, symbolic links skipped, binary files skipped by a NUL sniff);
only the .git folder is skipped. This is a tripwire for review, not proof: text that is encoded or split is not found.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import argparse
import json
import posixpath
import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit

MAX_BYTES = 2_000_000
SNIFF = 8000
GENERIC_DOMAINS = ("example.com", "example.org", "example.net", "example.invalid")
GENERIC_URL_PREFIXES = (("github.com", "/conventional-changelog/"), ("github.com", "/gitleaks/gitleaks"))
PATTERNS = {
    "ipv4": re.compile(r"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])"),
    "ipv6": re.compile(r"(?<![\w:])(?:(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4})?"
                       r"|(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4})(?![\w:])"),
    "email": re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}"),
    "machine": re.compile(r"(?:[a-z]:[\\/]+users[\\/]+|/users/|/home/)[a-z0-9._-]+|\\\\[a-z0-9.-]+\\[a-z0-9$._-]+",
                          re.IGNORECASE),
    "foreign-id": re.compile(r"\bG\d-\d+-\d+|\bADR-\d{4}|\(#\d+[ ,;)]|\b[\w.-]+/[\w.-]+#\d+\b", re.IGNORECASE),
}
URL = re.compile(r"(?:https?|ssh|sftp|ftp|git)://[^\s\"'<>)\]]+|\bwww\.[A-Za-z0-9.-]+\.[A-Za-z]{2,}[^\s\"'<>)\]]*",
                 re.IGNORECASE)


def _generic_domain(host: str) -> bool:
    host = host.lower()
    return any(host == d or host.endswith("." + d) for d in GENERIC_DOMAINS)


def _allowed_url(url: str) -> bool:
    if not re.match(r"[a-z]+://", url, re.IGNORECASE):
        url = "https://" + url
    parts = urlsplit(url)
    host = (parts.hostname or "").lower()
    if parts.scheme.lower() not in ("http", "https"):
        return False
    if _generic_domain(host):
        return True
    path = posixpath.normpath(unquote(parts.path or "/"))
    if ".." in unquote(parts.path or "").split("/"):
        return False
    # a prefix is a whole path segment: "/gitleaks/gitleaks" does not allow "/gitleaks/gitleaks-other"
    return any(host == h and (path == prefix.rstrip("/") or path.startswith(prefix.rstrip("/") + "/"))
               for h, prefix in GENERIC_URL_PREFIXES)


def probes_from(project: object) -> list[str]:
    """Values that identify the project, from its project.json; short values are ignored to avoid noise."""
    if not isinstance(project, dict):
        return []
    found: list[str] = []

    def section(name):
        value = project.get(name)
        return value if isinstance(value, dict) else {}

    found += [v for v in section("secret_guard").get("blocked_folders", []) if isinstance(v, str)]
    found += [v for v in section("agent_boundaries").get("cd_targets", []) if isinstance(v, str)]
    found += [v for v in section("gates").get("approvers", []) if isinstance(v, str)]
    for config in section("packs").values():
        if isinstance(config, dict):
            solution = config.get("solution")
            if isinstance(solution, str):
                found += [solution, solution.rsplit(".", 1)[0]]
            # locked_restore.env_name is not a probe: it names a switch, usually a CI's own public variable (such as
            # the one the core's gates.py reads), not a fact that identifies the project
    return sorted({v for v in found if len(v) >= 4})


def scan_text(text: str, probes: list[str]) -> list[tuple[int, str]]:
    findings: list[tuple[int, str]] = []
    lowered = [p.lower() for p in probes]
    for number, line in enumerate(text.splitlines(), start=1):
        for category, pattern in PATTERNS.items():
            for match in pattern.finditer(line):
                if category == "email" and _generic_domain(match.group(0).rsplit("@", 1)[1]):
                    continue
                findings.append((number, category))
                break
        for match in URL.finditer(line):
            if not _allowed_url(match.group(0)):
                findings.append((number, "url"))
        low = line.lower()
        for probe in lowered:
            if probe in low:
                findings.append((number, "probe"))
    return findings


def files_under(target: Path):
    if target.is_symlink():
        return
    if target.is_file():
        yield target, target.name
    elif target.is_dir():
        for path in sorted(target.rglob("*")):
            relative = path.relative_to(target)
            if path.is_symlink() or not path.is_file() or ".git" in relative.parts:
                continue
            yield path, relative.as_posix()


def scan(targets: list[Path], probes: list[str], skip: set[Path] = frozenset()) -> list[str]:
    out: list[str] = []
    for target in targets:
        for path, shown in files_under(target):
            if path.resolve() in skip:
                continue
            if path.stat().st_size > MAX_BYTES:
                raise ValueError(f"a file is larger than {MAX_BYTES} bytes")
            data = path.read_bytes()
            if b"\0" in data[:SNIFF]:
                continue  # binary
            try:
                text = data.decode("utf-8-sig")
            except UnicodeDecodeError:
                raise ValueError("a file is not UTF-8 text and not binary") from None
            if scan_text(shown, probes):
                shown = "<a file whose name matches a pattern>"
            for number, category in scan_text(text, probes):
                out.append(f"{shown}:{number}: {category}")
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--project", default=None, help="a project.json whose values are used as probes")
    parser.add_argument("targets", nargs="+", type=Path)
    args = parser.parse_args(argv)
    probes: list[str] = []
    try:
        if args.project:
            project = json.loads(Path(args.project).read_text(encoding="utf-8-sig"))
            if not isinstance(project, dict):
                raise ValueError("the project file must be a JSON object")
            probes = probes_from(project)
        findings = scan(args.targets, probes)
    except (OSError, ValueError, RecursionError) as exc:
        print(f"literals: {exc if isinstance(exc, ValueError) and not isinstance(exc, json.JSONDecodeError) else 'an input cannot be used'}",
              file=sys.stderr)
        return 2
    for line in findings:
        print(line)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
