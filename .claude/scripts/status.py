#!/usr/bin/env python3
"""The status pack's script (ADR 0007): record an issue's end and a phase's exit, and assemble the delivery report.

Usage (from the repository root, by the main session through the `phase-exit` skill and the `issue` skill's finish;
never by a listed agent):
  python .claude/scripts/status.py end <n> [--missing planning|information:<what>]... [--replace]
  python .claude/scripts/status.py phase-exit <id> [--scope-change "EP-nn|YYYY-MM-DD|<change>[|<thread>]"]...
                                   [--lesson <text>]... [--done EP-nn]... [--descoped EP-nn]... [--replace]
  python .claude/scripts/status.py report <id> --trigger phase-exit|on-request [--correction]

`end` records, after the owner confirms an issue's last pull request merged and its branch deleted, the end time and
what slowed or blocked the issue, as status-issue-<n>.json in the capture folder (usage.capture_folder). `phase-exit`
records the owner's phase-exit confirmation, as status-phase-<id>.json: the exit time, scope changes per epic,
lessons, and the epics done or descoped. With --replace either record is written again with its first time kept.

`report` assembles the delivery report in format 1.0.0 from those records, the core's start-<n>.json records and
captures, the pipeline manifests (kind, phase, epic, gates, the G6 findings line), the plan intake.py wrote
(docs/intake/<slug>.json) and packs.status in .claude/project.json. It checks the report against the format's schema
and its twelve cross-checks, checks every free-text field with literals.py's patterns, and writes it into the capture
folder as report-<yyyy-mm-dd>-<id>.json, never under a name the capture copy job takes; --correction corrects the
phase's latest report, as the next -r2, -r3 under that report's date. It prints the file's SHA-256 and the name it takes in the exchange. The owner
approves it; the main session then copies it with the file edit tools. Nothing here is sent anywhere.

A finished issue's manifest is read from this checkout (pull main first); an open issue's from this checkout or, when
it is not here, from its local or origin issue branch. A started issue whose manifest is found nowhere is not reported
and is named in the output. An unknown is a `null` or a `missing` with its reason, never a zero; a gate state the
format has no value for, or a G6 review without its findings line, stops the report.

Exit 0 = done; 1 = stopped, with the reason (a record cannot be used, a check failed, a file exists or could not be
written); 2 = usage, or .claude/project.json cannot be used.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import argparse
import contextlib
import hashlib
import importlib.util
import json
import os
import re
import secrets
import unicodedata
from datetime import date, datetime, timezone
from pathlib import Path

SELF = Path(__file__).resolve()
ROOT = SELF.parents[2]


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
capture = _load("_norma_capture", SELF.parent / "capture.py")
literals = _load("_norma_literals", SELF.parent / "literals.py")

FORMAT_VERSION = "1.0.0"
EPIC = re.compile(r"EP-[0-9]{2,}")
THREAD = re.compile(r"[A-Z]-[0-9]{3,}")  # an exchange thread id
HANDOFF_THREAD = re.compile(r"G-[0-9]{3,}")
DATE = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}")
PHASE_ID = re.compile(r"[0-9]{1,3}(?:\.[0-9]{1,3})?")
START_FILE = re.compile(r"start-([1-9][0-9]{0,8})\.json")
CAPTURE_FILE = re.compile(r"capture-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}\.json")
PHASE_FILE = re.compile(r"status-phase-([0-9]{1,3}(?:\.[0-9]{1,3})?)\.json")
META = re.compile(r"- (Kind|Phase|Epic):[ \t]*(.*?)\s*")
COUNTS = ("input_tokens", "output_tokens", "cache_read_tokens", "cache_creation_tokens")
SPLIT = ("cache_creation_5m_tokens", "cache_creation_1h_tokens")
# the reasons capture.py gives an issue's capture that concern that issue alone; any other reason of an issue's capture
# (an unreadable record file, expired records, another account) also hides usage outside the issues
ISSUE_ONLY_REASONS = ("the issue's start was not recorded",
                      "the issue ran in a sandbox, whose records are not on this machine",
                      "no usage was found for the issue")
CUT_REASON = "a capture's reasons were cut short"  # capture.py keeps a missing_reason to 200 characters
MAX_RECORD_BYTES = 1024 * 1024
MAX_TEXT = 300
MAX_ITEMS = 50
# a free-text value is one line of visible text: no control, format (zero-width, bidi), surrogate, private-use or
# unassigned character, and no line or paragraph separator
BAD_CATEGORIES = {"Cc", "Cf", "Cs", "Co", "Cn", "Zl", "Zp"}

# The delivery report format 1.0.0, as the accepted format contract gives it (JSON Schema draft 2020-12, closed). Only
# the title differs: it names no participant. The contract names the draft in words, so this copy has no $schema link.
SCHEMA = json.loads(r"""
{
  "$id": "delivery-report.schema.json",
  "title": "SDLC factory delivery report, format 1.0.0",
  "type": "object",
  "required": ["format_version", "project_id", "factory", "report_date", "trigger", "phase", "epics", "issues", "unattributed_usage"],
  "properties": {
    "format_version": { "type": "string", "pattern": "^1\\.\\d+\\.\\d+$" },
    "project_id": { "type": "string", "pattern": "^GL-\\d{4}$" },
    "factory": { "type": "string", "pattern": "^[a-z0-9]+(-[a-z0-9]+)*$" },
    "report_date": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$" },
    "trigger": { "type": "string", "enum": ["phase-exit", "on-request"] },
    "handoff": {
      "type": "object",
      "required": ["thread", "product_version"],
      "properties": {
        "thread": { "type": "string", "pattern": "^G-\\d{3,}$" },
        "product_version": { "type": "integer", "minimum": 1 }
      },
      "additionalProperties": false
    },
    "phase": {
      "type": "object",
      "required": ["id", "milestone", "status", "planned_start", "planned_end", "actual_start", "actual_end"],
      "properties": {
        "id": { "type": "string", "minLength": 1 },
        "milestone": { "type": "string", "minLength": 1 },
        "status": { "type": "string", "enum": ["not-started", "in-progress", "done"] },
        "planned_start": { "type": ["string", "null"], "pattern": "^\\d{4}-\\d{2}-\\d{2}$" },
        "planned_end": { "type": ["string", "null"], "pattern": "^\\d{4}-\\d{2}-\\d{2}$" },
        "actual_start": { "type": ["string", "null"], "pattern": "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}(:\\d{2})?(Z|[+-]\\d{2}:\\d{2})$" },
        "actual_end": { "type": ["string", "null"], "pattern": "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}(:\\d{2})?(Z|[+-]\\d{2}:\\d{2})$" }
      },
      "additionalProperties": false
    },
    "epics": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["epic_id", "status", "issues", "planned_start", "planned_end"],
        "properties": {
          "epic_id": { "type": "string", "pattern": "^EP-\\d{2,}$" },
          "status": { "type": "string", "enum": ["not-started", "in-progress", "done", "descoped"] },
          "issues": {
            "type": "array",
            "items": { "type": "integer", "minimum": 1 }
          },
          "planned_start": { "type": ["string", "null"], "pattern": "^\\d{4}-\\d{2}-\\d{2}$" },
          "planned_end": { "type": ["string", "null"], "pattern": "^\\d{4}-\\d{2}-\\d{2}$" },
          "scope_changes": {
            "type": "array",
            "items": {
              "type": "object",
              "required": ["date", "change"],
              "properties": {
                "date": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$" },
                "change": { "type": "string", "minLength": 1 },
                "thread": { "type": "string" }
              },
              "additionalProperties": false
            }
          }
        },
        "additionalProperties": false
      }
    },
    "issues": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["number", "epic_id", "start", "end", "run_location", "agent_usage", "gates", "findings", "rechecks", "missing_inputs"],
        "properties": {
          "number": { "type": "integer", "minimum": 1 },
          "epic_id": { "type": ["string", "null"], "pattern": "^EP-\\d{2,}$" },
          "kind": { "type": "string", "enum": ["feature", "fix", "rework", "tooling", "docs"] },
          "start": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}(:\\d{2})?(Z|[+-]\\d{2}:\\d{2})$" },
          "end": { "type": ["string", "null"], "pattern": "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}(:\\d{2})?(Z|[+-]\\d{2}:\\d{2})$" },
          "run_location": { "type": "string", "enum": ["host", "sandbox"] },
          "agent_usage": { "$ref": "#/$defs/usage" },
          "gates": {
            "type": "array",
            "items": {
              "type": "object",
              "required": ["gate", "verdict"],
              "properties": {
                "gate": { "type": "string", "minLength": 1 },
                "verdict": { "type": "string", "enum": ["PASS", "PASS-WITH-NOTES", "N/A", "BLOCK", "SKIPPED"] },
                "reason": { "type": "string" }
              },
              "additionalProperties": false
            }
          },
          "findings": {
            "type": "object",
            "required": ["high", "medium", "low", "info"],
            "properties": {
              "high": { "type": "integer", "minimum": 0 },
              "medium": { "type": "integer", "minimum": 0 },
              "low": { "type": "integer", "minimum": 0 },
              "info": { "type": "integer", "minimum": 0 }
            },
            "additionalProperties": false
          },
          "rechecks": { "type": "integer", "minimum": 0 },
          "missing_inputs": {
            "type": "array",
            "items": {
              "type": "object",
              "required": ["kind", "what"],
              "properties": {
                "kind": { "type": "string", "enum": ["planning", "information"] },
                "what": { "type": "string", "minLength": 1 }
              },
              "additionalProperties": false
            }
          }
        },
        "additionalProperties": false
      }
    },
    "unattributed_usage": { "$ref": "#/$defs/usage" },
    "lessons": {
      "type": "array",
      "items": { "type": "string", "minLength": 1 }
    }
  },
  "$defs": {
    "usage": {
      "type": "object",
      "required": ["status", "by_model"],
      "properties": {
        "status": { "type": "string", "enum": ["complete", "partial", "missing"] },
        "missing_reason": { "type": "string" },
        "by_model": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["model", "source", "input_tokens", "output_tokens", "cache_read_tokens", "cache_creation_tokens"],
            "properties": {
              "model": { "type": "string", "minLength": 1 },
              "source": { "type": "string", "enum": ["main", "subagent"] },
              "agent": { "type": "string", "minLength": 1 },
              "input_tokens": { "type": "integer", "minimum": 0 },
              "output_tokens": { "type": "integer", "minimum": 0 },
              "cache_read_tokens": { "type": "integer", "minimum": 0 },
              "cache_creation_tokens": { "type": "integer", "minimum": 0 },
              "cache_creation_5m_tokens": { "type": "integer", "minimum": 0 },
              "cache_creation_1h_tokens": { "type": "integer", "minimum": 0 }
            },
            "additionalProperties": false
          }
        }
      },
      "additionalProperties": false
    }
  },
  "additionalProperties": false
}
""")


class StatusError(Exception):
    """A record or a report that cannot be used; the message names the place and the rule."""


# --- the schema check: exactly the keywords format 1.x may use ---

KEYWORDS = {"type", "required", "properties", "additionalProperties", "enum", "pattern", "minimum", "minLength",
            "items", "$ref", "$id", "title", "$defs"}


def schema_keywords(node: object, nested: str = "") -> set[str]:
    """Every keyword the schema uses (the names under properties and $defs are names, not keywords)."""
    found: set[str] = set()
    if isinstance(node, dict):
        for key, value in node.items():
            if nested in ("properties", "$defs"):
                found |= schema_keywords(value)
            else:
                found.add(key)
                found |= schema_keywords(value, key)
    elif isinstance(node, list) and nested not in ("required", "enum", "type"):
        for item in node:
            found |= schema_keywords(item)
    return found


if schema_keywords(SCHEMA) - KEYWORDS:  # a keyword this check does not apply would pass every report unchecked
    raise RuntimeError("the report schema uses a keyword the check does not apply")


def _ecma(pattern: str) -> re.Pattern[str]:
    """A schema pattern as JSON Schema reads it: \\d is an ASCII digit, and a final $ matches only at the very end
    (Python's $ also matches before a final line feed)."""
    if pattern.endswith("$") and not pattern.endswith("\\$"):
        pattern = pattern[:-1] + r"\Z"
    return re.compile(pattern, re.ASCII)


def _is(value: object, kind: str) -> bool:
    return {"string": isinstance(value, str), "object": isinstance(value, dict), "array": isinstance(value, list),
            "null": value is None, "boolean": isinstance(value, bool),
            "integer": isinstance(value, int) and not isinstance(value, bool),
            "number": isinstance(value, (int, float)) and not isinstance(value, bool)}[kind]


def schema_errors(value: object, schema: dict, where: str = "$") -> list[str]:
    if "$ref" in schema:
        return schema_errors(value, SCHEMA["$defs"][schema["$ref"].removeprefix("#/$defs/")], where)
    if "type" in schema:
        kinds = schema["type"] if isinstance(schema["type"], list) else [schema["type"]]
        if not any(_is(value, kind) for kind in kinds):
            return [f"{where} is not {' or '.join(kinds)}"]
    errors = []
    if "enum" in schema and not any(type(value) is type(e) and value == e for e in schema["enum"]):
        errors.append(f"{where} is not one of {', '.join(map(str, schema['enum']))}")
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0):
            errors.append(f"{where} is shorter than {schema['minLength']}")
        if "pattern" in schema and not _ecma(schema["pattern"]).search(value):
            errors.append(f"{where} does not have the form {schema['pattern']}")
    if _is(value, "number") and "minimum" in schema and value < schema["minimum"]:
        errors.append(f"{where} is below {schema['minimum']}")
    if isinstance(value, dict):
        properties = schema.get("properties", {})
        errors += [f"{where} has no {key}" for key in schema.get("required", []) if key not in value]
        for key, item in value.items():
            if key in properties:
                errors += schema_errors(item, properties[key], f"{where}.{key}")
            elif schema.get("additionalProperties") is False:
                errors.append(f"{where} has {key}, which the format does not list")
    if isinstance(value, list) and "items" in schema:
        for i, item in enumerate(value):
            errors += schema_errors(item, schema["items"], f"{where}[{i}]")
    return errors


