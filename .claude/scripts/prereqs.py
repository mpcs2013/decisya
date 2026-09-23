#!/usr/bin/env python3
"""Check that the platform pieces a skill depends on exist before it runs.

Usage:  python .claude/scripts/prereqs.py <skill> [--phase <name>]
Exit 0 when every prerequisite is present; exit 1 and list each missing one with
the GitHub issue expected to provide it. Run from the repository root.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# kind "path": the path exists. kind "type": a C# type declaration matching the
# regex exists somewhere under src/ or tests/.
CHECKS: dict[str, dict[str, list[tuple[str, str, str, str]]]] = {
    "module-scaffold": {
        # Phase "tenancy": DbContext, architecture registration, isolation test.
        "tenancy": [
            ("type", r"\b(record\s+struct|struct|record|class)\s+TenantId\b", "TenantId", "#32 (0.04b)"),
            ("type", r"\binterface\s+ITenantScoped\b", "ITenantScoped", "#22 (0.10)"),
            ("type", r"\bclass\s+TenantDbContext\b", "TenantDbContext", "#22 (0.10)"),
            ("type", r"\bclass\s+PostgresFixture\b", "PostgresFixture (Decisya.TestInfrastructure)", "#22 (0.10)"),
            ("path", "tests/Decisya.ArchitectureTests/ModuleBoundaryTests.cs", "Decisya.ArchitectureTests", "#22 (0.10)"),
        ],
    },
    "ef-migration": {
        "default": [
            ("path", "dotnet-tools.json", "local dotnet-ef tool manifest", "#35"),
            ("path", "src/Decisya.Api/Decisya.Api.csproj", "Decisya.Api (migrations startup project)", "#20 (0.08)"),
            ("type", r"\bclass\s+TenantDbContext\b", "TenantDbContext", "#22 (0.10)"),
            ("type", r"\bclass\s+PostgresFixture\b", "PostgresFixture (Decisya.TestInfrastructure)", "#22 (0.10)"),
        ],
    },
    "api-contract": {
        "default": [
            ("path", "src/Decisya.Api/Decisya.Api.csproj", "Decisya.Api", "#20 (0.08)"),
            ("path", "src/Decisya.Web/package.json", "Decisya.Web SPA", "#26 (0.14)"),
            ("path", "tests/Decisya.ContractTests", "Decisya.ContractTests", "first issue that adds an endpoint"),
        ],
    },
    "otel-instrumentation": {
        "default": [
            ("type", r"\bclass\s+SensitiveAttribute\b", "[Sensitive] attribute", "#15 (0.03)"),
            ("type", r"\bclass\s+SensitiveDataMaskingProcessor\b", "SensitiveDataMaskingProcessor", "#15 (0.03)"),
        ],
    },
}


def _type_exists(pattern: str) -> bool:
    regex = re.compile(pattern)
    for base in ("src", "tests"):
        for path in (ROOT / base).rglob("*.cs"):
            if any(part in ("bin", "obj") for part in path.parts):
                continue
            if regex.search(path.read_text(encoding="utf-8", errors="ignore")):
                return True
    return False


def main(argv: list[str]) -> int:
    if not argv or argv[0] not in CHECKS:
        print(f"usage: prereqs.py <{'|'.join(CHECKS)}> [--phase <name>]", file=sys.stderr)
        return 2
    skill = argv[0]
    phases = CHECKS[skill]
    phase = argv[argv.index("--phase") + 1] if "--phase" in argv else next(iter(phases))
    if phase not in phases:
        print(f"unknown phase '{phase}' for {skill}; known: {', '.join(phases)}", file=sys.stderr)
        return 2

    missing = []
    for kind, target, label, issue in phases[phase]:
        present = (ROOT / target).exists() if kind == "path" else _type_exists(target)
        print(f"  {'ok     ' if present else 'MISSING'}  {label}" + ("" if present else f"  -> expected from {issue}"))
        if not present:
            missing.append(label)

    if missing:
        print(f"{skill} ({phase}): {len(missing)} prerequisite(s) missing; stop here and do not work around them.")
        return 1
    print(f"{skill} ({phase}): all prerequisites present.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
