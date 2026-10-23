#!/usr/bin/env python3
"""SPA package guard: no install-time scripts in src/Decisya.Web (issue #26 M1, moved here by issue #123).

Stdlib only. Reads files and never runs a package manager, so it is safe to run before any install.
Used by the `build-test` and `e2e` jobs of ci.yml, so the guard and its trigger cannot drift apart.

Checks:
- .npmrc sets ignore-scripts=true;
- package.json defines none of the lifecycle scripts below;
- package.json does not set allowScripts.

Usage: spa_package_guard.py [directory]    (default: src/Decisya.Web next to the repository root)
Exit codes: 0 pass, 1 a rule is broken, 2 the files cannot be read.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DIR = ROOT / "src" / "Decisya.Web"
LIFECYCLE_SCRIPTS = ("preinstall", "install", "postinstall", "prepublish", "preprepare", "prepare", "postprepare")
IGNORE_SCRIPTS_RE = re.compile(r"^ignore-scripts[ \t]*=[ \t]*true[ \t]*$", re.MULTILINE)


def check(directory: Path) -> list[str]:
    """Return the broken rules (empty list = pass). Raises OSError or ValueError when a file cannot be read."""
    problems: list[str] = []
    npmrc = (directory / ".npmrc").read_text(encoding="utf-8")
    if not IGNORE_SCRIPTS_RE.search(npmrc):
        problems.append("src/Decisya.Web/.npmrc must set ignore-scripts=true")
    package = json.loads((directory / "package.json").read_text(encoding="utf-8"))
    if not isinstance(package, dict):
        raise ValueError("package.json is not a JSON object")
    scripts = package.get("scripts")
    if isinstance(scripts, dict):
        bad = [name for name in LIFECYCLE_SCRIPTS if name in scripts]
        if bad:
            problems.append("lifecycle scripts in package.json: " + ", ".join(bad))
    if "allowScripts" in package:
        problems.append("package.json must not set allowScripts")
    return problems


def main(argv: list[str]) -> int:
    directory = Path(argv[1]) if len(argv) > 1 else DEFAULT_DIR
    try:
        problems = check(directory)
    except (OSError, ValueError) as exc:
        print(f"::error::SPA package guard could not read the package files ({type(exc).__name__})")
        return 2
    for problem in problems:
        print(f"::error::{problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