# --- the twelve cross-checks of format 1.0.0 ---

def _usages(report: dict):
    for issue in report["issues"]:
        yield f"issue #{issue['number']}", issue["agent_usage"]
    yield "unattributed_usage", report["unattributed_usage"]


def cross_check_errors(report: dict) -> list[str]:
    errors = []
    epics = {e["epic_id"]: e for e in report["epics"]}
    numbers = [i["number"] for i in report["issues"]]
    if len(epics) != len(report["epics"]):
        errors.append("1: an epic id is listed twice")
    if len(set(numbers)) != len(numbers):
        errors.append("1: an issue number is listed twice")
    issues = {i["number"]: i for i in report["issues"]}
    for issue in report["issues"]:
        epic = issue["epic_id"]
        if epic is not None and (epic not in epics or issue["number"] not in epics[epic]["issues"]):
            errors.append(f"2: issue #{issue['number']} names {epic}, which does not list it")
    for epic in report["epics"]:
        for number in epic["issues"]:
            if number not in issues or issues[number]["epic_id"] != epic["epic_id"]:
                errors.append(f"2: {epic['epic_id']} lists issue #{number}, which does not name it")
    for issue in report["issues"]:
        if issue["end"] is not None and capture.parse_time(issue["end"]) < capture.parse_time(issue["start"]):
            errors.append(f"3: issue #{issue['number']} ends before it starts")
    for label, usage in _usages(report):
        if usage["status"] == "missing" and usage["by_model"]:
            errors.append(f"4: {label}: missing usage has counts")
        if usage["status"] == "complete" and not usage["by_model"] and label != "unattributed_usage":
            errors.append(f"4: {label}: complete usage has no counts")
        if usage["status"] != "complete" and not usage.get("missing_reason"):
            errors.append(f"4: {label}: {usage['status']} usage has no reason")
    for issue in report["issues"]:
        for gate in issue["gates"]:
            if gate["verdict"] in ("N/A", "SKIPPED") and not gate.get("reason"):
                errors.append(f"5: issue #{issue['number']} {gate['gate']} is {gate['verdict']} without a reason")
    phase = report["phase"]
    if report["trigger"] == "phase-exit" and phase["status"] != "done":
        errors.append("6: a phase-exit report needs phase.status done")
    if phase["status"] == "done" and phase["actual_end"] is None:
        errors.append("6: a done phase needs actual_end")
    starts = [capture.parse_time(i["start"]) for i in report["issues"]]
    if (capture.parse_time(phase["actual_start"]) if phase["actual_start"] else None) != min(starts, default=None):
        errors.append("7: phase.actual_start is not the earliest issue start")
    for issue in report["issues"]:
        if issue["end"] is None and issue["gates"]:
            errors.append(f"8: open issue #{issue['number']} has gates")
        if issue["run_location"] == "sandbox" and issue["agent_usage"]["status"] == "complete":
            errors.append(f"9: sandbox issue #{issue['number']} has complete usage")
    for label, usage in _usages(report):
        keys = set()
        for row in usage["by_model"]:
            if (SPLIT[0] in row) != (SPLIT[1] in row) or (
                    SPLIT[0] in row and row[SPLIT[0]] + row[SPLIT[1]] != row["cache_creation_tokens"]):
                errors.append(f"10: {label}: {row['model']} has cache-write parts that are not both there or do not sum")
            if "agent" in row and row["source"] != "subagent":
                errors.append(f"11: {label}: {row['model']} names an agent on a main entry")
            key = (row["model"], row["source"], row.get("agent"))
            if key in keys:
                errors.append(f"12: {label}: {row['model']} is listed twice for one source and agent")
            keys.add(key)
    return errors


