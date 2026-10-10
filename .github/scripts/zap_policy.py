#!/usr/bin/env python3
"""ZAP baseline verdict (ADR-0019 decision 5, issue #123, G3 G4-123-05). Stdlib only.

Reads ZAP's JSON report, the list of URLs ZAP accessed (written by .github/zap/hook.py) and ZAP's exit
code, and decides pass or fail under our own policy. ZAP itself suppresses nothing.

Policy:
- ZAP exit codes other than 0, 1 and 2 fail (3 is ZAP's own error; Docker's 125 to 127 and 137 are not ZAP).
- Every site name and every alert-instance URI must parse to https://localhost:7200 exactly. Origins are
  compared by parsing the URL, never by string prefix.
- The URL list must exist, be non-empty, hold only that origin and include "/" and "/bff/me".
- A riskcode that is missing or unknown counts as High. Only confidence 0 (False Positive) is dropped.
- Medium (2) and High (3) fail unless an active exception covers that alert instance. Low (1) and
  Informational (0) are reported only.
- An invalid exceptions file exits 2 before anything else. An expired entry is listed, never applied.

Exit codes: 0 pass, 1 findings or a result that cannot be trusted, 2 configuration or schema error.

Everything derived from the report, the URL list or the exceptions file is printed between
::stop-commands:: markers with control characters escaped (T-14, H-10), and the step summary escapes
markup. Nothing is written to the environment-file or output-file variables of the runner.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import secrets
import sys
import unicodedata
import urllib.parse
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXCEPTIONS_FILE = ROOT / ".github" / "zap" / "exceptions.json"

ORIGIN = ("https", "localhost", 7200)
REQUIRED_PATHS = ("/", "/bff/me")
OK_ZAP_EXIT_CODES = (0, 1, 2)
MEDIUM = 2
HIGH = 3
RISK_NAMES = {0: "Informational", 1: "Low", 2: "Medium", 3: "High"}
MAX_EXCEPTION_DAYS = 90
MAX_URL_LINES = 200_000
MAX_REPORT_BYTES = 256 * 1024 * 1024

DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
ISSUE_RE = re.compile(r"^#\d+$")
PLUGIN_RE = re.compile(r"^\d+$")
ENTRY_KEYS = {"pluginId", "path", "justification", "issue", "added", "expires"}


# --------------------------------------------------------------------------- text hygiene

def neutralize(text: object) -> str:
    """Escape control and format characters so no report text can look like a workflow command."""
    out = []
    for ch in str(text):
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
    """Print lines between stop-commands markers with a token that no report text can know."""
    out = out or sys.stdout
    token = secrets.token_hex(16)
    print(f"::stop-commands::{token}", file=out)
    for line in lines:
        print(neutralize(line), file=out)
    print(f"::{token}::", file=out)
    out.flush()


# --------------------------------------------------------------------------- origins and paths

def parse_origin(url: object) -> tuple[str, str, int] | None:
    """(scheme, host, port) of an absolute http(s) URL, or None when it does not parse cleanly."""
    if not isinstance(url, str) or not url or url != url.strip():
        return None
    if any(ord(ch) < 0x21 or ord(ch) == 0x7F for ch in url):
        return None
    try:
        parts = urllib.parse.urlsplit(url)
        host = parts.hostname
        port = parts.port  # raises ValueError for 72000 and for 7200.evil
    except ValueError:
        return None
    if parts.username is not None or parts.password is not None:
        return None
    if not parts.scheme or host is None:
        return None
    if port is None:
        port = {"https": 443, "http": 80}.get(parts.scheme)
        if port is None:
            return None
    return parts.scheme, host, port


def is_bff_origin(url: object) -> bool:
    return parse_origin(url) == ORIGIN


def url_path(url: str) -> str:
    """The path of a URL with the query and fragment ignored; an empty path is "/"."""
    return urllib.parse.urlsplit(url).path or "/"


def path_covered(rule_path: str, uri_path: str) -> bool:
    """An exact path, or a prefix that ends in "/" (never "/" alone: the validator refuses it)."""
    if rule_path.endswith("/"):
        return uri_path.startswith(rule_path)
    return uri_path == rule_path


# --------------------------------------------------------------------------- exceptions

def _utc_today() -> date:
    # A Python CI script: invariant 4's analyzer ban applies to .NET code.
    return datetime.now(timezone.utc).date()


def exception_errors(data: object, today: date | None = None) -> tuple[list[str], list[dict]]:
    """(errors, valid entries) for exceptions.json. Mirrors image_scan.validate_exceptions."""
    today = today or _utc_today()
    errors: list[str] = []
    entries: list[dict] = []
    if not isinstance(data, dict) or not isinstance(data.get("exceptions"), list):
        return ["top level must be an object with an 'exceptions' list"], entries
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
        if not PLUGIN_RE.match(e["pluginId"]):
            errors.append(f"{where}: pluginId must be a digit string"); ok = False
        path = e["path"]
        if not path.startswith("/") or path == "/" or any(ch in path for ch in "?#*") or any(ord(ch) < 0x21 for ch in path):
            errors.append(f"{where}: path must be an exact path or a prefix ending in '/', never '/' alone, with no query, glob or whitespace"); ok = False
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
        ident = (e["pluginId"], e["path"])
        if ident in seen:
            errors.append(f"{where}: duplicates another entry (same pluginId and path)"); ok = False
        seen.add(ident)
        if ok:
            entries.append({**e, "added_date": added, "expires_date": expires})
    return errors, entries


def validate_exceptions(data: object, today: date | None = None) -> list[dict]:
    """Any error: print them inside the stop window and exit 2 (as image_scan.py does)."""
    errors, entries = exception_errors(data, today)
    if errors:
        emit_protected([f"zap-policy: invalid exceptions.json: {m}" for m in errors])
        print("::error::zap-policy: .github/zap/exceptions.json failed validation")
        sys.exit(2)
    return entries


# --------------------------------------------------------------------------- report

def parse_risk(value: object) -> int:
    """riskcode as an integer 0..3. Missing, unparsable or out of range counts as High (fail closed)."""
    try:
        if isinstance(value, bool):
            return HIGH
        code = int(str(value).strip())
    except (TypeError, ValueError):
        return HIGH
    return code if 0 <= code <= HIGH else HIGH


def is_false_positive(value: object) -> bool:
    """Only confidence 0 is dropped; a missing or odd confidence stays in."""
    try:
        return not isinstance(value, bool) and int(str(value).strip()) == 0
    except (TypeError, ValueError):
        return False


def read_urls(path: Path) -> list[str]:
    with open(path, encoding="utf-8", errors="strict") as fh:
        lines = []
        for i, line in enumerate(fh):
            if i >= MAX_URL_LINES:
                raise ValueError("URL list is too long")
            line = line.rstrip("\r\n")
            if line.strip():
                lines.append(line)
    return lines


def evaluate(report: object, urls: list[str] | None, zap_exit: int | None,
             exceptions: list[dict], today: date | None = None) -> dict:
    """Apply the policy. Returns {problems, findings, excepted, reported, used}. Pure.

    problems: reasons the result cannot be trusted (any one fails the run).
    findings: Medium or High alert instances without an active exception, as rows.
    excepted: Medium or High rows an active exception covers.
    reported: Low and Informational rows (never fail).
    """
    today = today or _utc_today()
    problems: list[str] = []
    if zap_exit not in OK_ZAP_EXIT_CODES:
        problems.append(f"ZAP exit code {zap_exit!r} is not 0, 1 or 2")

    if urls is None or not urls:
        problems.append("the URL list ZAP wrote is missing or empty: coverage cannot be proven")
    else:
        bad = [u for u in urls if not is_bff_origin(u)]
        if bad:
            problems.append(f"{len(bad)} URL(s) in the URL list are not on https://localhost:7200")
        paths = {url_path(u) for u in urls if is_bff_origin(u)}
        for required in REQUIRED_PATHS:
            if required not in paths:
                problems.append(f"the URL list does not include {required}")

    active = [e for e in exceptions if today <= e["expires_date"]]
    findings: list[dict] = []
    excepted: list[dict] = []
    reported: dict[tuple, dict] = {}
    used: set = set()

    sites = report.get("site") if isinstance(report, dict) else None
    if not isinstance(sites, list):
        problems.append("report has no 'site' list")
        sites = []
    for site in sites:
        if not isinstance(site, dict):
            problems.append("a report site entry is not an object")
            continue
        if not is_bff_origin(site.get("@name")):
            problems.append("a report site is not https://localhost:7200")
        alerts = site.get("alerts")
        if not isinstance(alerts, list):
            problems.append("a report site has no 'alerts' list")
            continue
        for alert in alerts:
            if not isinstance(alert, dict):
                problems.append("a report alert is not an object")
                continue
            plugin = str(alert.get("pluginid", alert.get("pluginId", ""))).strip()
            name = str(alert.get("name") or alert.get("alert") or "(unnamed)")
            risk = parse_risk(alert.get("riskcode"))
            if is_false_positive(alert.get("confidence")):
                continue
            instances = alert.get("instances")
            if not isinstance(instances, list) or not instances:
                # No instance, no URI: the origin and the path cannot be checked, so this cannot be excepted.
                instances = [{"uri": None}]
            for inst in instances:
                uri = inst.get("uri") if isinstance(inst, dict) else None
                if not is_bff_origin(uri):
                    problems.append(f"an alert instance of plugin {plugin or '?'} is not on https://localhost:7200")
                    continue
                path = url_path(uri)
                row = {"pluginId": plugin, "name": name, "risk": risk, "path": path}
                if risk < MEDIUM:
                    key = (plugin, name, risk)
                    agg = reported.setdefault(key, {"pluginId": plugin, "name": name, "risk": risk, "count": 0})
                    agg["count"] += 1
                    continue
                hit = next((e for e in active if e["pluginId"] == plugin and path_covered(e["path"], path)), None)
                if hit:
                    used.add((hit["pluginId"], hit["path"]))
                    excepted.append({**row, "issue": hit["issue"]})
                else:
                    findings.append(row)
    return {"problems": problems, "findings": findings, "excepted": excepted,
            "reported": sorted(reported.values(), key=lambda r: (-r["risk"], r["pluginId"])), "used": used}


# --------------------------------------------------------------------------- output

def _summary(result: dict, notes: list[str]) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    lines = ["### ZAP baseline", ""]
    blocking = result["findings"]
    if blocking:
        lines += ["| Plugin | Alert | Risk | Path | Status |", "| --- | --- | --- | --- | --- |"]
        for r in blocking:
            lines.append("| " + " | ".join(md_cell(c) for c in (r["pluginId"], r["name"], RISK_NAMES[r["risk"]], r["path"], "FAIL")) + " |")
    else:
        lines.append("No Medium or High alerts without an exception.")
    if result["excepted"]:
        lines += ["", "Excepted:", "", "| Plugin | Alert | Risk | Path | Issue |", "| --- | --- | --- | --- | --- |"]
        for r in result["excepted"]:
            lines.append("| " + " | ".join(md_cell(c) for c in (r["pluginId"], r["name"], RISK_NAMES[r["risk"]], r["path"], r["issue"])) + " |")
    if result["reported"]:
        lines += ["", "Reported only (Low and Informational):", "", "| Plugin | Alert | Risk | Instances |", "| --- | --- | --- | --- |"]
        for r in result["reported"]:
            lines.append("| " + " | ".join(md_cell(c) for c in (r["pluginId"], r["name"], RISK_NAMES[r["risk"]], r["count"])) + " |")
    lines += [""] + [f"- {md_cell(n)}" for n in notes]
    with open(path, "a", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")


def run(report_path: Path, urls_path: Path, zap_exit: int, exceptions_path: Path = EXCEPTIONS_FILE) -> int:
    try:
        raw = json.loads(exceptions_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        emit_protected([f"zap-policy: cannot read the exceptions file: {type(exc).__name__}"])
        print("::error::zap-policy: configuration error")
        return 2
    today = _utc_today()
    entries = validate_exceptions(raw, today)  # exits 2 on any schema error, before the report is read

    problems_early: list[str] = []
    report: object = None
    try:
        if report_path.stat().st_size > MAX_REPORT_BYTES:
            raise ValueError("report is too large")
        report = json.loads(report_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        problems_early.append(f"the ZAP JSON report is missing or unparsable ({type(exc).__name__})")
    urls: list[str] | None
    try:
        urls = read_urls(urls_path)
    except (OSError, ValueError) as exc:
        urls = None
        problems_early.append(f"the URL list cannot be read ({type(exc).__name__})")

    result = evaluate(report, urls, zap_exit, entries, today)
    result["problems"] = problems_early + result["problems"]

    notes: list[str] = []
    for e in entries:
        if today > e["expires_date"]:
            notes.append(f"expired (not applied): plugin {e['pluginId']} {e['path']} (expired {e['expires']}, {e['issue']})")
        elif (e["pluginId"], e["path"]) not in result["used"]:
            notes.append(f"stale (matched nothing): plugin {e['pluginId']} {e['path']}")

    log = [f"zap exit code: {zap_exit}",
           f"URLs ZAP accessed: {len(urls) if urls else 0}",
           f"blocking: {len(result['findings'])}, excepted: {len(result['excepted'])}, reported only: {sum(r['count'] for r in result['reported'])}"]
    log += [f"  FAIL: plugin {r['pluginId']} {RISK_NAMES[r['risk']]} {r['name']} at {r['path']}" for r in result["findings"]]
    log += [f"  excepted: plugin {r['pluginId']} {RISK_NAMES[r['risk']]} {r['name']} at {r['path']} ({r['issue']})" for r in result["excepted"]]
    log += [f"  {RISK_NAMES[r['risk']]}: plugin {r['pluginId']} {r['name']} x{r['count']}" for r in result["reported"]]
    log += [f"  {n}" for n in notes]
    log += [f"  RESULT NOT TRUSTED: {p}" for p in result["problems"]]
    emit_protected(log)
    _summary(result, notes + [f"result not trusted: {p}" for p in result["problems"]])

    if result["problems"]:
        print(f"::error::zap-policy: {len(result['problems'])} problem(s); the result cannot be trusted")
    if result["findings"]:
        print(f"::error::zap-policy: {len(result['findings'])} Medium or High alert instance(s) without an active exception")
    return 1 if (result["problems"] or result["findings"]) else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="ZAP baseline verdict")
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--urls", required=True, type=Path)
    parser.add_argument("--zap-exit", required=True, type=int)
    parser.add_argument("--exceptions", type=Path, default=EXCEPTIONS_FILE)
    args = parser.parse_args(argv)
    try:
        return run(args.report, args.urls, args.zap_exit, args.exceptions)
    except SystemExit:
        raise
    except Exception as exc:  # fail closed, and never echo report text outside the window
        emit_protected([f"zap-policy: unexpected {type(exc).__name__}"])
        print("::error::zap-policy: unexpected error")
        return 1


if __name__ == "__main__":
    sys.exit(main())
