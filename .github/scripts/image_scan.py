#!/usr/bin/env python3
"""Container image CVE scan for the five pinned vendor images (ADR-0015, issue #28; Caddy and the
OpenTelemetry collector joined in issue #120, ADR-0015 section 2 as amended by ADR-0018).

Stdlib only. Targets come from src/Decisya.AppHost/ContainerImages.cs and nowhere else. The scanner
is the digest-pinned Grype image named by the single FROM line of .github/image-scan/Dockerfile. It
runs as a container with no Docker socket, no token and no mount except the vulnerability database.

Exit codes: 0 pass, 1 findings or a scan that cannot be trusted, 2 configuration or schema error.

Everything derived from scanner output, the images or the exceptions file is printed between
::stop-commands:: markers with control characters escaped (T-14, H-10, T28-10), and the step summary
escapes markup. Nothing is written to $GITHUB_ENV or $GITHUB_OUTPUT.
"""
from __future__ import annotations

import json
import os
import re
import secrets
import subprocess
import sys
import tempfile
import unicodedata
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTAINER_IMAGES = ROOT / "src" / "Decisya.AppHost" / "ContainerImages.cs"
SCANNER_DOCKERFILE = ROOT / ".github" / "image-scan" / "Dockerfile"
EXCEPTIONS_FILE = ROOT / ".github" / "image-scan" / "exceptions.json"

# Each alias is the lower-cased stem of the four constants <Stem>Registry, <Stem>Image, <Stem>Tag and
# <Stem>Sha256 in ContainerImages.cs (CaddyRegistry... gives `caddy`, OtelCollectorRegistry... gives
# `otelcollector`). Still fail-closed: ContainerImages.cs must hold exactly this set (issue #120).
ALIASES = ("postgres", "keycloak", "redis", "caddy", "otelcollector")
# Exceptions may also name the release images (#119, ADR-0017), which release_images.py scans with evaluate().
EXCEPTION_IMAGES = ALIASES + ("api", "bff", "migrator")
PLATFORM = "linux/amd64"
MAX_EXCEPTION_DAYS = 90
ELEVATE_CVSS = 7.0

REF_RE = re.compile(r"^[a-z0-9.-]+/[a-z0-9._/-]+:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$")
CONST_RE = re.compile(r'public\s+const\s+string\s+(\w+?)(Registry|Image|Tag|Sha256)\s*=\s*"([^"]*)"\s*;')
FROM_RE = re.compile(r"^FROM\s+(\S+)\s+AS\s+grype\s*$", re.MULTILINE)
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
ID_RE = re.compile(r"^(CVE-\d{4}-\d{4,}|GHSA(-[23456789cfghjmpqrvwx]{4}){3})$")
ISSUE_RE = re.compile(r"^#\d+$")
PACKAGE_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:@/+-]*$")  # no glob, regex or whitespace characters
ENTRY_KEYS = {"id", "image", "package", "justification", "issue", "added", "expires"}

# The environment the scanner container receives: this exact list, nothing else (G4-28-05).
# GRYPE_DB_VALIDATE_AGE and GRYPE_DB_MAX_ALLOWED_BUILT_AGE are never set: the age check stays on.
DB_MOUNT = "/db"
DB_UPDATE_ENV = {"GRYPE_DB_CACHE_DIR": DB_MOUNT}
SCAN_ENV = {
    "GRYPE_DB_CACHE_DIR": DB_MOUNT,
    "GRYPE_DB_AUTO_UPDATE": "false",
    "GRYPE_CHECK_FOR_APP_UPDATE": "false",
}


# --------------------------------------------------------------------------- text hygiene

def neutralize(text: object) -> str:
    """Escape control and format characters so no scanner text can look like a workflow command."""
    s = str(text)
    out = []
    for ch in s:
        if unicodedata.category(ch).startswith("C"):
            out.append("\\x%02x" % ord(ch) if ord(ch) < 256 else "\\u%04x" % ord(ch))
        else:
            out.append(ch)
    return "".join(out)


def md_cell(text: object) -> str:
    """Make a value safe for a Markdown table cell: control characters escaped, markup as entities."""
    s = neutralize(text).replace("&", "&amp;")
    for raw, ent in (("<", "&lt;"), (">", "&gt;"), ("[", "&#91;"), ("]", "&#93;"),
                     ("\\", "&#92;"), ("|", "&#124;"), ("`", "&#96;")):
        s = s.replace(raw, ent)
    return s


def emit_protected(lines: list[str], out=None) -> None:
    """Print lines between stop-commands markers with a token that no scanner text can know."""
    out = out or sys.stdout
    token = secrets.token_hex(16)
    print(f"::stop-commands::{token}", file=out)
    for line in lines:
        print(neutralize(line), file=out)
    print(f"::{token}::", file=out)
    out.flush()