# --- free text, project facts and files ---

def free_text(value: object, where: str, probes: list[str]) -> str:
    """One line of visible delivery text that literals.py finds nothing in: the report leaves the repository."""
    if not isinstance(value, str) or not 1 <= len(value) <= MAX_TEXT or value != value.strip():
        raise StatusError(f"{where} must be 1 to {MAX_TEXT} characters, with no space at either end")
    if any(unicodedata.category(c) in BAD_CATEGORIES for c in value):
        raise StatusError(f"{where} holds a control, invisible or line-break character")
    found = literals.scan_text(value, probes)
    if found:
        raise StatusError(f"{where} holds a {found[0][1]} literal; the report is shared, so describe delivery only")
    return value


def phases_of(project: dict) -> dict[str, str]:
    """packs.status.phases as {id: milestone title}; an id given twice cannot name one milestone."""
    phases: dict[str, str] = {}
    for item in project["packs"]["status"]["phases"]:
        phase_id, _, title = item.partition("=")
        if phase_id in phases:
            raise StatusError(f"packs.status.phases gives phase {phase_id} twice")
        phases[phase_id] = title
    return phases


def read_json(path: Path, what: str) -> object:
    if not capture.plain(path, "file"):
        raise StatusError(f"{what} is not a regular file")
    try:
        if path.stat().st_size > MAX_RECORD_BYTES:
            raise StatusError(f"{what} is larger than {MAX_RECORD_BYTES} bytes")
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, RecursionError):
        raise StatusError(f"{what} cannot be read as JSON") from None


def write_record(folder: Path, name: str, data: dict, replace: bool, hint: str = "give --replace to record it again") -> None:
    """A record or a report, written whole or not at all: to a temporary name the copy job never takes, then moved
    into place. Without `replace` an existing name stops it."""
    target = folder / name
    if os.path.lexists(target) and not (replace and capture.plain(target, "file")):
        raise StatusError(f"{name} exists" + ("" if replace else f"; {hint}"))
    temporary = folder / f"status-part-{secrets.token_hex(4)}.json"
    try:
        with open(temporary, "x", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps(data, indent=2, ensure_ascii=False) + "\n")
        if replace:
            os.replace(temporary, target)
        else:
            os.link(temporary, target)  # fails when the name exists: a report is never overwritten
            temporary.unlink()
    except FileExistsError:
        raise StatusError(f"{name} exists; nothing overwritten") from None
    except OSError as exc:
        raise StatusError(f"{name} was not written ({type(exc).__name__})") from None
    finally:
        with contextlib.suppress(OSError):
            if capture.plain(temporary, "file"):
                temporary.unlink()


