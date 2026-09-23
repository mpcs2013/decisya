#!/usr/bin/env python3
"""Generate the base files of a Decisya module from ../assets.

Usage:  python .claude/skills/module-scaffold/scripts/scaffold.py <Name> <schema>
Writes (never overwrites):
  src/Modules/<Name>/Decisya.Modules.<Name>.Contracts/Decisya.Modules.<Name>.Contracts.csproj
  src/Modules/<Name>/Decisya.Modules.<Name>/Decisya.Modules.<Name>.csproj
  src/Modules/<Name>/Decisya.Modules.<Name>/<Name>Module.cs
  tests/Modules/Decisya.Modules.<Name>.Tests/Decisya.Modules.<Name>.Tests.csproj
  tests/Modules/Decisya.Modules.<Name>.Tests/<Name>ModuleBoundaryTests.cs
Run from the repository root.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ASSETS = Path(__file__).resolve().parents[1] / "assets"
ROOT = Path(__file__).resolve().parents[4]


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    name, schema = argv
    if not re.fullmatch(r"[A-Z][A-Za-z0-9]*", name):
        print(f"Name must be PascalCase, e.g. Tenancy (got '{name}')", file=sys.stderr)
        return 2
    if not re.fullmatch(r"[a-z][a-z0-9_]*", schema):
        print(f"schema must be lowercase, e.g. tenancy (got '{schema}')", file=sys.stderr)
        return 2

    src = f"src/Modules/{name}"
    tests = f"tests/Modules/Decisya.Modules.{name}.Tests"
    files = {
        f"{src}/Decisya.Modules.{name}.Contracts/Decisya.Modules.{name}.Contracts.csproj": "Contracts.csproj",
        f"{src}/Decisya.Modules.{name}/Decisya.Modules.{name}.csproj": "Module.csproj",
        f"{src}/Decisya.Modules.{name}/{name}Module.cs": "Module.cs",
        f"{tests}/Decisya.Modules.{name}.Tests.csproj": "Tests.csproj",
        f"{tests}/{name}ModuleBoundaryTests.cs": "ModuleBoundaryTests.cs",
    }

    existing = [p for p in files if (ROOT / p).exists()]
    if existing:
        print("Refusing to overwrite existing files:\n  " + "\n  ".join(existing), file=sys.stderr)
        return 1

    for target, asset in files.items():
        text = (ASSETS / asset).read_text(encoding="utf-8")
        text = text.replace("__Name__", name).replace("__schema__", schema)
        path = ROOT / target
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")
        print(f"created {target}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
