#!/usr/bin/env python3
"""Dependency-gate canary (issue #28, docs/architecture/ci-hardening.md D2).

Proves that the dependency gates still fail on a known-vulnerable package. It passes ONLY when the
gate fails as expected. Stdlib only. Nothing vulnerable is ever committed: the seeded projects are
generated here, at run time, and removed again. Package code is never built, executed or run.

  python .github/scripts/dependency_canary.py nuget   # restore of Newtonsoft.Json 12.0.3 must fail with NU1903/NU1904
  python .github/scripts/dependency_canary.py npm     # audit of lodash 4.17.20 must exit non-zero and list lodash

Exit codes: 0 the gate is closed (canary passes), 1 the gate is open or the proof was not made,
2 usage or environment error.

--weaken-for-evidence (local only, nuget only) adds -p:TreatWarningsAsErrors=false to the restore, so
the audit becomes a warning. The canary must then fail with "gate is open". The script refuses the
option when GITHUB_ACTIONS=true, and ci.yml never uses it.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CANARY_DIR = ROOT / "artifacts" / "dependency-canary"

NUGET_PACKAGE, NUGET_VERSION = "Newtonsoft.Json", "12.0.3"   # GHSA-5crp-9r3c-p9vr, high
NPM_PACKAGE, NPM_VERSION = "lodash", "4.17.20"               # GHSA-35jh-r3h4-6jhm, high
# The same string as the real audit step in ci.yml (a .claude test compares them).
NPM_AUDIT_LEVEL = "--audit-level=high"

# Real output: "error NU1903: Warning As Error: Package 'Newtonsoft.Json' 12.0.3 has a known high severity ..."
NU190X_RE = re.compile(r"error (NU190[1-4]): (?:Warning As Error: )?Package '([^']+)'")


def _rmtree(path: Path) -> None:
    def onerror(func, p, _exc):
        os.chmod(p, stat.S_IWRITE)
        func(p)
    if path.exists():
        shutil.rmtree(path, onerror=onerror)


def _show(title: str, text: str) -> None:
    print(f"--- {title} ---")
    print(text.rstrip() if text else "(no output)")
    print("--- end ---")


def _env() -> dict[str, str]:
    env = dict(os.environ)
    env.update({"DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "NPM_CONFIG_UPDATE_NOTIFIER": "false", "NPM_CONFIG_FUND": "false"})
    return env


def _run(argv: list[str], cwd: Path) -> subprocess.CompletedProcess:
    return subprocess.run(argv, cwd=cwd, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", env=_env(), timeout=900)


def judge_nuget(returncode: int, output: str) -> tuple[bool, str]:
    """Pure verdict on a restore of the seeded project. (True, reason) means the gate is closed."""
    if returncode == 0:
        return False, "FAIL: restore succeeded, so the NuGet audit gate is open"
    hits = NU190X_RE.findall(output)
    if not hits:
        return False, "FAIL: restore failed, but not with an NU1901-NU1904 audit error: the proof was not made"
    others = sorted({name for _, name in hits if name != NUGET_PACKAGE})
    if others:
        return False, f"FAIL: an audit error names a package other than {NUGET_PACKAGE}: {others}"
    if not any(code in ("NU1903", "NU1904") for code, _ in hits):
        return False, "FAIL: the audit errors are below High severity (NU1901/NU1902 only); the seeded package must be High or Critical"
    return True, f"OK: restore failed with {sorted({c for c, _ in hits})} naming {NUGET_PACKAGE}"


def judge_npm(returncode: int, stdout: str) -> tuple[bool, str]:
    """Pure verdict on ONE 'npm audit --audit-level=high --json' call (exit code and JSON together)."""
    if returncode == 0:
        return False, "FAIL: npm audit exited 0, so the npm audit gate is open"
    try:
        doc = json.loads(stdout)
        sev = doc["vulnerabilities"][NPM_PACKAGE]["severity"]
    except (ValueError, KeyError, TypeError):
        return False, f"FAIL: npm audit failed, but its JSON does not list {NPM_PACKAGE}: the proof was not made"
    if sev not in ("high", "critical"):
        return False, f"FAIL: {NPM_PACKAGE} is listed at '{sev}', expected high or critical"
    return True, f"OK: npm audit exited {returncode} and lists {NPM_PACKAGE} at {sev}"


def canary_nuget(weaken: bool) -> int:
    dotnet = shutil.which("dotnet")
    if not dotnet:
        print("dotnet is not on PATH")
        return 2
    _rmtree(CANARY_DIR)  # a stale directory from a killed run (S-03)
    try:
        CANARY_DIR.mkdir(parents=True)
        # Imports the real central props so every real Directory.Build.* policy applies, and adds one
        # version. The root Directory.Packages.props is never touched.
        (CANARY_DIR / "Directory.Packages.props").write_text(
            '<Project>\n'
            '  <Import Project="$([MSBuild]::GetPathOfFileAbove(\'Directory.Packages.props\', \'$(MSBuildThisFileDirectory)../\'))" />\n'
            '  <ItemGroup>\n'
            f'    <PackageVersion Include="{NUGET_PACKAGE}" Version="{NUGET_VERSION}" />\n'
            '  </ItemGroup>\n'
            '</Project>\n', encoding="utf-8")
        (CANARY_DIR / "Canary.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            '  <PropertyGroup>\n'
            '    <EnableDefaultItems>false</EnableDefaultItems>\n'
            '  </PropertyGroup>\n'
            '  <ItemGroup>\n'
            f'    <PackageReference Include="{NUGET_PACKAGE}" />\n'
            '  </ItemGroup>\n'
            '</Project>\n', encoding="utf-8")
        argv = [dotnet, "restore", "Canary.csproj",
                "--packages", str(CANARY_DIR / "packages"),
                f"-p:ArtifactsPath={CANARY_DIR / 'out'}"]  # S-03: every output lands inside the folder we delete
        if weaken:
            argv.append("-p:TreatWarningsAsErrors=false")
        # Restore only. Never build, test or run the seeded project.
        proc = _run(argv, CANARY_DIR)
        output = proc.stdout + proc.stderr
        _show(f"dotnet restore exit {proc.returncode}", output)
        ok, reason = judge_nuget(proc.returncode, output)
        print(reason)
        return 0 if ok else 1
    finally:
        _rmtree(CANARY_DIR)


def canary_npm() -> int:
    npm = shutil.which("npm")
    if not npm:
        print("npm is not on PATH")
        return 2
    work = Path(tempfile.mkdtemp(prefix="decisya-npm-canary-"))
    try:
        if ROOT in work.resolve().parents:
            print("refusing to run inside the repository")
            return 2
        (work / "package.json").write_text(json.dumps(
            {"name": "decisya-dependency-canary", "version": "0.0.0", "private": True,
             "dependencies": {NPM_PACKAGE: NPM_VERSION}}), encoding="utf-8")
        # Resolves metadata only: no tarball download, no lifecycle script.
        lock = _run([npm, "install", "--package-lock-only", "--ignore-scripts", "--no-audit", "--no-fund"], work)
        if lock.returncode != 0:
            _show(f"npm install --package-lock-only exit {lock.returncode}", lock.stdout + lock.stderr)
            print("FAIL: could not resolve the seeded package: the proof was not made")
            return 1
        # ONE call gives both the exit code and the JSON (S-02).
        audit = _run([npm, "audit", NPM_AUDIT_LEVEL, "--json"], work)
        _show(f"npm audit {NPM_AUDIT_LEVEL} --json exit {audit.returncode}", audit.stdout)
        ok, reason = judge_npm(audit.returncode, audit.stdout)
        print(reason)
        return 0 if ok else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Prove the dependency gates fail on a seeded vulnerable package.")
    parser.add_argument("target", choices=["nuget", "npm"])
    parser.add_argument("--weaken-for-evidence", action="store_true",
                        help="local only, nuget only: restore with warnings as warnings, to show the canary turning red")
    args = parser.parse_args(argv)
    if args.weaken_for_evidence:
        if os.environ.get("GITHUB_ACTIONS", "").lower() == "true":
            print("--weaken-for-evidence is refused when GITHUB_ACTIONS=true")
            return 2
        if args.target != "nuget":
            print("--weaken-for-evidence applies to the nuget target only")
            return 2
    return canary_nuget(args.weaken_for_evidence) if args.target == "nuget" else canary_npm()


if __name__ == "__main__":
    sys.exit(main())