# --------------------------------------------------------------------------- inputs

def parse_targets(text: str) -> dict[str, str]:
    """alias -> reference, from ContainerImages.cs. Exactly the ALIASES, one value each."""
    parts: dict[str, dict[str, str]] = {}
    for name, kind, value in CONST_RE.findall(text):
        alias = name.lower()
        if kind in parts.setdefault(alias, {}):
            raise ValueError(f"ContainerImages.cs: duplicate constant {name}{kind}")
        parts[alias][kind] = value
    if set(parts) != set(ALIASES):
        raise ValueError(f"ContainerImages.cs: image aliases are {sorted(parts)}, expected {sorted(ALIASES)}")
    refs = {}
    for alias in ALIASES:
        p = parts[alias]
        if set(p) != {"Registry", "Image", "Tag", "Sha256"}:
            raise ValueError(f"ContainerImages.cs: {alias} needs Registry, Image, Tag and Sha256 constants")
        ref = f"{p['Registry']}/{p['Image']}:{p['Tag']}@sha256:{p['Sha256']}"
        if not REF_RE.match(ref):
            raise ValueError(f"ContainerImages.cs: {alias} reference is not a digest-pinned reference")
        refs[alias] = ref
    return refs


def parse_scanner_ref(text: str) -> str:
    """The single 'FROM <ref> AS grype' reference of .github/image-scan/Dockerfile."""
    found = FROM_RE.findall(text)
    if len(found) != 1:
        raise ValueError(f"image-scan/Dockerfile: expected exactly one 'FROM <ref> AS grype' line, found {len(found)}")
    if not REF_RE.match(found[0]):
        raise ValueError("image-scan/Dockerfile: the scanner reference is not a digest-pinned reference")
    return found[0]


def _utc_today() -> date:
    # A Python CI script: invariant 4's analyzer ban applies to .NET code.
    return datetime.now(timezone.utc).date()


def validate_exceptions(data: object, today: date | None = None) -> list[dict]:
    """Validate exceptions.json (ADR-0015 item 4, G4-28-04). Any error: print them and exit 2."""
    today = today or _utc_today()
    errors: list[str] = []
    entries: list[dict] = []
    if not isinstance(data, dict) or not isinstance(data.get("exceptions"), list):
        errors.append("top level must be an object with an 'exceptions' list")
    else:
        extra = set(data) - {"$comment", "exceptions"}
        if extra:
            errors.append(f"unknown top-level keys: {sorted(extra)}")
        if "$comment" in data and not isinstance(data["$comment"], str):
            errors.append("'$comment' must be a string")
        seen = set()
        for i, e in enumerate(data["exceptions"]):
            where = f"exceptions[{i}]"
            if not isinstance(e, dict):
                errors.append(f"{where}: must be an object")
                continue
            keys = set(e)
            if keys != ENTRY_KEYS:
                errors.append(f"{where}: keys must be exactly {sorted(ENTRY_KEYS)} (missing {sorted(ENTRY_KEYS - keys)}, unknown {sorted(keys - ENTRY_KEYS)})")
                continue
            if not all(isinstance(e[k], str) for k in ENTRY_KEYS):
                errors.append(f"{where}: every value must be a string")
                continue
            ok = True
            if not ID_RE.match(e["id"]):
                errors.append(f"{where}: id must be a CVE or GHSA id"); ok = False
            if e["image"] not in EXCEPTION_IMAGES:
                errors.append(f"{where}: image must be one of {list(EXCEPTION_IMAGES)}"); ok = False
            if not e["package"] or not PACKAGE_RE.match(e["package"]):
                errors.append(f"{where}: package must be a non-empty exact name without glob or regex characters"); ok = False
            if len(e["justification"].strip()) < 20:
                errors.append(f"{where}: justification must be at least 20 characters"); ok = False
            if not ISSUE_RE.match(e["issue"]):
                errors.append(f"{where}: issue must look like #123"); ok = False
            added = expires = None
            for key in ("added", "expires"):
                if not DATE_RE.match(e[key]):
                    errors.append(f"{where}: {key} must be YYYY-MM-DD"); ok = False
                    continue
                try:
                    d = date.fromisoformat(e[key])
                except ValueError:
                    errors.append(f"{where}: {key} is not a real date"); ok = False
                    continue
                if key == "added":
                    added = d
                else:
                    expires = d
            if added and added > today:
                errors.append(f"{where}: added is in the future"); ok = False
            if added and expires:
                if expires < added:
                    errors.append(f"{where}: expires is before added"); ok = False
                elif expires - added > timedelta(days=MAX_EXCEPTION_DAYS):
                    errors.append(f"{where}: expires is more than {MAX_EXCEPTION_DAYS} days after added"); ok = False
            ident = (e["image"], e["package"], e["id"])
            if ident in seen:
                errors.append(f"{where}: duplicates another entry (same image, package and id)"); ok = False
            seen.add(ident)
            if ok:
                entries.append({**e, "added_date": added, "expires_date": expires})
    if errors:
        emit_protected([f"image-scan: invalid exceptions.json: {m}" for m in errors])
        print("::error::image-scan: .github/image-scan/exceptions.json failed validation")
        sys.exit(2)
    return entries