# --- `end` and `phase-exit` ---

def parse_missing(items: list[str], probes: list[str]) -> list[dict]:
    if len(items) > MAX_ITEMS:
        raise StatusError(f"at most {MAX_ITEMS} missing inputs")
    out = []
    for i, item in enumerate(items):
        kind, sep, what = item.partition(":")
        if not sep or kind not in ("planning", "information"):
            raise StatusError(f"--missing {i + 1} is planning:<what> or information:<what>")
        out.append({"kind": kind, "what": free_text(what.strip(), f"--missing {i + 1}", probes)})
    return out


def end(project: dict, folder: Path, issue: int, missing: list[str], replace: bool) -> str:
    probes = literals.probes_from(project)
    inputs = parse_missing(missing, probes)
    started, where = capture.issue_start(folder, issue)
    if started is None:
        raise StatusError(f"issue #{issue} has no start record (start-{issue}.json); it was not started with /issue")
    name = f"status-issue-{issue}.json"
    ended = capture.utc(datetime.now(timezone.utc))
    if replace and capture.plain(folder / name, "file"):
        ended = read_issue_end(folder, issue, probes)["end"]  # the confirmation's time stays
    write_record(folder, name, {"issue": issue, "end": ended, "missing_inputs": inputs}, replace)
    return f"status: issue #{issue}: end recorded ({name}, {len(inputs)} missing input(s))"


def parse_scope_change(item: str, i: int, probes: list[str]) -> dict:
    parts = item.split("|")
    where = f"--scope-change {i + 1}"
    if len(parts) not in (3, 4) or not EPIC.fullmatch(parts[0]) or not real_date(parts[1]):
        raise StatusError(f"{where} is EP-nn|YYYY-MM-DD|<change>[|<thread id>]")
    change = {"epic": parts[0], "date": parts[1], "change": free_text(parts[2].strip(), where, probes)}
    if len(parts) == 4:
        if not THREAD.fullmatch(parts[3]):
            raise StatusError(f"{where}: the thread is an exchange thread id such as G-012")
        change["thread"] = parts[3]
    return change


def real_date(text: str) -> bool:
    if not DATE.fullmatch(text):
        return False
    try:
        date.fromisoformat(text)
    except ValueError:
        return False
    return True


def phase_exit(project: dict, folder: Path, phase_id: str, changes: list[str], lessons: list[str], done: list[str],
               descoped: list[str], replace: bool) -> str:
    if phase_id not in phases_of(project):
        raise StatusError(f"phase {phase_id} is not in packs.status.phases")
    probes = literals.probes_from(project)
    for epic in done + descoped:
        if not EPIC.fullmatch(epic):
            raise StatusError("--done and --descoped each take an epic id such as EP-01")
    if len(set(done + descoped)) != len(done + descoped):
        raise StatusError("an epic is given twice in --done and --descoped")
    if len(changes) > MAX_ITEMS or len(lessons) > MAX_ITEMS:
        raise StatusError(f"at most {MAX_ITEMS} scope changes and {MAX_ITEMS} lessons")
    record = {"phase": phase_id, "actual_end": capture.utc(datetime.now(timezone.utc)),
              "scope_changes": [parse_scope_change(c, i, probes) for i, c in enumerate(changes)],
              "lessons": [free_text(t.strip(), f"--lesson {i + 1}", probes) for i, t in enumerate(lessons)],
              "done": sorted(done), "descoped": sorted(descoped)}
    plan = read_plan(project["usage"]["project_id"])
    known = {e["id"] for e in plan["epics"]} if plan else set()
    for epic in done + descoped + [c["epic"] for c in record["scope_changes"]]:
        if epic not in known:  # the report lists only the plan's epics: an unknown one would be dropped unseen
            raise StatusError(f"{epic} is not an epic of the plan (docs/intake/<slug>.json)")
    name = f"status-phase-{phase_id}.json"
    if replace and capture.plain(folder / name, "file"):
        record["actual_end"] = read_phase_exits(folder, probes)[phase_id]["actual_end"]
    write_record(folder, name, record, replace)
    return (f"status: phase {phase_id}: exit recorded ({name}, {len(record['scope_changes'])} scope change(s), "
            f"{len(record['lessons'])} lesson(s))")


# --- reading the records back (each checked again: the folder is the main session's, not the script's) ---

def read_issue_end(folder: Path, issue: int, probes: list[str]) -> dict | None:
    path = folder / f"status-issue-{issue}.json"
    if not os.path.lexists(path):
        return None
    data = read_json(path, path.name)
    if not isinstance(data, dict) or set(data) != {"issue", "end", "missing_inputs"} or data["issue"] != issue \
            or capture.parse_time(data["end"]) is None or not isinstance(data["missing_inputs"], list):
        raise StatusError(f"{path.name} is not an end record status.py wrote")
    for i, item in enumerate(data["missing_inputs"]):
        if not isinstance(item, dict) or set(item) != {"kind", "what"} or item["kind"] not in ("planning", "information"):
            raise StatusError(f"{path.name}: missing input {i + 1} is not a kind and a what")
        free_text(item["what"], f"{path.name}: missing input {i + 1}", probes)
    return data


def read_phase_exits(folder: Path, probes: list[str]) -> dict[str, dict]:
    exits = {}
    for path in sorted(folder.glob("status-phase-*.json")):
        m = PHASE_FILE.fullmatch(path.name)
        if not m:
            raise StatusError(f"{path.name} is not a phase-exit record name")
        data = read_json(path, path.name)
        if not isinstance(data, dict) or set(data) != {"phase", "actual_end", "scope_changes", "lessons", "done",
                                                       "descoped"} \
                or data["phase"] != m.group(1) or capture.parse_time(data["actual_end"]) is None:
            raise StatusError(f"{path.name} is not a phase-exit record status.py wrote")
        for key in ("scope_changes", "lessons", "done", "descoped"):
            if not isinstance(data[key], list) or len(data[key]) > MAX_ITEMS:
                raise StatusError(f"{path.name}: {key} is not a list status.py wrote")
        for i, change in enumerate(data["scope_changes"]):
            if not isinstance(change, dict) or not {"epic", "date", "change"} <= set(change) <= {"epic", "date", "change", "thread"} \
                    or not isinstance(change["epic"], str) or not EPIC.fullmatch(change["epic"]) \
                    or not isinstance(change["date"], str) or not real_date(change["date"]) \
                    or ("thread" in change and not (isinstance(change["thread"], str) and THREAD.fullmatch(change["thread"]))):
                raise StatusError(f"{path.name}: scope change {i + 1} is not one status.py wrote")
            free_text(change["change"], f"{path.name}: scope change {i + 1}", probes)
        for i, lesson in enumerate(data["lessons"]):
            free_text(lesson, f"{path.name}: lesson {i + 1}", probes)
        if not all(isinstance(e, str) and EPIC.fullmatch(e) for e in data["done"] + data["descoped"]):
            raise StatusError(f"{path.name}: done and descoped hold epic ids only")
        exits[data["phase"]] = data
    return exits


