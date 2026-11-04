#!/usr/bin/env python3
"""The delivery label check for CI (ADR 0007): a pull request carries exactly one kind: and one phase: label.

Usage (a CI step on the pull request's opened, synchronize, reopened, labeled and unlabeled events):
  python .claude/scripts/labels.py check --event "$GITHUB_EVENT_PATH"

It reads only the event file the runner writes and .claude/project.json; it needs no token and calls nothing. A
project adopts the label set with a `labels` section in project.json; without one, the check passes and says so.
With it, the pull request must carry exactly one `kind:<kind>` label (feature, fix, rework, tooling, docs) and
exactly one `phase:<id>` label (`phase:1`, or `phase:0.4` for a release milestone), and no other label unless
`labels.extra` lists it. A dependency bot's pull request serves no issue and is exempt: by the same rule as
`gates.py bot`, its branch is under the first matching entry of gates.dependency_bot_branches, it was opened by that
entry's login, and a run that changes the branch (opened, synchronize, reopened) was started by that login too; a
label change, made by whoever changes the label, keeps the exemption.

Exit 0 = the labels pass, or the set is not adopted, or the pull request is a bot's; 1 = a label is missing,
repeated or not allowed (each named); 2 = usage, an unreadable event file, or .claude/project.json cannot be used.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import argparse
import importlib.util
import json
from pathlib import Path

SELF = Path(__file__).resolve()
ROOT = SELF.parents[2]
MAX_EVENT_BYTES = 4 * 1024 * 1024


def _load(name: str, path: Path):
    """A sibling module from its source file, never from bytecode in __pycache__; one module object per file."""
    module = sys.modules.get(name)
    if module is not None and Path(getattr(module, "__file__", "") or "").resolve() == path:
        return module
    module = importlib.util.module_from_spec(importlib.util.spec_from_file_location(name, path))
    sys.modules[name] = module
    exec(compile(path.read_bytes(), str(path), "exec"), module.__dict__)  # noqa: S102
    return module


claudecfg = _load("_claudecfg", SELF.parent / "_claudecfg.py")


def safe(text: object) -> str:
    """A value from the event, as printable ASCII on one line, never longer than 80 characters."""
    out = "".join(c if " " <= c <= "~" else f"\\x{ord(c):02x}" if ord(c) <= 0xFF else f"\\u{ord(c):04x}"
                  for c in str(text))
    return out if len(out) <= 80 else out[:77] + "..."


def problems(event: dict, project: dict) -> tuple[str, list[str]]:
    """('passed' | 'not-adopted' | 'bot' | 'failed', the problems found)."""
    if "labels" not in project:
        return "not-adopted", []
    pr = event.get("pull_request")
    if not isinstance(pr, dict):
        return "failed", ["the event holds no pull_request (run the check on pull request events only)"]
    head = pr.get("head") if isinstance(pr.get("head"), dict) else {}
    user = pr.get("user") if isinstance(pr.get("user"), dict) else {}
    branch, login = head.get("ref"), user.get("login")
    sender = event.get("sender", {}).get("login") if isinstance(event.get("sender"), dict) else None
    # the same rule as gates.py bot: the first entry whose prefix the branch is under, its login as the author, and
    # its login as the actor of a run that changes the branch (a label change is made by whoever changes the label)
    entries = [b for b in project["gates"]["dependency_bot_branches"] if isinstance(branch, str) and
               branch.startswith(b["branch"])]
    if entries and login == entries[0]["login"] and (event.get("action") in ("labeled", "unlabeled")
                                                     or sender == entries[0]["login"]):
        return "bot", []
    raw = pr.get("labels", [])
    if not isinstance(raw, list) or not all(isinstance(label, dict) for label in raw):
        return "failed", ["the event's labels are not a list of label objects"]
    names = [label.get("name") for label in raw]
    if not all(isinstance(n, str) for n in names):
        return "failed", ["a label in the event has no name"]
    kinds = [n for n in names if n.startswith("kind:")]
    phases = [n for n in names if n.startswith("phase:")]
    extra = set(project["labels"]["extra"])
    found = []
    if len(kinds) != 1:
        found.append(f"needs exactly one kind: label (kind:{'|kind:'.join(claudecfg.LABEL_KINDS)}), has {len(kinds)}")
    found += [f"label {safe(n)} is not a kind of the set" for n in kinds if n[len("kind:"):] not in claudecfg.LABEL_KINDS]
    if len(phases) != 1:
        found.append(f"needs exactly one phase: label (phase:<n> or phase:<x.y>), has {len(phases)}")
    found += [f"label {safe(n)} is not phase:<n> or phase:<x.y>" for n in phases if not claudecfg.PHASE_LABEL.fullmatch(n)]
    found += [f"label {safe(n)} is outside the set and not in labels.extra" for n in names
              if not n.startswith(("kind:", "phase:")) and n not in extra]
    return ("failed" if found else "passed"), found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="cmd", required=True)
    check = sub.add_parser("check")
    check.add_argument("--event", required=True)
    args = parser.parse_args(argv)
    try:
        project = claudecfg.load_project(ROOT / ".claude" / "project.json")
    except claudecfg.ProjectConfigError as exc:
        print(f"labels: {exc}", file=sys.stderr)
        return 2
    try:
        path = Path(args.event)
        if path.stat().st_size > MAX_EVENT_BYTES:
            raise ValueError("too large")
        event = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(event, dict):
            raise ValueError("not an object")
    except (OSError, ValueError, RecursionError):
        print("labels: the event file cannot be read as a JSON object", file=sys.stderr)
        return 2
    state, found = problems(event, project)
    if state == "not-adopted":
        print("labels: the label set is not adopted (no labels section in .claude/project.json); nothing checked")
        return 0
    if state == "bot":
        print("labels: a dependency bot's pull request serves no issue; exempt")
        return 0
    if state == "passed":
        print("labels: ok")
        return 0
    for problem in found:
        print(f"labels: {problem}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