# --------------------------------------------------------------------------- scanner invocation

def db_update_argv(scanner: str, db_dir: str) -> list[str]:
    argv = ["docker", "run", "--rm", "-v", f"{db_dir}:{DB_MOUNT}"]
    for k, v in DB_UPDATE_ENV.items():
        argv += ["-e", f"{k}={v}"]
    return argv + [scanner, "db", "update"]


def scan_argv(scanner: str, ref: str, db_dir: str) -> list[str]:
    argv = ["docker", "run", "--rm", "-v", f"{db_dir}:{DB_MOUNT}"]
    for k, v in SCAN_ENV.items():
        argv += ["-e", f"{k}={v}"]
    return argv + [scanner, f"registry:{ref}", "--platform", PLATFORM, "-o", "json"]


def _run(argv: list[str], log: list[str]) -> subprocess.CompletedProcess:
    # argv list, no shell, output captured so it only reaches the log inside the stop window (S-04).
    proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=1800)
    for stream in (proc.stderr,):
        for line in (stream or "").splitlines()[-200:]:
            log.append(f"  [docker stderr] {line}")
    return proc


# --------------------------------------------------------------------------- evaluation

def _db_built(doc: dict) -> object:
    db = (doc.get("descriptor") or {}).get("db")
    if not isinstance(db, dict):
        return None
    # Grype releases differ: descriptor.db.built, or descriptor.db.status.built.
    if db.get("built"):
        return db["built"]
    status = db.get("status")
    if isinstance(status, dict) and status.get("built"):
        return status["built"]
    return None


def check_provenance(doc: object, ref: str) -> list[str]:
    """Reasons to distrust a scan result (G4-28-05). Empty list means the scan proves it scanned ref."""
    problems: list[str] = []
    if not isinstance(doc, dict) or not isinstance(doc.get("matches"), list):
        return ["scanner JSON has no 'matches' list"]
    source = doc.get("source") if isinstance(doc.get("source"), dict) else {}
    target = source.get("target") if isinstance(source.get("target"), dict) else {}
    if source.get("type") != "image":
        problems.append("source.type is not 'image'")
    if target.get("userInput") not in (ref, f"registry:{ref}"):
        problems.append("source.target.userInput does not equal the requested reference with its digest")
    layers = target.get("layers")
    if not isinstance(layers, list) or not layers:
        problems.append("source.target.layers is empty: nothing was catalogued")
    distro = doc.get("distro") if isinstance(doc.get("distro"), dict) else {}
    if not distro.get("name"):
        problems.append("distro.name is empty: the image was not recognised as an OS image")
    if not _db_built(doc):
        problems.append("descriptor.db.built is missing: the vulnerability database is not identified")
    return problems


def _scores(*vulns: dict) -> list[float]:
    scores = []
    for v in vulns:
        for c in (v.get("cvss") or []):
            try:
                scores.append(float((c.get("metrics") or {}).get("baseScore")))
            except (TypeError, ValueError):
                pass
    return scores


def evaluate(doc: dict, alias: str, exceptions: list[dict], today: date) -> dict:
    """Apply the policy to one image's scanner JSON. Returns findings, excepted and used exception keys."""
    active = [e for e in exceptions if today <= e["expires_date"]]
    findings, excepted, used = [], [], set()
    for m in doc["matches"]:
        vuln = m.get("vulnerability") or {}
        related = m.get("relatedVulnerabilities") or []
        artifact = m.get("artifact") or {}
        sev = str(vuln.get("severity", "")).strip().lower()
        elevated = False
        if sev in ("high", "critical"):
            pass
        elif sev in ("unknown", "negligible", "") and any(s >= ELEVATE_CVSS for s in _scores(vuln, *related)):
            elevated = True  # S-05: a distro rating that hides a CVSS 7.0+ record counts as High
        else:
            continue
        ids = {vuln.get("id")} | {r.get("id") for r in related}
        ids.discard(None)
        fix = vuln.get("fix") or {}
        item = {
            "image": alias,
            "package": artifact.get("name", ""),
            "version": artifact.get("version", ""),
            "id": vuln.get("id", ""),
            "severity": ("Elevated(" + (sev or "unknown") + ")") if elevated else sev.capitalize(),
            "fix": ", ".join(fix.get("versions") or []) if fix.get("state") == "fixed" else (fix.get("state") or "unknown"),
        }
        hit = next((e for e in active if e["image"] == alias and e["package"] == item["package"] and e["id"] in ids), None)
        if hit and fix.get("state") == "fixed":
            # An upstream fix ends the exception at once, not at expiry (G6 D-2): bump the image.
            item["note"] = "exception not applied: a fix is now available, bump the image"
            hit = None
        if hit:
            used.add((hit["image"], hit["package"], hit["id"]))
            excepted.append(item)
        else:
            findings.append(item)
    return {"findings": findings, "excepted": excepted, "used": used}