def read_starts(folder: Path) -> dict[int, dict]:
    starts = {}
    for path in sorted(folder.glob("start-*.json")):
        m = START_FILE.fullmatch(path.name)
        if not m:
            continue
        data = read_json(path, path.name)
        issue = int(m.group(1))
        if not isinstance(data, dict) or data.get("issue") != issue \
                or capture.parse_time(data.get("started_at")) is None \
                or data.get("where") not in ("this-machine", "sandbox"):
            raise StatusError(f"{path.name} is not a start record capture.py wrote")
        starts[issue] = {"start": capture.utc(capture.parse_time(data["started_at"])),
                         "run_location": "sandbox" if data["where"] == "sandbox" else "host"}
    return starts


def _count(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and 0 <= value <= capture.MAX_TOKENS


def read_captures(folder: Path, project_id: str) -> list[dict]:
    """Every capture, checked against capture format 1.x and its rules; one that is not stops the report."""
    captures = []
    for path in sorted(folder.glob("capture-*.json")):
        bad = StatusError(f"{path.name} is not a capture in format 1.x; nothing written")
        if not CAPTURE_FILE.fullmatch(path.name):
            raise bad
        data = read_json(path, path.name)
        if not isinstance(data, dict) or set(data) != {"format_version", "project_id", "capture_id", "captured_at",
                                                       "issue", "window", "usage"}:
            raise bad
        usage = data["usage"]
        if not (isinstance(data["format_version"], str) and _ecma(r"^1\.\d+\.\d+$").search(data["format_version"])) \
                or data["project_id"] != project_id or capture.parse_time(data["captured_at"]) is None \
                or not (data["issue"] is None or (_count(data["issue"]) and data["issue"] >= 1)) \
                or not isinstance(usage, dict) or not {"status", "by_model"} <= set(usage) <= {"status", "by_model", "missing_reason"} \
                or usage["status"] not in ("complete", "partial", "missing") or not isinstance(usage["by_model"], list) \
                or len(usage["by_model"]) > capture.MAX_ENTRIES:
            raise bad
        if (usage["status"] == "complete") == ("missing_reason" in usage) \
                or (usage["status"] == "missing") != (not usage["by_model"]):
            raise bad
        if "missing_reason" in usage and not (isinstance(usage["missing_reason"], str)
                                              and re.fullmatch(r"[ -~]{1,200}", usage["missing_reason"])):
            raise bad
        for row in usage["by_model"]:
            if not isinstance(row, dict) or not {"model", "source", *COUNTS} <= set(row) <= {"model", "source", "agent", *COUNTS, *SPLIT} \
                    or not (isinstance(row["model"], str) and len(row["model"]) <= 100 and capture.MODEL.fullmatch(row["model"])) \
                    or row["source"] not in ("main", "subagent") \
                    or ("agent" in row and not (row["source"] == "subagent" and isinstance(row["agent"], str)
                                                and len(row["agent"]) <= 100 and capture.AGENT.fullmatch(row["agent"]))) \
                    or not all(_count(row[k]) for k in COUNTS + tuple(k for k in SPLIT if k in row)) \
                    or (SPLIT[0] in row) != (SPLIT[1] in row) \
                    or (SPLIT[0] in row and row[SPLIT[0]] + row[SPLIT[1]] != row["cache_creation_tokens"]):
                raise bad
        data["_at"] = capture.parse_time(data["captured_at"])
        captures.append(data)
    return captures


def combine(captures: list[dict], none_reason: str) -> dict:
    """Several captures as one usage (the format's rule): counts add per model, source and agent; `missing` when no
    capture has counts and one is missing or partial, `complete` when every one is complete, else `partial`; the
    reasons joined, each once."""
    if not captures:
        return {"status": "missing", "missing_reason": none_reason, "by_model": []}
    groups: dict[tuple, dict] = {}
    reasons: list[str] = []
    for item in sorted(captures, key=lambda c: c["_at"]):
        usage = item["usage"]
        if usage["status"] != "complete" and usage["missing_reason"] not in reasons:
            reasons.append(usage["missing_reason"])
        for row in usage["by_model"]:
            group = groups.setdefault((row["model"], row["source"], row.get("agent")), {"split": True})
            for name in COUNTS + SPLIT:
                group[name] = group.get(name, 0) + row.get(name, 0)
            group["split"] = group["split"] and SPLIT[0] in row
    statuses = {item["usage"]["status"] for item in captures}
    status = "missing" if not groups else "complete" if statuses == {"complete"} else "partial"
    rows = []
    for (model, source, agent), group in sorted(groups.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2] or "")):
        row = {"model": model, "source": source, **({"agent": agent} if agent else {})}
        row.update({name: group[name] for name in COUNTS})
        if group["split"]:
            row.update({name: group[name] for name in SPLIT})
        rows.append(row)
    usage = {"status": status, "by_model": rows}
    if status != "complete":
        usage["missing_reason"] = "; ".join(reasons)
    return usage


def read_plan(project_id: str) -> dict | None:
    """docs/intake/<slug>.json, the plan intake.py wrote; None for a project without a handoff."""
    folder = ROOT / "docs" / "intake"
    plans = sorted(folder.glob("*.json")) if capture.plain(folder, "dir") else []
    if not plans:
        return None
    if len(plans) > 1:
        raise StatusError("docs/intake/ holds more than one plan; the report takes exactly one")
    name = f"docs/intake/{plans[0].name}"
    data = read_json(plans[0], name)
    bad = StatusError(f"{name} is not a plan intake.py wrote")
    if not isinstance(data, dict) or data.get("schema") != 1 or data.get("project_id") != project_id:
        raise StatusError(f"{name} is not a plan intake.py wrote for usage.project_id {project_id}")
    handoff, epics, phases, mapping = (data.get(k) for k in ("handoff", "epics", "phases", "mapping"))
    if not isinstance(handoff, dict) or not (_count(handoff.get("product_version")) and handoff["product_version"] >= 1) \
            or ("thread" in handoff and not (isinstance(handoff["thread"], str) and HANDOFF_THREAD.fullmatch(handoff["thread"]))) \
            or not isinstance(epics, list) or not isinstance(phases, list) or not isinstance(mapping, dict):
        raise bad

    def dated(item: object, keys: tuple[str, ...]) -> bool:
        return isinstance(item, dict) and all(k in item for k in keys) \
            and all(isinstance(item[k], str) and real_date(item[k]) for k in ("planned_start", "planned_end")) \
            and (item["milestone"] is None or isinstance(item["milestone"], str))

    if not all(dated(e, ("id", "milestone", "planned_start", "planned_end")) and isinstance(e["id"], str)
               and EPIC.fullmatch(e["id"]) for e in epics) \
            or not all(dated(p, ("epics", "milestone", "planned_start", "planned_end")) and isinstance(p["epics"], list)
                       and all(isinstance(e, str) and EPIC.fullmatch(e) for e in p["epics"]) for p in phases) \
            or not all(isinstance(k, str) and isinstance(v, str) for k, v in mapping.items()):
        raise bad
    return data


