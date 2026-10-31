#!/usr/bin/env python3
"""The usage capture (ADR 0006): an issue's Claude Code development usage, in counts only.

Usage (from the repository root, by the main session; never by a listed agent):
  python .claude/scripts/capture.py start <n> --where this-machine|sandbox
  python .claude/scripts/capture.py finish <n> [--other-accounts]

`start` (at G0) records the issue's start time and where it runs, as start-<n>.json in the capture folder
(usage.capture_folder in .claude/project.json). `finish` (after the owner confirms the merge) reads Claude Code's
session records for this repository and writes capture-<id>.json, format 1.0.0: usage per model, source and agent for
issue <n>, and a second capture, with issue null, for usage on branches that belong to no issue. It reads only the
usage fields of assistant records, counts each message id once (ledger.jsonl) and never prints a value from a record.
A capture is linked into place and never overwritten. A gap is a `partial` or `missing` status with a reason, never a
zero. The capture folder sits in the main checkout, shared by every worktree, and must be ignored by git as a folder;
the script writes nothing otherwise. Session records live per user account: when another account worked on the issue,
--other-accounts marks the capture `partial`, and the same finish under that account captures its share.

Each worktree a start or finish sees is remembered (worktrees.json), so its records are still read after it is
removed; a worktree made and removed between the start and the finish is seen only if a start ran in it.

Exit 0 = done; 1 = stopped, with the reason: git could not be run; there is no main checkout, or the folder is not a
plain folder or not git-ignored as a folder; another capture is running; a capture name exists; the folder cannot hold
a second link; the ledger or worktrees.json could not be written; a capture's temporary file could not be removed; or
another file error in the folder (each capture written before the stop is printed); 2 = usage, or
.claude/project.json cannot be used.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import argparse
import contextlib
import importlib.util
import json
import os
import re
import secrets
import stat
import subprocess
from datetime import datetime, timedelta, timezone
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

FORMAT_VERSION = "1.0.0"
MAX_FILE_BYTES = 512 * 1024 * 1024  # a session record file larger than this is skipped (the capture is then partial)
MAX_LINE_BYTES = 16 * 1024 * 1024
MAX_HEAD_BYTES = 64 * 1024 * 1024  # how far into a session file its `cwd` is looked for
MAX_META_BYTES = 1024 * 1024
MAX_TOKENS = 10 ** 12  # a count above this is not a real count
MAX_ENTRIES = 50
RETENTION_DAYS = 30  # Claude Code removes session records older than this by default
MODEL = re.compile(r"[A-Za-z0-9._:/@+-]+(?:\[[a-z0-9]{1,8}\])?")
AGENT = re.compile(r"[A-Za-z0-9._-]+(?::[A-Za-z0-9._-]+)?")
MESSAGE_ID = re.compile(r"[A-Za-z0-9_-]{1,200}")
ISSUE_BRANCH = re.compile(r"issue/([1-9][0-9]{0,8})-")
SYNTHETIC = "<synthetic>"  # the model name of a message Claude Code writes itself
TOKEN_FIELDS = (("input_tokens", "input_tokens"), ("output_tokens", "output_tokens"),
                ("cache_read_input_tokens", "cache_read_tokens"), ("cache_creation_input_tokens", "cache_creation_tokens"))


class CaptureError(Exception):
    pass


def git(*args: str, cwd: Path = ROOT) -> subprocess.CompletedProcess:
    try:
        return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, encoding="utf-8", check=False,
                              timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        raise CaptureError("git could not be run; nothing written") from None


def utc(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_time(text: object) -> datetime | None:
    """A record's time in UTC, or None for anything that is not a plausible time (a year far off cannot be printed)."""
    if not isinstance(text, str) or len(text) > 40:
        return None
    try:
        moment = datetime.fromisoformat(text.replace("Z", "+00:00"))
        if moment.tzinfo is None:
            return None
        moment = moment.astimezone(timezone.utc)
    except (ValueError, OverflowError):
        return None
    return moment if 2000 <= moment.year <= 9998 else None


def plain(path: Path, kind: str) -> bool:
    """Whether a path is itself a regular file ("file") or folder ("dir"): never a link, junction or other reparse
    point. Checked without following one, so a link to a network share is never contacted."""
    try:
        info = os.lstat(path)
    except (OSError, ValueError):
        return False
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        return False
    return stat.S_ISDIR(info.st_mode) if kind == "dir" else stat.S_ISREG(info.st_mode)