# --------------------------------------------------------------------------- main

def _summary(rows: list[dict], notes: list[str]) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    lines = ["### Image CVE scan", ""]
    if rows:
        lines += ["| Image | Package | Version | Id | Severity | Fix | Status |", "| --- | --- | --- | --- | --- | --- | --- |"]
        for r in rows:
            lines.append("| " + " | ".join(md_cell(r[k]) for k in ("image", "package", "version", "id", "severity", "fix", "status")) + " |")
    else:
        lines.append("No High or Critical findings.")
    lines += [""] + [f"- {md_cell(n)}" for n in notes]
    with open(path, "a", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")


def run() -> int:
    log: list[str] = []
    try:
        refs = parse_targets(CONTAINER_IMAGES.read_text(encoding="utf-8"))
        scanner = parse_scanner_ref(SCANNER_DOCKERFILE.read_text(encoding="utf-8"))
        raw = json.loads(EXCEPTIONS_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        emit_protected([f"image-scan: configuration error: {exc}"])
        print("::error::image-scan: configuration error")
        return 2
    today = _utc_today()
    entries = validate_exceptions(raw, today)  # exits 2 on any schema error, before scanning

    runner_temp = os.environ.get("RUNNER_TEMP") or tempfile.gettempdir()
    db_dir = os.path.join(runner_temp, "grype-db")
    os.makedirs(db_dir, exist_ok=True)
    os.chmod(db_dir, 0o777)  # the scanner container runs as another uid

    log.append(f"scanner: {scanner}")
    proc = _run(db_update_argv(scanner, db_dir), log)
    if proc.returncode != 0:
        log.append(f"vulnerability database update failed (exit {proc.returncode})")
        emit_protected(log)
        print("::error::image-scan: vulnerability database update failed")
        return 1

    rows: list[dict] = []
    used: set = set()
    failed: list[str] = []
    for alias, ref in refs.items():
        log.append(f"scanning {alias}: {ref}")
        proc = _run(scan_argv(scanner, ref, db_dir), log)
        if proc.returncode != 0:
            failed.append(f"{alias}: scanner exit {proc.returncode}")
            continue
        try:
            doc = json.loads(proc.stdout)
        except ValueError as exc:
            failed.append(f"{alias}: scanner output is not JSON ({exc})")
            continue
        problems = check_provenance(doc, ref)
        if problems:
            failed += [f"{alias}: {p}" for p in problems]
            continue
        result = evaluate(doc, alias, entries, today)
        used |= result["used"]
        rows += [{**f, "status": "FAIL"} for f in result["findings"]]
        rows += [{**f, "status": "excepted"} for f in result["excepted"]]
        log.append(f"  {alias}: {len(doc['matches'])} matches, {len(result['findings'])} blocking, {len(result['excepted'])} excepted")

    notes: list[str] = []
    for e in entries:
        key = (e["image"], e["package"], e["id"])
        if today > e["expires_date"]:
            notes.append(f"expired (not applied): {e['id']} {e['image']} {e['package']} (expired {e['expires']}, {e['issue']})")
        elif key not in used:
            notes.append(f"stale (matched nothing): {e['id']} {e['image']} {e['package']}")
    log += [f"  {n}" for n in notes]
    for r in rows:
        log.append(f"  {r['status']}: {r['image']} {r['package']} {r['version']} {r['id']} {r['severity']} fix={r['fix']}" + (f" ({r['note']})" if r.get("note") else ""))
    for f in failed:
        log.append(f"  SCAN NOT TRUSTED: {f}")
    emit_protected(log)
    _summary(rows, notes + [f"scan not trusted: {f}" for f in failed])

    blocking = [r for r in rows if r["status"] == "FAIL"]
    if failed:
        print(f"::error::image-scan: {len(failed)} scan problem(s); the result cannot be trusted")
    if blocking:
        print(f"::error::image-scan: {len(blocking)} High or Critical finding(s) without an active exception")
    return 1 if (failed or blocking) else 0


def main() -> int:
    try:
        return run()
    except SystemExit:
        raise
    except Exception as exc:  # fail closed, and never echo scanner text outside the window
        emit_protected([f"image-scan: unexpected {type(exc).__name__}: {exc}"])
        print("::error::image-scan: unexpected error")
        return 1


if __name__ == "__main__":
    sys.exit(main())