# --- the manifests ---

def manifest_text(gates, issue: int, ended: bool) -> tuple[str, str | None] | None:
    """(manifest text, branch ref it came from or None for this checkout), or None when it is found nowhere."""
    path = gates.manifest_path(issue)
    if capture.plain(path, "file"):
        return path.read_text(encoding="utf-8-sig"), None
    if ended:
        raise StatusError(f"issue #{issue} has ended, but docs/ai/pipeline/{issue}.md is not in this checkout; "
                          "pull main first")
    refs = capture.git("for-each-ref", "--format=%(refname)", f"refs/heads/issue/{issue}-*",
                       f"refs/remotes/origin/issue/{issue}-*", cwd=ROOT)
    for ref in refs.stdout.split() if refs.returncode == 0 else []:
        shown = capture.git("show", "--no-textconv", f"{ref}:docs/ai/pipeline/{issue}.md", cwd=ROOT)
        if shown.returncode == 0:
            return shown.stdout, ref
    return None


def manifest_meta(text: str, issue: int, gates) -> dict[str, str]:
    meta: dict[str, str] = {}
    for line in text.splitlines():
        if line.startswith("|"):  # the gate table: the lines gates.py init writes all sit above it
            break
        m = META.fullmatch(line)
        if m:
            if m.group(1) in meta:
                raise StatusError(f"docs/ai/pipeline/{issue}.md gives {m.group(1)} twice")
            meta[m.group(1)] = m.group(2)
    if "Kind" in meta and meta["Kind"] not in claudecfg.LABEL_KINDS \
            or "Phase" in meta and not PHASE_ID.fullmatch(meta["Phase"]) \
            or "Epic" in meta and not gates.EPIC.fullmatch(meta["Epic"]):
        raise StatusError(f"docs/ai/pipeline/{issue}.md has a Kind, Phase or Epic line gates.py init does not write")
    return meta


def counted_findings(gates, text: str, issue: int) -> tuple[dict, int]:
    """The G6 findings line, or zero findings and zero re-checks when the G6 review did not run (skipped, or not yet
    done on an open issue); two lines stop the report."""
    lines = gates.findings_lines(text)
    if len(lines) > 1:
        raise StatusError(f"issue #{issue}: the G6 review has more than one findings line")
    if not lines:
        return {"high": 0, "medium": 0, "low": 0, "info": 0}, 0
    high, medium, low, info, rechecks = (int(g) for g in lines[0].groups())
    return {"high": high, "medium": medium, "low": low, "info": info}, rechecks


def final_gates(gates, issue: int) -> tuple[list[dict], str]:
    """The gate engine's final state for a finished issue, and the G6 review's text ("" when G6 was skipped). A
    passed orchestrator gate is PASS, an approved skip SKIPPED with its reason, an agent gate its verdict line; any
    other state has no value in the format and stops the report."""
    rows = gates.read_rows(issue)
    out, g6_text = [], ""
    for gate, _, _ in gates.GATES:
        row = rows.get(gate)
        stop = StatusError(f"issue #{issue} has ended, but its {gate} gate is in no state the report can give")
        if row is None:
            raise stop
        if row["status"] == "skipped":
            ok, _ = gates.check_gate(issue, gate, row)
            reason = next((f.strip()[7:].strip() for f in row["note"].split(";") if f.strip().startswith("reason:")), "")
            if not ok or not reason:
                raise stop
            out.append({"gate": gate, "verdict": "SKIPPED", "reason": reason})
            continue
        if gate in gates.ORCHESTRATOR_GATES:
            if row["status"] != "passed" or not row["note"]:
                raise stop
            out.append({"gate": gate, "verdict": "PASS"})
            continue
        ok, detail = gates.check_gate(issue, gate, row)
        artifact = row["artifact"]
        readable = artifact and not gates.artifact_path_problem(artifact, gate) and capture.plain(ROOT / artifact, "file")
        text = (ROOT / artifact).read_text(encoding="utf-8-sig") if readable else ""
        lines = gates.verdict_lines(text, gate)
        if not ok and not (len(lines) == 1 and lines[0].group(2) == "BLOCK" and int(lines[0].group(3)) == issue):
            raise StatusError(f"issue #{issue} has ended, but its {gate} gate is in no state the report can give "
                              f"({printable(detail)})")
        verdict, rest = lines[0].group(2), lines[0].group(4)
        entry = {"gate": gate, "verdict": verdict}
        if verdict == "N/A":
            entry["reason"] = rest.split("reason:", 1)[1].strip().strip("|").strip()
        out.append(entry)
        if gate == "G6":
            # check_gate stops at a BLOCK before it looks for the findings line: a review that ran always has one
            if len(gates.findings_lines(text)) != 1:
                raise StatusError(f"issue #{issue} has ended, but its G6 review has no single findings line, so its "
                                  "findings cannot be counted")
            g6_text = text
    return out, g6_text


def printable(text: str, cap: int | None = 200) -> str:
    """Text from a file or another module reduced to printable ASCII and capped, for the terminal."""
    return "".join(c if " " <= c <= "~" else "?" for c in text)[:cap]


def open_findings(gates, text: str, ref: str | None, issue: int) -> tuple[dict, int]:
    """An open issue's findings so far, from its G6 artifact where the manifest came from."""
    for line in text.splitlines():
        m = gates.ROW.match(line.strip())
        if m and m.group(1) == "G6":
            artifact = m.group(4).strip().strip("`")
            break
    else:
        artifact = ""
    if not artifact or gates.artifact_path_problem(artifact, "G6"):
        return counted_findings(gates, "", issue)
    if ref is None:
        path = ROOT / artifact
        review = path.read_text(encoding="utf-8-sig") if capture.plain(path, "file") else ""
    else:
        shown = capture.git("show", "--no-textconv", f"{ref}:{artifact}", cwd=ROOT)
        review = shown.stdout if shown.returncode == 0 else ""
    return counted_findings(gates, review, issue)


# --- `report` ---