def main_checkout() -> Path:
    """The main checkout, which holds the capture folder for every worktree: one ledger and one lock per repository,
    so two worktrees never count a message twice, and removing a worktree never removes a capture."""
    run = git("rev-parse", "--path-format=absolute", "--git-common-dir")
    common = Path(run.stdout.strip()) if run.returncode == 0 and run.stdout.strip() else None
    if common is None or common.name.lower() != ".git" or not plain(common.parent, "dir"):
        raise CaptureError("the repository has no main checkout that is a plain folder (not a link, junction or "
                           "cloud placeholder) to hold the capture folder; nothing written")
    return common.parent


def capture_folder(project: dict) -> Path:
    """The capture folder in the main checkout, after checking that git ignores the folder itself: no line can then
    re-include a file in it, and a capture leaves the machine only by the copy job."""
    rel = project["usage"]["capture_folder"]
    base = main_checkout()
    refused = (f"usage.capture_folder {rel} is not ignored by git as a folder: add /{rel}/ to .gitignore; "
               "nothing written")
    if git("check-ignore", "-q", f"{rel}/probe", cwd=base).returncode != 0:
        raise CaptureError(refused)
    path = base
    for part in rel.split("/"):
        path = path / part
        if os.path.lexists(path) and not plain(path, "dir"):
            raise CaptureError(f"usage.capture_folder {rel} is not a plain folder (a link or a file); nothing written")
    created = not os.path.lexists(path)
    path.mkdir(parents=True, exist_ok=True)
    if git("check-ignore", "-q", rel, cwd=base).returncode != 0:  # the folder, now that it exists
        if created:
            with contextlib.suppress(OSError):
                path.rmdir()
        raise CaptureError(refused)
    return path


# --- reading Claude Code's session records ---

def same_path(path: str | os.PathLike) -> str:
    """A path as text, for comparison: never resolved, so a path from a record never touches the file system."""
    return os.path.normcase(os.path.normpath(path))


def repository_roots() -> list[str]:
    """This repository's root and its worktrees, as the paths a session's `cwd` would hold, resolved and as given."""
    roots = [ROOT]
    listing = git("worktree", "list", "--porcelain").stdout
    roots += [Path(line[len("worktree "):]) for line in listing.splitlines() if line.startswith("worktree ")]
    return sorted({same_path(form) for root in roots for form in (root, os.path.realpath(root))})


def belongs_here(cwd: object, roots: list[str]) -> bool:
    """Whether a record's `cwd` is a repository root or inside one. Only a local absolute path is compared: a network
    path (\\\\<host>\\<share>, //<host>/<share>) is never taken, so no share is ever contacted."""
    if not isinstance(cwd, str) or not 0 < len(cwd) <= 4096 or "\x00" in cwd:
        return False
    if cwd.startswith(("\\\\", "//")) or not os.path.isabs(cwd):
        return False
    path = same_path(cwd)
    return any(path == root or path.startswith(root.rstrip(os.sep) + os.sep) for root in roots)


ANY_NAME = re.compile(r".+")
SESSION_FILE = re.compile(r".+\.jsonl")
AGENT_FILE = re.compile(r"agent-[A-Za-z0-9_-]{1,100}\.jsonl")


def listed(folder: Path, kind: str, names: re.Pattern[str]) -> list[Path]:
    """The plain files or folders directly in a plain folder whose names match, listed without following a link."""
    if not plain(folder, "dir"):
        return []
    try:
        with os.scandir(folder) as found:
            entries = sorted(entry.name for entry in found)
    except OSError:
        return []
    return [folder / name for name in entries if names.fullmatch(name) and plain(folder / name, kind)]


def lines(fh, budget: int | None = None):
    """The lines of a file, each at most MAX_LINE_BYTES: a longer line is consumed in pieces and given as None. With a
    budget, reading stops once that many bytes are read, even inside a line."""
    while raw := fh.readline(MAX_LINE_BYTES + 1):
        if len(raw) <= MAX_LINE_BYTES or raw.endswith(b"\n"):
            yield raw
        else:
            while (rest := fh.readline(MAX_LINE_BYTES)) and not rest.endswith(b"\n"):
                if budget is not None and fh.tell() > budget:
                    return
            yield None
        if budget is not None and fh.tell() > budget:
            return