def build_report(project: dict, folder: Path, gates, phase_id: str, trigger: str) -> tuple[dict, list[int]]:
    phases = phases_of(project)
    if phase_id not in phases:
        raise StatusError(f"phase {phase_id} is not in packs.status.phases")
    milestone = phases[phase_id]
    project_id = project["usage"]["project_id"]
    probes = literals.probes_from(project)
    exits = read_phase_exits(folder, probes)
    exit_record = exits.get(phase_id)
    if trigger == "phase-exit" and exit_record is None:
        raise StatusError(f"phase {phase_id} has no exit record; record the owner's confirmation first with "
                          f"status.py phase-exit {phase_id}")
    captures = read_captures(folder, project_id)
    issues, not_found, no_phase = [], [], []
    for number, started in sorted(read_starts(folder).items()):
        ended = read_issue_end(folder, number, probes)
        found = manifest_text(gates, number, ended is not None)
        if found is None:
            not_found.append(number)
            continue
        text, ref = found
        meta = manifest_meta(text, number, gates)
        if meta.get("Phase") != phase_id:
            if "Phase" not in meta:
                no_phase.append(number)
            continue
        if ended is not None:
            gate_list, review = final_gates(gates, number)
            findings, rechecks = counted_findings(gates, review, number)
        else:
            gate_list = []
            findings, rechecks = open_findings(gates, text, ref, number)
        usage = combine([c for c in captures if c["issue"] == number],
                        "no capture was made for the issue" if ended else
                        "the issue is open; its usage is captured when it ends")
        if started["run_location"] == "sandbox" and usage["status"] == "complete":
            usage = {**usage, "status": "partial",
                     "missing_reason": "the issue ran in a sandbox, whose records are not on this machine"}
        entry = {"number": number, "epic_id": meta.get("Epic")}
        if "Kind" in meta:
            entry["kind"] = meta["Kind"]
        entry.update({"start": started["start"], "end": ended["end"] if ended else None,
                      "run_location": started["run_location"], "agent_usage": usage, "gates": gate_list,
                      "findings": findings, "rechecks": rechecks,
                      "missing_inputs": ended["missing_inputs"] if ended else []})
        issues.append(entry)
    actual_start = min((i["start"] for i in issues), key=capture.parse_time, default=None)
    actual_end = exit_record["actual_end"] if exit_record else None
    plan = read_plan(project_id)
    report = {"format_version": FORMAT_VERSION, "project_id": project_id,
              "factory": project["packs"]["status"]["factory"], "report_date": date.today().isoformat(),
              "trigger": trigger}
    notes = []
    if not_found:
        notes.append("not reported, no manifest found in this checkout or an issue branch: "
                     + ", ".join(f"#{n}" for n in not_found))
    if no_phase:
        notes.append("not reported, the manifest has no Phase line (made before the label set): "
                     + ", ".join(f"#{n}" for n in no_phase))
    if plan and "thread" in plan["handoff"]:
        report["handoff"] = {"thread": plan["handoff"]["thread"], "product_version": plan["handoff"]["product_version"]}
    elif plan:
        notes.append("no handoff in the report: the plan records no handoff thread (intake.py map <slug> --thread)")
    planned = planned_dates(plan, milestone)
    report["phase"] = {"id": phase_id, "milestone": milestone,
                       "status": "done" if exit_record else "in-progress" if issues else "not-started",
                       "planned_start": planned[0], "planned_end": planned[1],
                       "actual_start": actual_start, "actual_end": actual_end}
    report["epics"] = epic_entries(plan, milestone, issues, exits)
    report["issues"] = issues
    report["unattributed_usage"] = unattributed(captures, actual_start, actual_end)
    if exit_record and exit_record["lessons"]:
        report["lessons"] = exit_record["lessons"]
    return report, notes


def planned_dates(plan: dict | None, milestone: str) -> tuple[str | None, str | None]:
    """The roadmap entry whose milestone is the phase's; else the entries without a milestone that the accepted
    mapping places in it (earliest start, latest end); else none."""
    if plan is None:
        return None, None
    entries = [p for p in plan["phases"] if p["milestone"] == milestone]
    if not entries:
        mapped = {epic for epic, m in plan["mapping"].items() if m == milestone}
        entries = [p for p in plan["phases"] if p["milestone"] is None and mapped & set(p["epics"])]
    if not entries:
        return None, None
    return min(p["planned_start"] for p in entries), max(p["planned_end"] for p in entries)


def epic_entries(plan: dict | None, milestone: str, issues: list[dict], exits: dict[str, dict]) -> list[dict]:
    """Every plan epic of the phase (its milestone, or the accepted mapping's), then every earlier epic an issue in
    this report serves, with only this report's issues."""
    served = {i["epic_id"] for i in issues if i["epic_id"]}
    if plan is None:
        if served:
            raise StatusError("an issue names an epic, but the project has no plan (docs/intake/<slug>.json)")
        return []
    by_id = {e["id"]: e for e in plan["epics"]}
    unknown = sorted(served - set(by_id))
    if unknown:
        raise StatusError(f"an issue names {unknown[0]}, which the plan does not hold")
    chosen = [e for e in plan["epics"] if plan["mapping"].get(e["id"], e["milestone"]) == milestone]
    chosen += [by_id[e] for e in sorted(served) if by_id[e] not in chosen]
    done = {e for record in exits.values() for e in record["done"]}
    descoped = {e for record in exits.values() for e in record["descoped"]}
    out = []
    for epic in chosen:
        own = [i for i in issues if i["epic_id"] == epic["id"]]
        if epic["id"] in descoped:
            status = "descoped"
        elif epic["id"] in done and all(i["end"] for i in own):
            status = "done"
        else:
            status = "in-progress" if own else "not-started"
        entry = {"epic_id": epic["id"], "status": status, "issues": sorted(i["number"] for i in own),
                 "planned_start": epic["planned_start"], "planned_end": epic["planned_end"]}
        changes = sorted(({k: c[k] for k in ("date", "change", "thread") if k in c}
                          for record in exits.values() for c in record["scope_changes"] if c["epic"] == epic["id"]),
                         key=lambda c: c["date"])
        if changes:
            entry["scope_changes"] = changes
        out.append(entry)
    return out


def unattributed(captures: list[dict], start: str | None, end: str | None) -> dict:
    """Usage on no issue branch in the phase window: from the phase's actual start to its exit, or to now. A capture
    belongs to the window when it was made in it."""
    if start is None:
        return {"status": "missing", "missing_reason": "no issue of the phase has started, so it has no window",
                "by_model": []}
    low = capture.parse_time(start)
    high = capture.parse_time(end) if end else datetime.now(timezone.utc)
    window = [c for c in captures if low <= c["_at"] <= high]
    if not window:
        return {"status": "missing", "missing_reason": "no capture was made in the phase window", "by_model": []}
    outside = [c for c in window if c["issue"] is None]
    # a finish writes no capture for usage outside an issue when it finds none, even when a gap (an unreadable record
    # file, expired records, another account) hid some: the gaps of every finish in the window apply here too
    gaps = []
    for c in window:
        if c["issue"] is not None and c["usage"]["status"] != "complete":
            reasons = c["usage"]["missing_reason"].split("; ")
            if len(c["usage"]["missing_reason"]) >= 200:  # its last reason may be a fragment: never copied as text
                reasons[-1] = CUT_REASON
            gaps += [reason for reason in reasons if reason not in ISSUE_ONLY_REASONS]
    usage = combine(outside, "") if outside else {"status": "complete", "by_model": []}
    if gaps:
        reasons = [r for r in usage.get("missing_reason", "").split("; ") if r]
        reasons += [r for r in dict.fromkeys(gaps) if r not in reasons]
        usage = {"status": "partial" if usage["by_model"] else "missing", "by_model": usage["by_model"],
                 "missing_reason": "; ".join(reasons)}
    return usage


def text_fields(report: dict):
    for issue in report["issues"]:
        for gate in issue["gates"]:
            if "reason" in gate:
                yield f"issue #{issue['number']} {gate['gate']} reason", gate["reason"]
        for i, item in enumerate(issue["missing_inputs"]):
            yield f"issue #{issue['number']} missing input {i + 1}", item["what"]
    for epic in report["epics"]:
        for i, change in enumerate(epic.get("scope_changes", [])):
            yield f"{epic['epic_id']} scope change {i + 1}", change["change"]
    for i, lesson in enumerate(report.get("lessons", [])):
        yield f"lesson {i + 1}", lesson


def string_leaves(value: object, where: str):
    if isinstance(value, str):
        yield where, value
    elif isinstance(value, dict):
        for key, item in value.items():
            yield from string_leaves(item, f"{where}.{key}")
    elif isinstance(value, list):
        for i, item in enumerate(value):
            yield from string_leaves(item, f"{where}[{i}]")


REPORT_FILE = re.compile(r"report-([0-9]{4}-[0-9]{2}-[0-9]{2})-([0-9.]+?)(?:-r([2-9]|[1-9][0-9]+))?\.json")


def report_name(folder: Path, phase_id: str, correction: bool, today: str) -> str:
    """report-<date>-<id>.json; a correction corrects the phase's latest report: the next -rN under that report's
    first file's date."""
    earlier = []
    for path in folder.glob("report-*.json"):
        m = REPORT_FILE.fullmatch(path.name)
        if m and m.group(2) == phase_id:
            earlier.append((m.group(1), int(m.group(3) or 1)))
    if not correction:
        return f"report-{today}-{phase_id}.json"
    if not earlier:
        raise StatusError(f"phase {phase_id} has no report to correct; leave out --correction")
    latest = max(d for d, _ in earlier)
    number = max(n for d, n in earlier if d == latest) + 1
    return f"report-{latest}-{phase_id}-r{number}.json"


def report(project: dict, folder: Path, gates, phase_id: str, trigger: str, correction: bool) -> str:
    data, notes = build_report(project, folder, gates, phase_id, trigger)
    probes = literals.probes_from(project)
    for where, value in text_fields(data):
        free_text(value, where, probes)
    for where, value in string_leaves(data, "$"):  # the milestone, usage reasons, model and agent names too
        found = literals.scan_text(value, probes)
        if found:
            raise StatusError(f"{where} holds a {found[0][1]} literal; the report is shared, so nothing was written")
    errors = schema_errors(data, SCHEMA) + cross_check_errors(data)
    if errors:
        raise StatusError("the report fails its checks, so nothing was written:\n  " + "\n  ".join(errors[:20]))
    name = report_name(folder, phase_id, correction, data["report_date"])
    write_record(folder, name, data, replace=False, hint="give --correction for a corrected report")
    digest = hashlib.sha256((folder / name).read_bytes()).hexdigest()
    lines = [f"status: wrote {name} ({len(data['issues'])} issue(s), {len(data['epics'])} epic(s))",
             f"status: SHA-256 {digest}",
             f"status: in the exchange it is {name.removeprefix('report-')}; the owner approves it first"]
    lines += [f"status: {note}" for note in notes]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="cmd", required=True)
    finished = sub.add_parser("end")
    finished.add_argument("issue", type=int)
    finished.add_argument("--missing", action="append", default=[])
    finished.add_argument("--replace", action="store_true")
    leave = sub.add_parser("phase-exit")
    leave.add_argument("phase")
    leave.add_argument("--scope-change", dest="changes", action="append", default=[])
    leave.add_argument("--lesson", dest="lessons", action="append", default=[])
    leave.add_argument("--done", action="append", default=[])
    leave.add_argument("--descoped", action="append", default=[])
    leave.add_argument("--replace", action="store_true")
    made = sub.add_parser("report")
    made.add_argument("phase")
    made.add_argument("--trigger", required=True, choices=("phase-exit", "on-request"))
    made.add_argument("--correction", action="store_true")
    args = parser.parse_args(argv)
    for stream in (sys.stdout, sys.stderr):  # free text is any Unicode; a Windows pipe defaults to a code page
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    if args.cmd == "end" and args.issue < 1 or args.cmd != "end" and not PHASE_ID.fullmatch(args.phase):
        print("status: the issue number is 1 or more; a phase id is <n> or <n>.<m>", file=sys.stderr)
        return 2
    try:
        project = claudecfg.load_project(ROOT / ".claude" / "project.json")
    except claudecfg.ProjectConfigError as exc:
        print(f"status: {exc}", file=sys.stderr)
        return 2
    if "status" not in project["packs"] or "labels" not in project:
        print("status: the status pack needs packs.status and the label set (labels) in .claude/project.json",
              file=sys.stderr)
        return 2
    try:
        folder = capture.capture_folder(project)
        if args.cmd == "end":
            print(end(project, folder, args.issue, args.missing, args.replace))
        elif args.cmd == "phase-exit":
            print(phase_exit(project, folder, args.phase, args.changes, args.lessons, args.done, args.descoped,
                             args.replace))
        else:
            try:
                gates = _load("_norma_gates", SELF.parent / "gates.py")
            except Exception as exc:  # noqa: BLE001  gates.py stops on a project or boundaries file it cannot use
                print(f"status: gates.py cannot be loaded: {printable(str(exc))}", file=sys.stderr)
                return 2
            print(report(project, folder, gates, args.phase, args.trigger, args.correction))
    except (StatusError, capture.CaptureError) as exc:  # a message can name a file from the capture folder
        print("status: " + "\n".join(printable(line, None) for line in str(exc).split("\n")), file=sys.stderr)
        return 1
    except UnicodeDecodeError:
        print("status: a manifest, gate artifact or record is not UTF-8 text; nothing was written", file=sys.stderr)
        return 1
    except OSError as exc:
        print(f"status: stopped on a file error ({type(exc).__name__}); nothing was written", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