def first_cwd(path: Path) -> object:
    """The `cwd` of the first record that has one, reading at most the first 200 lines or MAX_HEAD_BYTES of a session
    file, whatever its size: an oversized file of this repository is still found, and reported as a gap."""
    try:
        with path.open("rb") as fh:
            for count, raw in enumerate(lines(fh, MAX_HEAD_BYTES)):
                if count >= 200:
                    break
                if raw is None or b'"cwd"' not in raw:
                    continue
                try:
                    record = json.loads(raw)
                except (ValueError, RecursionError):
                    continue
                if isinstance(record, dict) and "cwd" in record:
                    return record["cwd"]
    except OSError:
        return None
    return None


def record_files(config: Path, roots: list[str]) -> list[tuple[Path, str | None]]:
    """The session record files of this repository, each with its subagent role, if any. Claude Code's folder names
    can collide for two paths, so each session is taken by its own `cwd`, and a subagent's file by its own or by its
    session's. Only plain folders and files are listed; a link or junction anywhere is passed over."""
    files = []
    for folder in listed(config / "projects", "dir", ANY_NAME):
        sessions = {f.name[:-len(".jsonl")] for f in listed(folder, "file", SESSION_FILE)
                    if belongs_here(first_cwd(f), roots)}
        files += [(folder / f"{name}.jsonl", None) for name in sorted(sessions)]
        for session in listed(folder, "dir", ANY_NAME):
            for path in listed(session / "subagents", "file", AGENT_FILE):
                if session.name in sessions or belongs_here(first_cwd(path), roots):
                    files.append((path, subagent_role(path)))
    return files


def subagent_role(record_file: Path) -> str | None:
    """`agentType` from the meta file beside a subagent's record file, when it is a valid agent name."""
    meta = record_file.with_name(record_file.name[:-len(".jsonl")] + ".meta.json")
    try:
        if not plain(meta, "file") or os.lstat(meta).st_size > MAX_META_BYTES:
            return None
        role = json.loads(meta.read_bytes()).get("agentType")
    except (OSError, ValueError, RecursionError, AttributeError):
        return None
    return role if isinstance(role, str) and len(role) <= 100 and AGENT.fullmatch(role) else None


def usage_entry(record: object, agent: str | None) -> dict | None:
    """The fields the capture keeps from one assistant record, or None for any other record. Nothing else is read.
    A record outside the caps gives {"problem", "issue"} and, when its message id is usable, "id" and "skip"."""
    if not isinstance(record, dict) or record.get("type") != "assistant":
        return None
    message = record.get("message")
    if not isinstance(message, dict) or not isinstance(message.get("usage"), dict):
        return None
    branch = record.get("gitBranch")
    match = ISSUE_BRANCH.match(branch) if isinstance(branch, str) else None
    issue = int(match.group(1)) if match else None
    usage, message_id = message["usage"], message.get("id")
    if not isinstance(message_id, str) or not MESSAGE_ID.fullmatch(message_id):
        return {"problem": "a record without a usable message id", "issue": issue}
    counts = {}
    for source, target in TOKEN_FIELDS:
        value = usage.get(source, 0)
        if type(value) is not int or not 0 <= value <= MAX_TOKENS:
            return {"problem": "a token count outside its bounds", "issue": issue, "id": message_id, "skip": True}
        counts[target] = value
    split = usage.get("cache_creation")
    if isinstance(split, dict):
        five, hour = split.get("ephemeral_5m_input_tokens"), split.get("ephemeral_1h_input_tokens")
        if type(five) is int and type(hour) is int and five >= 0 and hour >= 0 \
                and five + hour == counts["cache_creation_tokens"]:
            counts["cache_creation_5m_tokens"], counts["cache_creation_1h_tokens"] = five, hour
    model = message.get("model")
    if model == SYNTHETIC and not any(counts.values()):
        return None  # a message Claude Code makes itself (an API error, for example), with no usage
    if not isinstance(model, str) or len(model) > 100 or not MODEL.fullmatch(model):
        return {"problem": "a model name outside the caps", "issue": issue, "id": message_id, "skip": True}
    return {"id": message_id, "model": model, "source": "subagent" if record.get("isSidechain") is True else "main",
            "agent": agent, "time": parse_time(record.get("timestamp")), "issue": issue, "counts": counts}


ANY_ISSUE = "any"  # a problem that no branch can be named for, such as an unreadable file: it marks every capture


def read_usage(files: list[tuple[Path, str | None]], problems: set[tuple[object, str]]) -> dict[str, dict]:
    """{message id: entry} for every assistant record with usage in these files, each message once. Problems are
    added as (issue, reason): the issue the record's branch names, None for no issue, or ANY_ISSUE."""
    entries: dict[str, dict] = {}
    for path, agent in files:
        try:
            if os.lstat(path).st_size > MAX_FILE_BYTES:
                problems.add((ANY_ISSUE, "a session record file over the size cap was skipped"))
                continue
            with path.open("rb") as fh:
                for raw in lines(fh):
                    if raw is None:
                        problems.add((ANY_ISSUE, "a session record over the length cap was skipped"))
                        continue
                    if b'"usage"' not in raw:
                        continue
                    try:
                        entry = usage_entry(json.loads(raw), agent)
                    except (ValueError, RecursionError):
                        continue
                    if entry is None:
                        continue
                    if "id" not in entry:
                        problems.add((entry["issue"], entry["problem"]))
                        continue
                    entries.setdefault(entry["id"], entry)  # one message spans several records with one usage
        except OSError:
            problems.add((ANY_ISSUE, "a session record file could not be read"))
    return entries


# --- the capture file ---

def by_model(entries: list[dict], problems: set[str]) -> list[dict]:
    groups: dict[tuple, dict] = {}
    for entry in entries:
        key = (entry["model"], entry["source"], entry["agent"] if entry["source"] == "subagent" else None)
        group = groups.setdefault(key, {"counts": {}, "split": True})
        for name, value in entry["counts"].items():
            group["counts"][name] = group["counts"].get(name, 0) + value
        group["split"] = group["split"] and "cache_creation_5m_tokens" in entry["counts"]
    rows = []
    for (model, source, agent), group in sorted(groups.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2] or "")):
        row = {"model": model, "source": source}
        if agent:
            row["agent"] = agent
        row.update({name: group["counts"].get(name, 0) for _, name in TOKEN_FIELDS})
        if group["split"]:
            row["cache_creation_5m_tokens"] = group["counts"]["cache_creation_5m_tokens"]
            row["cache_creation_1h_tokens"] = group["counts"]["cache_creation_1h_tokens"]
        rows.append(row)
    if len(rows) > MAX_ENTRIES:
        problems.add(f"more than {MAX_ENTRIES} model, source and agent entries")
        rows = sorted(rows, key=lambda r: -(r["input_tokens"] + r["output_tokens"]))[:MAX_ENTRIES]
    return rows


def write_capture(folder: Path, project_id: str, issue: int | None, entries: list[dict], reasons: list[str],
                  problems: set[str]) -> tuple[str, str]:
    """Write one capture and count its messages; returns (file name, status). Linked into place, so an existing name
    stops it; the ledger takes the ids the moment the capture exists, before the temporary name is removed."""
    now = datetime.now(timezone.utc)
    captured_at = utc(now)
    capture_id = now.strftime("%Y%m%dT%H%M%SZ") + "-" + secrets.token_hex(4)
    rows = by_model([e for e in entries if not e.get("skip")], problems)
    reasons = reasons + sorted(problems)
    times = [e["time"] for e in entries if not e.get("skip") and e.get("time")]
    if not rows:
        status, window = "missing", None
        reasons = reasons or ["no usage was found for the issue" if issue else "no usage was found"]
    else:
        status = "partial" if reasons else "complete"
        start, end = min(times, default=now), min(max(times, default=now), now)
        window = {"from": utc(min(start, end)), "to": utc(end)}
    usage = {"status": status, "by_model": rows}
    if status != "complete":
        usage["missing_reason"] = "; ".join(reasons)[:200]
    capture = {"format_version": FORMAT_VERSION, "project_id": project_id, "capture_id": capture_id,
               "captured_at": captured_at, "issue": issue, "window": window, "usage": usage}
    name = f"capture-{capture_id}.json"
    temporary = folder / f"tmp-{capture_id}.json"  # a name the copy job never matches
    with open(temporary, "x", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(capture, indent=2) + "\n")
    try:
        os.link(temporary, folder / name)  # fails when the name exists: a capture is never overwritten
    except OSError as exc:
        with contextlib.suppress(OSError):
            temporary.unlink()
        if isinstance(exc, FileExistsError):
            raise CaptureError(f"{name} exists; nothing overwritten") from None
        raise CaptureError("the capture folder cannot hold a second link to a file; nothing written") from None
    try:
        add_to_ledger(folder, [e["id"] for e in entries])
    except OSError as exc:
        torn = isinstance(exc, LedgerTorn)
        try:
            (folder / name).unlink()
        except OSError:  # the temporary name stays, so the capture keeps two links and the copy job never takes it
            raise CaptureError(f"the ledger could not be written and {name} could not be removed; remove it and "
                               f"{temporary.name}, then finish again" + (TORN if torn else "")) from None
        with contextlib.suppress(OSError):
            temporary.unlink()
        raise CaptureError(f"the ledger could not be written, so {name} was removed again"
                           + (TORN if torn else "; nothing counted")) from None
    try:
        temporary.unlink()
        single = os.lstat(folder / name).st_nlink == 1
    except OSError:
        single = False
    if not single:  # the copy job refuses a file with a second link
        raise CaptureError(f"{name} was written and counted, but {temporary.name} could not be removed; remove it "
                           "once nothing holds it open, and the copy job then takes the capture")
    return name, status


def read_ledger(folder: Path) -> set[str]:
    ledger = folder / "ledger.jsonl"
    if not ledger.is_file():
        return set()
    counted = set()
    for line in ledger.read_text(encoding="utf-8").splitlines():
        try:
            value = json.loads(line).get("message_id")
        except (ValueError, RecursionError, AttributeError):
            continue
        if isinstance(value, str):
            counted.add(value)
    return counted


class LedgerTorn(OSError):
    """A failed append that could not be cut back: the ledger may name messages that no capture holds."""


TORN = ("; ledger.jsonl could not be cut back and may name some of its messages, which no capture then counts: "
        "tell the owner")


def add_to_ledger(folder: Path, ids: list[str]) -> None:
    """Append the ids in one write. A last line an earlier stop left unfinished is ended first, so no id joins it; a
    failed write is cut back to the ledger's length before and the OSError raised, or LedgerTorn when it cannot be."""
    text = "".join(json.dumps({"message_id": message_id}) + "\n" for message_id in sorted(ids)).encode("utf-8")
    if not text:
        return
    path = folder / "ledger.jsonl"
    with open(path, "ab") as fh:
        size = fh.seek(0, os.SEEK_END)
        if size:
            with open(path, "rb") as head:
                head.seek(size - 1)
                if head.read(1) != b"\n":
                    text = b"\n" + text
        try:
            fh.write(text)
            fh.flush()
            os.fsync(fh.fileno())
        except OSError:
            try:
                fh.truncate(size)
            except OSError:
                raise LedgerTorn("the ledger could not be cut back") from None
            raise


# --- the two commands ---

WORKTREES = "worktrees.json"  # every worktree root seen at a start or finish; local, never copied


def known_roots(folder: Path) -> list[str]:
    """This repository's roots now, and every one a start or finish saw before: a worktree removed before the finish
    still has its records read. The list is kept up to date as a side effect."""
    path = folder / WORKTREES
    seen: set[str] = set()
    try:
        data = json.loads(path.read_text(encoding="utf-8")) if plain(path, "file") else []
    except (OSError, ValueError, RecursionError):
        data = []
    if isinstance(data, list):
        seen = {r for r in data if isinstance(r, str) and 0 < len(r) <= 4096 and os.path.isabs(r)}
    roots = sorted(seen | set(repository_roots()))
    if roots != sorted(seen):
        temporary = folder / f"tmp-worktrees-{secrets.token_hex(4)}.json"
        try:
            with open(temporary, "x", encoding="utf-8", newline="\n") as fh:
                fh.write(json.dumps(roots, indent=2) + "\n")
            os.replace(temporary, path)
        except OSError:
            with contextlib.suppress(OSError):
                temporary.unlink()
            raise CaptureError(f"{WORKTREES} could not be written; nothing else was written") from None
    return roots


def start(project: dict, issue: int, where: str) -> str:
    folder = capture_folder(project)
    known_roots(folder)  # a worktree made for the issue is remembered once a start runs in it
    path = folder / f"start-{issue}.json"
    try:
        with open(path, "x", encoding="utf-8", newline="\n") as fh:
            fh.write(json.dumps({"issue": issue, "started_at": utc(datetime.now(timezone.utc)), "where": where}) + "\n")
    except FileExistsError:
        return f"capture: issue #{issue}: start already recorded ({path.name} kept)"
    return f"capture: issue #{issue}: start recorded ({path.name})"


def issue_start(folder: Path, issue: int) -> tuple[datetime | None, str | None]:
    path = folder / f"start-{issue}.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, RecursionError):
        return None, None
    if not isinstance(data, dict):
        return None, None
    where = data.get("where") if data.get("where") in ("this-machine", "sandbox") else None
    return parse_time(data.get("started_at")), where


def finish(project: dict, issue: int, config: Path, other_accounts: bool = False) -> None:
    folder = capture_folder(project)
    lock = folder / "capture.lock"
    try:
        lock.open("x").close()  # one finish at a time per folder, so two worktrees never count one message twice
    except FileExistsError:
        raise CaptureError("another capture is running; if none is, remove capture.lock from the capture folder") from None
    try:
        _finish(folder, project["usage"]["project_id"], issue, config, other_accounts)
    finally:
        lock.unlink()


def _finish(folder: Path, project_id: str, issue: int, config: Path, other_accounts: bool) -> None:
    problems: set[tuple[object, str]] = set()
    entries = read_usage(record_files(config, known_roots(folder)), problems)
    counted = read_ledger(folder)
    fresh = [e for e in entries.values() if e["id"] not in counted]
    started, where = issue_start(folder, issue)
    reasons = []
    if started is None:
        reasons.append("the issue's start was not recorded")
    elif started < datetime.now(timezone.utc) - timedelta(days=RETENTION_DAYS):
        reasons.append("records older than the retention period may be gone")
    if where == "sandbox":
        reasons.append("the issue ran in a sandbox, whose records are not on this machine")
    if other_accounts:  # each account holds only its own records; its own finish captures them
        reasons.append("another account worked on the issue, whose records this one cannot read")
    for scope, scope_reasons in ((issue, reasons), (None, [])):
        chosen = [e for e in fresh if e["issue"] == scope]
        found = {reason for key, reason in problems if key in (scope, ANY_ISSUE)}
        found |= {e["problem"] for e in chosen if e.get("skip")}
        if scope is None and not chosen:
            continue  # no new usage outside an issue: no capture for it
        name, status = write_capture(folder, project_id, scope, chosen, scope_reasons, found)
        label = f"issue #{issue}" if scope else "no issue"
        # printed at once, so a stop at the next capture still names this one
        print(f"capture: {label}: {status}, {sum(1 for e in chosen if not e.get('skip'))} messages -> {name}",
              flush=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)
    begin = sub.add_parser("start")
    begin.add_argument("issue", type=int)
    begin.add_argument("--where", required=True, choices=("this-machine", "sandbox"))
    end = sub.add_parser("finish")
    end.add_argument("issue", type=int)
    end.add_argument("--other-accounts", dest="other_accounts", action="store_true")
    args = parser.parse_args(argv)
    if args.issue < 1:
        print("capture: the issue number must be 1 or more", file=sys.stderr)
        return 2
    try:
        project = claudecfg.load_project(ROOT / ".claude" / "project.json")
    except claudecfg.ProjectConfigError as exc:
        print(f"capture: {exc}", file=sys.stderr)
        return 2
    config = Path(os.environ.get("CLAUDE_CONFIG_DIR") or Path.home() / ".claude")
    try:
        if args.command == "start":
            print(start(project, args.issue, args.where))
        else:
            finish(project, args.issue, config, args.other_accounts)
    except CaptureError as exc:
        print(f"capture: {exc}", file=sys.stderr)
        return 1
    except OSError as exc:  # a file the capture folder holds could not be made, read or removed
        print(f"capture: stopped on a file error in the capture folder ({type(exc).__name__}); each capture written "
              "before the stop is printed above", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
