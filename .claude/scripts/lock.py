#!/usr/bin/env python3
"""Lock tooling: write and verify a consumer's copy of a signed Norma release.

Usage:
  python .claude/scripts/lock.py verify [--root DIR] [--base REV] [--from-git REV] [--revoked FILE]
  python .claude/scripts/lock.py write --bundle FILE --tag TAG --packs a,b [--root DIR] [--revoked FILE]
  python -I <the snapshot's lock.py> notice --version X.Y.Z --release-folder DIR --expected-url URL --branch NAME
         --status-record FILE --root DIR [--downgrade REASON]
  python .claude/scripts/lock.py notice-signers [--write] [--root DIR]
  python -I <the snapshot's lock.py> key-change-check --root DIR --expected-url URL --branch NAME --head REV
         --kind KIND [--statement FILE --statement-sig FILE] [--former-commit REV] [--revoke FPR ... --new-key FPR]
         [--ci-revoked PATH] [--record-project-id ID --record-url URL]
Exit 0 = ok; exit 2 = a check failed or an input cannot be used (each failure is printed, naming steps and paths,
never file contents).

What it proves. The release is a signed tag (namespace `git`, principal `norma-release`). The tag message carries
the SHA-256 of `.claude/core.manifest`, which lists the SHA-256 of every core and pack file. `verify` works offline:
it checks the tag signature against the release signer line of the project's signers file (during a key rotation,
also a second line that ends on a valid-before date: ADR 0010), the tag payload, the manifest
against the tag message, the lock against the manifest, every file against the manifest, and that the core-owned
folders hold nothing else. It is tamper evidence under review: it detects drift and edits of locked files, and gives
the reviewer a signed statement to check. It is not authentication against a pull request that can also change this
checker or the signers file. For that, CI runs a copy of this file taken from the BASE commit (never the pull
request's own copy) with `--base REV`, which reads the signer line and the old lock from that commit and refuses a
lock that moves to an older release unless the lock carries a `downgrade` marker.
`--from-git REV` reads the files from that commit instead of the working tree (use it in a pre-push check).
`write` installs from a release bundle (`norma-v<X>.tar`, made by Norma's release script and announced with its
SHA-256): the signed tag object and the files of each unit. It never reads a Norma repository; it checks the bundle's
tag, manifest and files with the same rules as `verify`, then installs the units of the installed packs.
`notice` (ADR 0014) authenticates a release notice before anything in it is followed: NOTICE.md and its .sig are read
once from the release folder, signed under `norma-notice`, and checked against a snapshot, the exact tip of the
project's default branch on the expected remote, from which every trust input is read; it is run as the snapshot's own
copy (`git show <commit>:.claude/scripts/lock.py`, then `python -I`) with a current status record from the
independent source. `notice-signers` derives the notice-signers file from the signers file, or checks it.
`key-change-check` runs the reviewer's checks of one key change (ADR 0014, "Key changes") read-only, against the
snapshot and the pull request's head commit, which must contain it; its output is the Key-change evidence record.
"""
from __future__ import annotations

import sys

if sys.version_info < (3, 12):  # an older interpreter stops here with a clear message, not a traceback
    sys.stderr.write("norma: this needs Python 3.12 or newer, found %d.%d; fix the interpreter on PATH.\n" % sys.version_info[:2])
    sys.exit(2)

import argparse
import base64
import binascii
import fnmatch
import hashlib
import io
import json
import os
import stat
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import unicodedata
from datetime import date, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PRINCIPAL = "norma-release"
NAMESPACE = "git"
LOCK_REL = ".claude/core.lock.json"
MANIFEST_REL = ".claude/core.manifest"
MAX_LOCK_BYTES = 262144
MAX_BUNDLE_BYTES = 64_000_000
MAX_TAG_BYTES = 14997  # its base64 form stays below the lock's 20000-character limit
BUNDLE_TAG = "tag"
BUNDLE_TAG_PARTS = ("tag.payload", "tag.sig")  # the tag split for `ssh-keygen -Y verify`; joined, they are the tag
MAX_FILE_BYTES = 2_000_000
MAX_RECORDS = 5000
MAX_PATH = 200
SSH_TIMEOUT = 20
TEXT_SUFFIXES = (".py", ".md", ".json", ".yml", ".yaml", ".toml", ".manifest")
PATH_RE = re.compile(r"\.claude(?:/[A-Za-z0-9._-]+)+")
VERSION_RE = re.compile(r"(\d+)\.(\d+)\.(\d+)")
UNIT_RE = re.compile(r"core|[a-z][a-z0-9-]{0,29}")
HEX64 = re.compile(r"[0-9a-f]{64}")
KEYTYPE = (r"(?:ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(?:256|384|521)"
           r"|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-nistp256@openssh\.com)")
SIGNER_LINE = re.compile(rf"(?P<principals>\S+)(?:\s+(?P<options>\S*\"[^\"]*\"\S*|(?!{KEYTYPE}\s)\S+))?"
                         rf"\s+(?P<type>{KEYTYPE})\s+(?P<key>[A-Za-z0-9+/]+={{0,2}})(?:\s.*)?")
GOOD = re.compile(rb'Good "git" signature for norma-release with (\S+) key (SHA256:[A-Za-z0-9+/]{43})')
TAG_PAYLOAD = re.compile(rb"object ([0-9a-f]{40})\ntype commit\ntag (v\d+\.\d+\.\d+)\ntagger [^\n]*\n\n"
                         rb"Norma (v\d+\.\d+\.\d+)\n\nmanifest-sha256: ([0-9a-f]{64})\n")
BEGIN = b"-----BEGIN SSH SIGNATURE-----"
END = b"-----END SSH SIGNATURE-----"
LOCK_KEYS = {"norma", "tag", "commit", "packs", "signer", "tag_object", "files"}
LOCK_OPTIONAL = {"downgrade"}


def safe(text: str) -> str:
    """Non-printable characters escaped, so a file name cannot hide or fake an output line."""
    return "".join(ch if ch.isprintable() else f"\\x{ord(ch):02x}" for ch in text)


class LockError(Exception):
    def __init__(self, step: str, message: str):
        super().__init__(f"{step}: {message}")
        self.step = step


# ---------------------------------------------------------------- hashing and paths

def normalised(data: bytes, name: str) -> bytes:
    """The bytes that are hashed: for named text files a carriage return directly before a line feed is dropped,
    in one pass; every other file is hashed as it is."""
    if name.endswith(TEXT_SUFFIXES):
        return data.replace(b"\r\n", b"\n")
    return data


def file_hash(data: bytes, name: str) -> str:
    return hashlib.sha256(normalised(data, name)).hexdigest()


def check_path(path: str) -> None:
    if (len(path) > MAX_PATH or not PATH_RE.fullmatch(path) or any(seg in (".", "..") for seg in path.split("/"))
            or any(ord(c) < 32 or c == "\\" for c in path)):
        raise LockError("manifest", "a path is not allowed")


def version_tuple(version: str) -> tuple[int, int, int]:
    m = VERSION_RE.fullmatch(version)
    if not m:
        raise LockError("lock", "a version is not in the form x.y.z")
    return tuple(int(g) for g in m.groups())  # type: ignore[return-value]


# ---------------------------------------------------------------- the manifest

def parse_manifest(data: bytes) -> dict:
    """{'version': 'x.y.z', 'units': {name: {'dirs': [...], 'files': {path: sha256}}}}; strict, raises LockError."""
    if len(data) > MAX_FILE_BYTES:
        raise LockError("manifest", "it is too large")
    if b"\r" in data or not data.endswith(b"\n"):
        raise LockError("manifest", "it must use LF line ends and end with a line feed")
    try:
        lines = data.decode("utf-8").split("\n")[:-1]
    except UnicodeDecodeError:
        raise LockError("manifest", "it is not UTF-8 text") from None
    if len(lines) > MAX_RECORDS or len(lines) < 3 or lines[0] != "norma-manifest 1":
        raise LockError("manifest", "the header is wrong or it holds too many records")
    if not lines[1].startswith("version ") or not VERSION_RE.fullmatch(lines[1][8:]):
        raise LockError("manifest", "the version line is wrong")
    version = lines[1][8:]
    units: dict[str, dict] = {}
    seen: set[str] = set()
    folded: set[str] = set()
    current = None
    for line in lines[2:]:
        kind, _, rest = line.partition(" ")
        if kind == "unit":
            if not UNIT_RE.fullmatch(rest) or rest in units or (not units and rest != "core") or (units and rest == "core"):
                raise LockError("manifest", "a unit record is wrong or repeated")
            current = units[rest] = {"dirs": [], "files": {}}
        elif current is None:
            raise LockError("manifest", "a record comes before the first unit")
        elif kind == "dir":
            check_path(rest)
            if current["files"] or rest in current["dirs"]:
                raise LockError("manifest", "a dir record is out of place or repeated")
            current["dirs"].append(rest)
        elif kind == "file":
            digest, _, path = rest.partition(" ")
            check_path(path)
            if not HEX64.fullmatch(digest) or path in seen:
                raise LockError("manifest", "a file record is wrong or repeated")
            key = unicodedata.normalize("NFC", path).casefold()
            if key in folded:
                raise LockError("manifest", "two paths collide after case folding")
            seen.add(path)
            folded.add(key)
            current["files"][path] = digest
        else:
            raise LockError("manifest", "an unknown record kind")
    for unit in units.values():
        if unit["dirs"] != sorted(unit["dirs"]) or list(unit["files"]) != sorted(unit["files"], key=lambda p: p.encode()):
            raise LockError("manifest", "records are not sorted")
    return {"version": version, "units": units}


# ---------------------------------------------------------------- the lock file

def _no_duplicates(pairs):
    seen = set()
    for key, _ in pairs:
        if key in seen:
            raise ValueError("duplicate key")
        seen.add(key)
    return dict(pairs)


def parse_lock(data: bytes) -> dict:
    if len(data) > MAX_LOCK_BYTES:
        raise LockError("lock", "the lock file is too large")
    try:
        lock = json.loads(data.decode("utf-8-sig"), object_pairs_hook=_no_duplicates)
    except (ValueError, RecursionError):
        raise LockError("lock", "the lock file is not valid JSON or has a duplicate key") from None
    if not isinstance(lock, dict) or not LOCK_KEYS <= set(lock) <= LOCK_KEYS | LOCK_OPTIONAL:
        raise LockError("lock", "the lock file has missing or unknown keys")
    version_tuple(lock["norma"]) if isinstance(lock["norma"], str) else _bad_lock()
    if lock["tag"] != "v" + lock["norma"]:
        raise LockError("lock", "tag must be v followed by the version")
    if not (isinstance(lock["commit"], str) and re.fullmatch(r"[0-9a-f]{40}", lock["commit"])):
        raise LockError("lock", "commit is not a full hash")
    packs = lock["packs"]
    if not (isinstance(packs, list) and len(packs) <= 10 and all(isinstance(p, str) and UNIT_RE.fullmatch(p) and p != "core"
                                                                for p in packs) and packs == sorted(set(packs))):
        raise LockError("lock", "packs must be a sorted list of pack names without core")
    if not (isinstance(lock["signer"], str) and re.fullmatch(r"SHA256:[A-Za-z0-9+/]{43}", lock["signer"])):
        raise LockError("lock", "signer is not a key fingerprint")
    if not (isinstance(lock["tag_object"], str) and len(lock["tag_object"]) < 20000):
        raise LockError("lock", "tag_object is wrong")
    files = lock["files"]
    if not (isinstance(files, dict) and len(files) <= MAX_RECORDS):
        raise LockError("lock", "files must be an object")
    for path, value in files.items():
        check_path(path)
        if not (isinstance(value, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", value)):
            raise LockError("lock", "a file hash is not in the form sha256:<hex>")
    if "downgrade" in lock:
        d = lock["downgrade"]
        if not (isinstance(d, dict) and set(d) == {"from", "to", "reason"} and all(isinstance(v, str) for v in d.values())
                and 1 <= len(d["reason"]) <= 300):
            raise LockError("lock", "downgrade must hold from, to and a reason")
        version_tuple(d["from"]), version_tuple(d["to"])
        if d["to"] != lock["norma"]:
            raise LockError("lock", "downgrade.to must be this lock's version")
        if version_tuple(d["from"]) <= version_tuple(d["to"]):
            raise LockError("lock", "downgrade.from must be a later version than downgrade.to")
    return lock


def _bad_lock():
    raise LockError("lock", "norma is not a string")


# ---------------------------------------------------------------- the signer line and the tag

def key_fingerprint(b64: str) -> str:
    try:
        blob = base64.b64decode(b64, validate=True)
    except (binascii.Error, ValueError):
        raise LockError("signers", "the key is not valid base64") from None
    return "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")


OPEN_OPTIONS = f'namespaces="{NAMESPACE}"'
VALID_BEFORE = re.compile(r'valid-before="([0-9]{8})"')
MAX_VALID_DAYS = 366  # an expiring line ends within about a year: it carries a rotation, never a second pin
OPENSSH_MIN = (8, 8)  # the first OpenSSH whose allowed-signers lines take valid-before
OPENSSH_OUT = re.compile(r"OpenSSH(?:_for_Windows)?_(\d+)\.(\d+)")


def _option_list(options: str) -> list[str]:
    """The comma-separated options of a signer line; a comma inside quotes belongs to its value."""
    parts, current, quoted = [], "", False
    for ch in options:
        if ch == '"':
            quoted = not quoted
        if ch == "," and not quoted:
            parts.append(current)
            current = ""
        else:
            current += ch
    return parts + [current]


def select_signers(text: str, today: date | None = None) -> list[tuple[str, str, str | None]]:
    """(key type, key, valid-before date or None) of each line that can verify principal norma-release, open-ended
    line first (ADR 0010): exactly one line with exactly `namespaces="git"`, and at most one more with exactly
    `namespaces="git"` and `valid-before="YYYYMMDD"`, a real date at most a year ahead, for another key. Each must
    name only that principal; no other line may match it."""
    matches = []
    # lines end at a line feed only, as ssh-keygen reads them: str.splitlines() also splits at U+2028 and other
    # separators, which would turn what a reviewer sees as one line into two
    for raw in text.split("\n"):
        line = raw.removesuffix("\r").strip()
        if not line or line.startswith("#"):
            continue
        if PRINCIPAL in line and not all(" " <= ch <= "~" or ch == "\t" for ch in line):
            raise LockError("signers", "a line that mentions the release principal holds a control or non-ASCII "
                                       "character")
        m = SIGNER_LINE.fullmatch(line)
        if not m:
            if PRINCIPAL in line:
                raise LockError("signers", "a line that mentions the release principal cannot be read")
            continue
        names = m.group("principals").split(",")
        positive = [n for n in names if not n.startswith("!")]
        negative = [n[1:] for n in names if n.startswith("!")]
        hit = (any(fnmatch.fnmatchcase(PRINCIPAL, n) for n in positive)
               and not any(fnmatch.fnmatchcase(PRINCIPAL, n) for n in negative))
        if hit:
            matches.append((m, names))
    several = "exactly one signer line must match the release principal, plus at most one that expires"
    if not 1 <= len(matches) <= 2 or (len(matches) == 2 and any(names != [PRINCIPAL] for _, names in matches)):
        raise LockError("signers", several)
    today = today or date.today()
    found = []
    for m, names in matches:
        if names != [PRINCIPAL]:
            raise LockError("signers", "the release signer line must name only the release principal, without wildcards")
        options = sorted(_option_list(m.group("options") or ""))
        expiry = [VALID_BEFORE.fullmatch(o) for o in options if o.startswith("valid-before=")]
        if options == [OPEN_OPTIONS]:
            found.append((m.group("type"), m.group("key"), None))
        elif len(options) == 2 and OPEN_OPTIONS in options and len(expiry) == 1 and expiry[0]:
            try:
                ends = datetime.strptime(expiry[0].group(1), "%Y%m%d").date()
            except ValueError:
                raise LockError("signers", "the expiring signer line's valid-before is not a real date") from None
            if (ends - today).days > MAX_VALID_DAYS:
                raise LockError("signers", "the expiring signer line's valid-before is more than a year ahead")
            found.append((m.group("type"), m.group("key"), expiry[0].group(1)))
        else:
            raise LockError("signers", f'a release signer line must carry exactly {OPEN_OPTIONS}, and the one that '
                                       'expires also exactly valid-before="YYYYMMDD"')
    found.sort(key=lambda entry: entry[2] is not None)
    if found[0][2] is not None or (len(found) == 2 and found[1][2] is None):
        raise LockError("signers", several + " (exactly one line without valid-before)")
    if len(found) == 2 and key_fingerprint(found[0][1]) == key_fingerprint(found[1][1]):  # never the base64 text
        raise LockError("signers", "the two release signer lines hold the same key")
    return found


def openssh_version(binary: str) -> tuple[int, int] | None:
    """The OpenSSH version of the `ssh` beside this ssh-keygen (ssh-keygen itself prints none), or None."""
    ssh = Path(binary).with_name("ssh" + Path(binary).suffix)
    if not ssh.is_file():
        return None
    try:
        with tempfile.TemporaryDirectory() as tmp:  # never the checkout as the working folder, as for ssh-keygen
            run = subprocess.run([str(ssh), "-V"], capture_output=True, env=ssh_env(), timeout=SSH_TIMEOUT,
                                 stdin=subprocess.DEVNULL, check=False, cwd=tmp)
    except (OSError, subprocess.TimeoutExpired):
        return None
    m = OPENSSH_OUT.search((run.stderr + run.stdout).decode("utf-8", "replace"))
    return (int(m.group(1)), int(m.group(2))) if m else None


def split_tag_object(raw: bytes) -> tuple[bytes, bytes]:
    """(payload, signature block) with one BEGIN line at the start of a line, one END line, nothing after it."""
    if raw.count(BEGIN) != 1 or raw.count(END) != 1:
        raise LockError("tag", "the tag object must hold exactly one signature block")
    start, end = raw.index(BEGIN), raw.index(END)
    if start == 0 or raw[start - 1] != 10 or end < start or raw[end + len(END):] not in (b"", b"\n"):
        raise LockError("tag", "the signature block is not at the end of the tag object")
    return raw[:start], raw[start:]


def tag_fields(payload: bytes) -> tuple[str, str, str, str]:
    """(object, tag, message tag, manifest hash) of a payload in the exact form the release script writes."""
    m = TAG_PAYLOAD.fullmatch(payload)
    if not m:
        raise LockError("tag", "the tag payload is not in the fixed release form")
    return tuple(g.decode("ascii") for g in m.groups())  # type: ignore[return-value]


def no_cwd_lookup() -> None:
    """On Windows a bare program name is looked up in the current folder first, which in CI is the checkout being
    verified; switch that off for this process before any program is started or searched for."""
    os.environ["NoDefaultCurrentDirectoryInExePath"] = "1"


def _inside(path: Path, repo: Path) -> bool:
    """Whether a found program lies in the repository (the same rule as _claudecfg.inside_repository); fails closed."""
    try:
        resolved = path.resolve()
    except (OSError, RuntimeError, ValueError):
        return True
    if resolved == repo or resolved.is_relative_to(repo):
        return True
    for folder in (resolved, *resolved.parents):
        try:
            if os.path.samefile(folder, repo):
                return True
        except OSError:
            continue
    return False


def trusted_program(name: str, root: Path | None = None) -> str | None:
    """The absolute path of `name` on PATH, never one the repository can supply: PATH entries in order, leaving out
    every entry that is not absolute, on Windows every `\\\\` form, and every program inside `root`; on Windows only
    `<name>.exe`, never a `.cmd` or `.bat`. The same rule as _claudecfg.trusted_program; lock.py keeps its own copy,
    since the locked checker imports no other script."""
    no_cwd_lookup()
    windows = os.name == "nt"
    repo = root.resolve() if root is not None else None
    filename = f"{name}.exe" if windows else name
    for entry in os.environ.get("PATH", "").split(os.pathsep):
        entry = entry.strip().strip('"')
        if not entry or not os.path.isabs(entry) or windows and entry.replace("/", "\\").startswith("\\\\"):
            continue
        candidate = Path(entry) / filename
        if not candidate.is_file() or not windows and not os.access(candidate, os.X_OK):
            continue
        if repo is not None and _inside(candidate, repo):
            continue
        return str(candidate)
    return None


def ssh_keygen(root: Path | None) -> str:
    """ssh-keygen from PATH, never the current folder, a relative entry or an entry inside `root`, the repository
    being checked (issue #61). Every caller passes the root it works in; None only where there is no repository."""
    found = trusted_program("ssh-keygen", root)
    if not found:
        raise LockError("tag", "ssh-keygen was not found")
    return found


SSH_ENV = ("SYSTEMROOT", "PROGRAMDATA")
WINDOWS = os.name == "nt"


def ssh_env(environ=os.environ, windows: bool | None = None) -> dict[str, str]:
    """The environment ssh-keygen runs with: empty, except on Windows, where it gets SYSTEMROOT and PROGRAMDATA;
    Windows' built-in OpenSSH exits 255 without a word when PROGRAMDATA is missing. Each is passed only as an
    absolute path to an existing folder. The environment already picks the binary (PATH), and floors.py refuses
    both names in a settings or pack `env`, so this widens nothing."""
    if not (WINDOWS if windows is None else windows):
        return {}
    return {name: environ[name] for name in SSH_ENV
            if environ.get(name) and os.path.isabs(environ[name]) and os.path.isdir(environ[name])}


def verify_signature(raw_tag: bytes, signers_text: str, revoked: Path | None = None,
                     revoked_data: bytes | None = None, *, root: Path | None = None) -> tuple[str, str, str, str, str]:
    """Check the signature with a signers file built from the selected lines only (one, or two during a key
    rotation). Returns the payload fields plus the key fingerprint ssh-keygen reported, which must be one of them."""
    payload, signature = split_tag_object(raw_tag)
    fields = tag_fields(payload)
    signers = select_signers(signers_text)
    fingerprints = [key_fingerprint(key) for _, key, _ in signers]
    binary = ssh_keygen(root)
    if len(signers) == 2:  # an older ssh-keygen would not read valid-before: stop with the minimum named
        found = openssh_version(binary)
        if found is None or found < OPENSSH_MIN:
            seen = f"found {found[0]}.{found[1]}" if found else "its version cannot be read"
            raise LockError("signers", f"a second, expiring signer line needs OpenSSH {OPENSSH_MIN[0]}.{OPENSSH_MIN[1]} "
                                       f"or later for ssh-keygen ({seen}); update OpenSSH or drop the expiring line")
    lines = ""
    for key_type, key, ends in signers:
        options = OPEN_OPTIONS + (f',valid-before="{ends}"' if ends else "")
        lines += f"{PRINCIPAL} {options} {key_type} {key}\n"
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp).resolve()
        (tmp_path / "signers").write_text(lines, encoding="ascii")
        (tmp_path / "sig").write_bytes(signature)
        argv = [binary, "-Y", "verify", "-f", str(tmp_path / "signers"), "-I", PRINCIPAL, "-n", NAMESPACE,
                "-s", str(tmp_path / "sig")]
        if revoked_data is not None:  # the revoked keys as the base branch holds them
            (tmp_path / "revoked").write_bytes(revoked_data)
            argv += ["-r", str(tmp_path / "revoked")]
        elif revoked is not None:
            argv += ["-r", str(Path(revoked).resolve())]
        env = ssh_env()
        try:
            run = subprocess.run(argv, input=payload, capture_output=True, cwd=tmp_path, env=env, timeout=SSH_TIMEOUT)
        except (OSError, subprocess.TimeoutExpired):
            raise LockError("tag", "ssh-keygen could not run to completion") from None
    if run.returncode != 0 and not run.stdout.strip() and not run.stderr.strip():
        # a refused signature always says why on stderr; silence means ssh-keygen itself did not start properly
        missing = [name for name in SSH_ENV if name not in env] if WINDOWS else []
        hint = f"; not passed: {', '.join(missing)}" if missing else ""
        raise LockError("tag", f"ssh-keygen failed (exit {run.returncode}) without output: {safe(binary)}{hint}")
    good = GOOD.fullmatch(run.stdout.strip())
    if run.returncode != 0 or not good:
        raise LockError("tag", "the tag signature does not verify against the release signer line")
    fingerprint = good.group(2).decode("ascii")
    if fingerprint not in fingerprints:
        raise LockError("tag", "the signing key is not the key of the release signer line")
    return fields + (fingerprint,)  # type: ignore[return-value]


# ---------------------------------------------------------------- where the files are read from

class Source:
    """The consumer tree to check: the working tree under `root`, or a commit of it read with git."""

    def __init__(self, root: Path, rev: str | None = None):
        self.root, self.rev = Path(root), rev

    def read(self, rel: str) -> bytes | None:
        """The bytes of a regular file, or None when it is absent. Raises LockError for a link or anything else."""
        if self.rev:
            out = git(self.root, "ls-tree", "-z", self.rev, "--", rel)
            if not out:
                return None
            mode = out.split(b" ", 1)[0]
            if mode not in (b"100644", b"100755"):
                raise LockError("files", f"{rel} is not a regular file")
            blob = git(self.root, "cat-file", "blob", f"{self.rev}:{rel}")
            if len(blob) > MAX_FILE_BYTES:
                raise LockError("files", f"{rel} is too large")
            return blob
        path = self.root / rel
        try:
            mode = os.lstat(path).st_mode
        except FileNotFoundError:
            return None
        except OSError:
            raise LockError("files", f"{rel} cannot be read") from None
        if not stat.S_ISREG(mode):
            raise LockError("files", f"{rel} is not a regular file")
        if path.stat().st_size > MAX_FILE_BYTES:
            raise LockError("files", f"{rel} is too large")
        with open(path, "rb") as fh:
            if not stat.S_ISREG(os.fstat(fh.fileno()).st_mode):
                raise LockError("files", f"{rel} is not a regular file")
            return fh.read(MAX_FILE_BYTES + 1)

    def all_tracked(self) -> set[str] | None:
        """Every path git holds: the index for the working tree, the tree of the commit otherwise; None when git
        cannot list them."""
        try:
            raw = (git(self.root, "ls-tree", "-r", "-z", "--name-only", self.rev) if self.rev
                   else git(self.root, "ls-files", "-z"))
        except LockError:
            return None
        return {n.decode("utf-8", "replace") for n in raw.split(b"\0") if n}

    def listing(self, rel_dir: str) -> list[tuple[str, bool]]:
        """Every entry below a folder as (path, is_regular_file); the working tree includes untracked and ignored
        files, a commit lists what it holds."""
        out: list[tuple[str, bool]] = []
        if self.rev:
            raw = git(self.root, "ls-tree", "-r", "-z", self.rev, "--", rel_dir)
            for entry in raw.split(b"\0"):
                if entry:
                    meta, _, name = entry.partition(b"\t")
                    out.append((name.decode("utf-8", "replace"), meta.split(b" ", 1)[0] in (b"100644", b"100755")))
            return out
        base = self.root / rel_dir
        if not base.exists():
            return out
        for dirpath, dirnames, filenames in os.walk(base, followlinks=False):
            for name in list(dirnames):  # a linked folder is reported as an entry that is not a regular file
                full = Path(dirpath) / name
                if full.is_symlink() or full.is_junction():
                    dirnames.remove(name)
                    out.append((full.relative_to(self.root).as_posix(), False))
            for name in dirnames + filenames:
                full = Path(dirpath) / name
                rel = full.relative_to(self.root).as_posix()
                regular = stat.S_ISREG(os.lstat(full).st_mode)
                if full.is_dir() and not full.is_symlink():
                    continue
                out.append((rel, regular))
        return out


def git(root: Path, *args: str) -> bytes:
    program = trusted_program("git", root)
    if not program:
        raise LockError("git", "git could not be run (not found on PATH outside the repository)")
    try:
        # no line-ending conversion: git must hand over the bytes that were committed
        run = subprocess.run([program, "-C", str(root), "-c", "core.autocrlf=false", "-c", "core.eol=lf", *args],
                             capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        raise LockError("git", "git could not be run") from None
    if run.returncode != 0:
        raise LockError("git", "a git command failed")
    return run.stdout


# ---------------------------------------------------------------- verify

def load_project_facts(source: Source) -> tuple[str, list[str]]:
    """(signers file path, declared packs) from the project.json of the source, checked in the same strict way."""
    raw = source.read(".claude/project.json")
    if raw is None:
        raise LockError("project", ".claude/project.json is missing")
    try:
        data = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=_no_duplicates)
    except (ValueError, RecursionError):
        raise LockError("project", ".claude/project.json is not valid JSON or has a duplicate key") from None
    try:
        signers = data["lock"]["signers_file"]
        packs = sorted(data["packs"])
    except (ValueError, KeyError, TypeError, AttributeError, RecursionError):
        raise LockError("project", ".claude/project.json has no usable lock.signers_file or packs") from None
    if not (isinstance(signers, str) and re.fullmatch(r"[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*", signers)
            and ".." not in signers.split("/")):
        raise LockError("project", "lock.signers_file is not a repository-relative path")
    return signers, packs


# A path component that a checkout can map onto another name: an 8.3 short name on Windows (CLAUDE~1 is .claude,
# GITHUB~1 is .github) or a name with a code point macOS ignores when it compares names (git's own list for .git).
# On Linux such a path is a separate file, so it passes every other check; on Windows or macOS it is written over the
# file it names, for example a signed hook or settings.json.
ALIAS_COMPONENT = re.compile(r"[^/~.]{1,6}~[0-9]+(?:\.[^/.]{0,3})?")
ALIAS_IGNORED = re.compile("[\u200c-\u200f\u202a-\u202e\u206a-\u206f\ufeff]")


def alias_path(path: str) -> bool:
    """True when a checkout on Windows or macOS could map this tracked path onto another file."""
    return bool(ALIAS_IGNORED.search(path)) or any(ALIAS_COMPONENT.fullmatch(c) for c in path.split("/"))


def alias_problems(source: "Source") -> list[str]:
    """Every tracked path, anywhere in the repository, that a checkout could map onto another file."""
    return [f"paths: {safe(p)} could be checked out over another file on Windows or macOS (an 8.3 short name or a "
            "character macOS ignores); rename it" for p in sorted(source.all_tracked() or ()) if alias_path(p)]


def core_folder_checks(source: "Source", root: Path, folder: str) -> tuple[list[str], set[str] | None]:
    """The folder-level refusals that verify and write share, and the tracked paths below the folder without case
    (None when git cannot list them; every file then counts as tracked). A core folder that is a link, or lies under
    one, is refused: a check through the link would miss what git holds beyond it. A tracked path that spells the
    folder in another case is refused: a case-insensitive checkout (Windows, macOS) puts it into the folder itself."""
    problems = []
    if not source.rev and _link_on_path(Path(root), folder):
        problems.append(f"folders: {folder} or a folder on its path is a link")
    every = source.all_tracked()
    if every is None:
        return problems, None
    prefix = folder.casefold() + "/"
    below = sorted(p for p in every if p.casefold().startswith(prefix))
    problems += [f"folders: {safe(p)} spells {folder} in another case" for p in below if not p.startswith(folder + "/")]
    return problems, {p.casefold() for p in below}


def _bytecode(path: str) -> bool:
    """A .pyc file, or anything in a __pycache__ folder, compared without case (ADR 0009). Norma's own hooks and scripts
    load their modules from source and never write bytecode, so none belongs in a core folder, whatever its header:
    a correct header is no proof of where the code came from."""
    folded = path.casefold()
    return folded.endswith(".pyc") or "/__pycache__/" in folded


def _bytecode_problems(paths: list[str]) -> list[str]:
    """One problem per folder that holds bytecode, naming the folder."""
    folders = sorted({p.rsplit("/", 1)[0] for p in paths})
    return [f"folders: {safe(f)} holds bytecode, which no core folder may hold (Norma's hooks and scripts never write "
            "any); delete it, and run your own tests that import a core module with python -B" for f in folders]


def verify(root: Path, base: str | None = None, from_git: str | None = None, revoked: Path | None = None) -> dict:
    """Run every check; returns a summary or raises LockError for the first failing step."""
    source = Source(root, from_git)
    signers_path, declared = load_project_facts(source)
    anchor = Source(root, base) if base else source
    anchor_signers, _ = load_project_facts(anchor) if base else (signers_path, declared)
    raw_lock = source.read(LOCK_REL)
    if raw_lock is None:
        raise LockError("lock", "the lock file is missing")
    lock = parse_lock(raw_lock)
    if sorted(lock["packs"]) != declared:
        raise LockError("lock", "the packs in the lock differ from the packs declared in project.json")
    signers_raw = anchor.read(anchor_signers)
    if signers_raw is None:
        raise LockError("signers", "the signers file is missing")
    try:
        signers_text = signers_raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise LockError("signers", "the signers file is not UTF-8 text") from None
    try:
        raw_tag = base64.b64decode(lock["tag_object"], validate=True)
    except (binascii.Error, ValueError):
        raise LockError("lock", "tag_object is not valid base64") from None
    revoked_data = None
    if base and revoked is not None:
        # a revoked-keys file inside the repository is read from the base, like the signers file: a pull request
        # that empties it must not bring a revoked key back
        # decided on the written path, never through links: a link in the working tree must not move it outside
        try:
            revoked_rel = Path(os.path.abspath(revoked)).relative_to(Path(os.path.abspath(root))).as_posix()
        except ValueError:
            revoked_rel = None  # outside the repository: the pull request cannot change it
        if revoked_rel is not None:
            if _link_on_path(Path(root), revoked_rel):
                raise LockError("revoked", "the revoked-keys file or a folder on its path is a link")
            revoked_data = anchor.read(revoked_rel)
            if revoked_data is None:
                raise LockError("revoked", "the revoked-keys file is not on the base branch")
    obj, tag, message_tag, manifest_hash, fingerprint = verify_signature(raw_tag, signers_text, revoked, revoked_data,
                                                                         root=root)
    if lock["signer"] != fingerprint:
        raise LockError("lock", "the signer in the lock is not the key of the release signer line")
    if obj != lock["commit"] or tag != lock["tag"] or message_tag != lock["tag"]:
        raise LockError("tag", "the tag does not match the lock's tag and commit")
    manifest_raw = source.read(MANIFEST_REL)
    if manifest_raw is None:
        raise LockError("manifest", "the manifest is missing")
    if hashlib.sha256(normalised(manifest_raw, MANIFEST_REL)).hexdigest() != manifest_hash:
        raise LockError("manifest", "the manifest does not match the hash signed in the tag")
    manifest = parse_manifest(normalised(manifest_raw, MANIFEST_REL))
    if manifest["version"] != lock["norma"]:
        raise LockError("manifest", "the manifest version differs from the lock")
    installed = ["core"] + lock["packs"]
    for unit in installed:
        if unit not in manifest["units"]:
            raise LockError("manifest", "an installed unit is not in the manifest")
    expected = {p: d for u in installed for p, d in manifest["units"][u]["files"].items()}
    if {p: "sha256:" + d for p, d in expected.items()} != lock["files"]:
        raise LockError("lock", "the files in the lock differ from the manifest")
    problems: list[str] = []
    for unit, spec in manifest["units"].items():
        if unit in installed:
            continue
        for path in spec["files"]:
            if source.read(path) is not None:
                problems.append(f"files: {path} belongs to a pack that is not installed")
    for path, digest in sorted(expected.items()):
        if not source.rev and _link_on_path(Path(root), path):
            problems.append(f"files: {path} or a folder on its path is a link")
            continue
        data = source.read(path)
        if data is None:
            problems.append(f"files: {path} is missing")
        elif file_hash(data, path) != digest:
            problems.append(f"files: {path} differs from the release")
    allowed = set(expected) | {MANIFEST_REL, LOCK_REL}
    for unit in installed:
        for folder in manifest["units"][unit]["dirs"]:
            # No bytecode at all, tracked or not, whatever its header (ADR 0009): each folder that holds some is named
            folder_problems, _tracked = core_folder_checks(source, root, folder)
            problems += folder_problems
            if folder_problems and not source.rev:
                continue  # never list through a link
            listing = source.listing(folder)
            problems += _bytecode_problems([path for path, _ in listing if _bytecode(path)])
            for path, regular in listing:
                if _bytecode(path):
                    continue
                if not regular:
                    problems.append(f"folders: {safe(path)} is not a regular file")
                elif path not in allowed:
                    problems.append(f"folders: {safe(path)} is not part of the release")
    problems += alias_problems(source)
    if base:
        prior = Source(root, base).read(LOCK_REL)
        if prior is not None:
            try:
                before = parse_lock(prior)
            except LockError:
                before = None
            if before is not None and version_tuple(lock["norma"]) < version_tuple(before["norma"]):
                marker = lock.get("downgrade")
                if not marker or marker["from"] != before["norma"]:
                    problems.append(f"lock: the lock moves from {before['norma']} to {lock['norma']} without a "
                                    "downgrade marker naming both versions and a reason")
    if problems:
        raise LockError("verify", "\n  ".join(problems))
    return {"norma": lock["norma"], "packs": lock["packs"], "files": len(expected)}


# ---------------------------------------------------------------- write

def read_bundle(bundle: Path, packs: list[str]) -> tuple[bytes, dict[str, bytes]]:
    """(raw tag object, {consumer path: bytes}) of a release bundle: a tar whose first member `tag` is the signed tag
    object and whose other members are the files of each unit under its archive path (`core/.claude/...`,
    `packs/<name>/.claude/...`). Files of a pack that is not installed are skipped without being read; anything else
    that is not a folder or a regular file below an installed unit is refused."""
    try:
        mode = os.lstat(bundle).st_mode
        size = os.lstat(bundle).st_size
    except OSError:
        raise LockError("bundle", "the bundle cannot be read") from None
    if not stat.S_ISREG(mode):
        raise LockError("bundle", "the bundle is not a regular file")
    if size > MAX_BUNDLE_BYTES:
        raise LockError("bundle", "the bundle is too large")
    with open(bundle, "rb") as fh:
        if not stat.S_ISREG(os.fstat(fh.fileno()).st_mode):
            raise LockError("bundle", "the bundle is not a regular file")
        data = fh.read(MAX_BUNDLE_BYTES + 1)
    if len(data) > MAX_BUNDLE_BYTES:
        raise LockError("bundle", "the bundle is too large")
    prefixes = {"core/.claude/": "core", **{f"packs/{p}/.claude/": p for p in packs}}
    raw_tag: bytes | None = None
    parts: list[bytes] = []
    members: dict[str, bytes] = {}
    total = 0
    previous_offset = -1
    try:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as tar:
            for index, member in enumerate(tar):
                # a crafted header must not make the reader loop or pile up members (older tarfile builds)
                if index > MAX_RECORDS or member.offset <= previous_offset:
                    raise LockError("bundle", "the bundle has too many entries or a malformed header")
                previous_offset = member.offset
                if member.name == BUNDLE_TAG:
                    if index != 0 or not member.isreg() or member.size > MAX_TAG_BYTES:
                        raise LockError("bundle", "the tag member must come first, once, as a small regular file")
                    raw_tag = tar.extractfile(member).read(MAX_TAG_BYTES + 1)
                    continue
                if index == 0:
                    raise LockError("bundle", "the tag member must come first, once, as a small regular file")
                if member.name in BUNDLE_TAG_PARTS or index < len(BUNDLE_TAG_PARTS) + 1:
                    # the tag's payload and signature as separate files, for the first-install check with ssh-keygen
                    expected = BUNDLE_TAG_PARTS[index - 1] if index <= len(BUNDLE_TAG_PARTS) else None
                    if member.name != expected or not member.isreg() or member.size > MAX_TAG_BYTES:
                        raise LockError("bundle", "the tag's payload and signature must follow the tag member, once each")
                    parts.append(tar.extractfile(member).read(MAX_TAG_BYTES + 1))
                    if len(parts) == len(BUNDLE_TAG_PARTS) and b"".join(parts) != raw_tag:
                        raise LockError("bundle", "the tag's payload and signature members are not the tag member")
                    continue
                if member.isdir():
                    continue
                other = re.fullmatch(r"packs/([a-z][a-z0-9-]{0,29})/\.claude/[^\x00]+", member.name)
                if other and other.group(1) not in packs and member.isreg():
                    continue  # a pack this project does not install: never read
                prefix = next((x for x in prefixes if member.name.startswith(x)), None)
                if not member.isreg() or prefix is None:
                    raise LockError("bundle", "the bundle holds an entry that is not a regular file below an installed unit")
                rel = ".claude/" + member.name[len(prefix):]
                check_path(rel)
                if member.size > MAX_FILE_BYTES:
                    raise LockError("bundle", f"{rel} is too large")
                content = tar.extractfile(member).read(MAX_FILE_BYTES + 1)
                total += len(content)
                if len(content) > MAX_FILE_BYTES or total > MAX_BUNDLE_BYTES or rel in members:
                    raise LockError("bundle", "the bundle is too large or repeats a path")
                members[rel] = content
    except (tarfile.TarError, EOFError, ValueError, OverflowError, UnicodeError):
        raise LockError("bundle", "the bundle is not a valid tar file") from None
    if raw_tag is None:
        raise LockError("bundle", "the bundle has no tag member")
    if len(parts) != len(BUNDLE_TAG_PARTS):
        raise LockError("bundle", "the tag's payload and signature must follow the tag member, once each")
    return raw_tag, members


def _link_on_path(root: Path, rel: str) -> bool:
    """Whether the target or any folder between root and it is a symbolic link or a junction."""
    current = root
    for part in rel.split("/"):
        current = current / part
        if current.is_symlink() or current.is_junction():
            return True
    return False


def previous_release_files(source: "Source", signers_text: str, revoked: Path | None, root: Path) -> set[str]:
    """The files of the release installed now, from its manifest, but only when that manifest matches the tag in the
    current lock and the tag's signature still verifies; otherwise none (nothing is removed, and `verify` reports any
    file left behind)."""
    old_manifest, old_lock = source.read(MANIFEST_REL), source.read(LOCK_REL)
    if old_manifest is None or old_lock is None:
        return set()
    try:
        raw_tag = base64.b64decode(parse_lock(old_lock)["tag_object"], validate=True)
        manifest_hash = verify_signature(raw_tag, signers_text, revoked, root=root)[3]
        if hashlib.sha256(normalised(old_manifest, MANIFEST_REL)).hexdigest() != manifest_hash:
            return set()
        previous = parse_manifest(normalised(old_manifest, MANIFEST_REL))
    except (LockError, binascii.Error, ValueError):
        return set()
    return {path for unit in previous["units"].values() for path in unit["files"]}


def write(root: Path, bundle: Path, tag: str, packs: list[str], revoked: Path | None = None) -> dict:
    """Owner-side bump: check a release bundle, install its units and write the lock. `tag` and `packs` are
    cross-checks: the tag must be the bundle's, the packs those that project.json declares."""
    root = Path(root)
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise LockError("write", "the tag must look like v1.2.3")
    source = Source(root)
    signers_path, declared = load_project_facts(source)
    if sorted(packs) != declared:
        raise LockError("write", "--packs must equal the packs declared in project.json")
    if any(not UNIT_RE.fullmatch(p) or p == "core" for p in packs):
        raise LockError("write", "a pack name is not allowed")
    signers_raw = source.read(signers_path)
    if signers_raw is None:
        raise LockError("signers", "the signers file is missing")
    raw_tag, members = read_bundle(Path(bundle), packs)
    obj, tag_name, message_tag, manifest_hash, fingerprint = verify_signature(
        raw_tag, signers_raw.decode("utf-8-sig"), revoked, root=root)
    if tag_name != tag or message_tag != tag:
        raise LockError("tag", "the bundle's tag is not the tag named with --tag")
    commit = obj  # the payload form requires `type commit`
    manifest_raw = members.get(MANIFEST_REL)
    if manifest_raw is None or hashlib.sha256(normalised(manifest_raw, MANIFEST_REL)).hexdigest() != manifest_hash:
        raise LockError("manifest", "the manifest in the release does not match the hash signed in the tag")
    manifest = parse_manifest(normalised(manifest_raw, MANIFEST_REL))
    installed = ["core"] + sorted(packs)
    if any(u not in manifest["units"] for u in installed) or manifest["version"] != tag[1:]:
        raise LockError("manifest", "the manifest does not hold the installed units or the tag's version")
    expected = {p: d for u in installed for p, d in manifest["units"][u]["files"].items()}
    for path, digest in expected.items():
        if path not in members or file_hash(members[path], path) != digest:
            raise LockError("write", f"{path} in the release differs from its manifest entry")
    if set(members) - set(expected) - {MANIFEST_REL}:
        raise LockError("write", "the release holds files that its manifest does not list")
    gone = previous_release_files(source, signers_raw.decode("utf-8-sig"), revoked, root) - set(expected)
    # files in the core-owned folders that neither this release nor the previous one has (older copies, for example):
    # verify would refuse them after the write, so they are named before anything is written
    aliases = alias_problems(source)
    if aliases:
        raise LockError("write", f"{aliases[0].split(': ', 1)[1]}; nothing was written")
    known = set(expected) | gone | {MANIFEST_REL, LOCK_REL}
    leftover, cached = [], []
    for folder in (f for unit in installed for f in manifest["units"][unit]["dirs"]):
        folder_problems, _tracked = core_folder_checks(source, root, folder)  # the same rules as verify
        if folder_problems:
            raise LockError("write", f"{folder_problems[0].split(': ', 1)[1]}; nothing was written")
        listing = source.listing(folder)
        cached += [path for path, _ in listing if _bytecode(path)]
        leftover += [path for path, _ in listing if path not in known and not _bytecode(path)]
    if cached:  # every folder named at once, so one clean-up and one run again
        names = "; ".join(problem.split(": ", 1)[1] for problem in _bytecode_problems(cached))
        raise LockError("write", f"{names}; nothing was written")
    if leftover:
        leftover.sort()
        shown = ", ".join(safe(p) for p in leftover[:5]) + (", ..." if len(leftover) > 5 else "")
        raise LockError("write", f"the core-owned folders hold files that are not part of the release ({shown}); "
                                 "remove them (older copies) and run write again; nothing was written. When the "
                                 "previous release no longer verifies against the signer lines (a revoked key, or a "
                                 "key change without an expiring line), its own files are listed too: compare them "
                                 "with the old lock's file list")
    for rel in sorted(gone | set(members) | {LOCK_REL}):  # every target first: nothing is written if one is unsafe
        if _link_on_path(root, rel):
            raise LockError("write", f"{rel}: the file or a folder on its path is a link")
        try:
            st = os.lstat(root / rel)
        except FileNotFoundError:
            continue
        except OSError:
            raise LockError("write", f"{rel}: the existing file cannot be checked") from None
        # a hard link would carry the write to a file outside the project; a folder or device cannot be replaced
        if not stat.S_ISREG(st.st_mode) or st.st_nlink > 1:
            raise LockError("write", f"{rel}: the existing entry is not a plain file (a folder, a device or a hard link)")
    try:
        for rel in sorted(gone):  # files of the previous release that this one no longer has
            target = root / rel
            if target.is_file():
                target.unlink()
        for rel, data in members.items():
            target = root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        lock = {"norma": tag[1:], "tag": tag, "commit": commit, "packs": sorted(packs), "signer": fingerprint,
                "tag_object": base64.b64encode(raw_tag).decode("ascii"),
                "files": {p: "sha256:" + d for p, d in sorted(expected.items())}}
        (root / LOCK_REL).write_text(json.dumps(lock, indent=2) + "\n", encoding="utf-8", newline="\n")
    except OSError as exc:
        raise LockError("write", f"a file could not be written ({type(exc).__name__}); the install is incomplete: "
                                 "fix the cause and run write again") from None
    return verify(root, revoked=revoked)


# ---------------------------------------------------------------- the signed release notice (ADR 0014)
#
# A project follows a release notice only after this check: the notice and its .sig are read once from the release
# folder as untrusted input; every trust input (the lock section of project.json, the signers, revoked-keys and
# notice-signers files, the installed version) comes from one snapshot, the exact tip of the project's default branch
# on its intended remote; and the checker is the snapshot's own lock.py. Only the canonical repository URL is ever
# compared or printed, never the URL as git contacts it: #61 scope item 3.

NOTICE_NAMESPACE = "norma-notice"
NOTICE_FILE = "NOTICE.md"
NOTICE_SIG = "NOTICE.md.sig"
NOTICE_FROM = (0, 5, 0)  # the first release whose lock.py has the notice command
MAX_NOTICE_BYTES = 262144
MAX_NOTICE_SIG_BYTES = 16384
MAX_STATUS_BYTES = 65536
STATUS_MAX_DAYS = 30
NOTICE_SIGNERS_DEFAULT = "allowed_notice_signers"
SELF_REL = ".claude/scripts/lock.py"
PROJECT_REL = ".claude/project.json"
GOOD_NOTICE = re.compile(rb'Good "norma-notice" signature for norma-release with (\S+) key (SHA256:[A-Za-z0-9+/]{43})')
REL_PATH = re.compile(r"[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*")
BRANCH_RE = re.compile(r"[A-Za-z0-9_][A-Za-z0-9._-]*(?:/[A-Za-z0-9_][A-Za-z0-9._-]*)*")
OBJECT_ID = re.compile(r"[0-9a-f]{40}|[0-9a-f]{64}")
ISO_DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
FILE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}")
NOTICE_FIELDS = ("version", "tag", "tag-object", "tag-commit", "release-date", "signing-key", "signing-key-fingerprint")
NOTICE_NEXT = ("next-key", "next-key-fingerprint")
LOCK_SECTION_KEYS = {"signers_file", "revoked_file", "notice_signers_file", "key_change_serial", "repository_url"}
GIT_KEEP = ("GIT_SSH", "GIT_SSH_COMMAND", "GIT_SSH_VARIANT", "GIT_ASKPASS")  # what authentication needs


def lock_paths(raw: bytes) -> dict[str, str | None]:
    """The trust-input paths of the lock section of a project.json: signers file, revoked-keys file (or None) and
    notice-signers file (`allowed_notice_signers` beside the signers file when the key is absent), with the loader's
    rules for these keys: repository-relative paths, three different files compared without case."""
    try:
        section = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=_no_duplicates)["lock"]
    except (ValueError, KeyError, TypeError, RecursionError):
        raise LockError("project", ".claude/project.json has no usable lock section") from None
    if not isinstance(section, dict) or "signers_file" not in section or set(section) - LOCK_SECTION_KEYS:
        raise LockError("project", "the lock section of .claude/project.json has missing or unknown keys")
    if ("key_change_serial" in section) != ("repository_url" in section):
        raise LockError("project", "lock.key_change_serial and lock.repository_url come together, or neither")
    paths: dict[str, str | None] = {}
    for key in ("signers_file", "revoked_file", "notice_signers_file"):
        if key not in section:
            paths[key] = None
            continue
        value = section[key]  # an explicit null is refused, as the loader refuses it
        if not (isinstance(value, str) and len(value) <= 200 and REL_PATH.fullmatch(value)
                and not any(segment in (".", "..") for segment in value.split("/"))):
            raise LockError("project", f"lock.{key} is not a repository-relative path")
        paths[key] = value
    if paths["notice_signers_file"] is None:
        folder = paths["signers_file"].rpartition("/")[0]
        paths["notice_signers_file"] = f"{folder}/{NOTICE_SIGNERS_DEFAULT}" if folder else NOTICE_SIGNERS_DEFAULT
    folded = [p.lower() for p in paths.values() if p is not None]
    if len(set(folded)) != len(folded):
        raise LockError("project", "the signers, revoked-keys and notice-signers files must be three different files")
    return paths


def notice_signer_lines(signers_text: str, today: date | None = None) -> bytes:
    """The notice-signers file derived from a signers file: one `norma-notice` line for each line select_signers
    takes, the open-ended line first, the expiring line keeping its valid-before (ADR 0014). ASCII, LF, no comment."""
    out = ""
    for key_type, key, ends in select_signers(signers_text, today):
        options = f'namespaces="{NOTICE_NAMESPACE}"' + (f',valid-before="{ends}"' if ends else "")
        out += f"{PRINCIPAL} {options} {key_type} {key}\n"
    return out.encode("ascii")


# The canonical repository URL: the same rules as _claudecfg.canonical_repository_url (a test feeds both the same
# inputs); lock.py keeps its own copy, since the locked checker imports no other script.
_URL_FORM = re.compile(r"(https|ssh)://([^/]*)(/.*)?")
_SCP_FORM = re.compile(r"(?:[^@/:]+@)?([^@/:]+):(.+)")
_URL_HOST = re.compile(r"[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)*")
_URL_SEGMENT = re.compile(r"[A-Za-z0-9._~!$&'()*+,;=:-]+")
_DEFAULT_PORTS = {"https": "443", "ssh": "22"}


def canonical_repository_url(url: str) -> str | None:
    """`<scheme>://<host>[:<port>]/<path>` of a git remote URL, or None; never holds a credential."""
    if not isinstance(url, str) or not 1 <= len(url) <= 2000 or not url.isascii() \
            or any(c in url for c in "?#\\ ") or any(ord(c) < 33 or ord(c) == 127 for c in url):
        return None
    form = _URL_FORM.fullmatch(url)
    if form:
        scheme, authority, path = form.group(1), form.group(2), form.group(3) or ""
        host, colon, port = authority.rpartition("@")[2].partition(":")
        if colon and not (port.isdigit() and port[0] != "0" and int(port) <= 65535):
            return None
    elif "://" in url:
        return None
    else:
        scp = _SCP_FORM.fullmatch(url)
        if not scp or len(scp.group(1)) == 1 or scp.group(2).startswith(":"):
            return None
        scheme, host, port, path = "ssh", scp.group(1), "", "/" + scp.group(2)
    host = host.lower()
    if not _URL_HOST.fullmatch(host):
        return None
    if path.endswith("/"):
        path = path[:-1]
    if path.endswith(".git"):
        path = path[:-4]
    if path.endswith(("/", ".git")):
        return None
    segments = path.split("/")[1:]
    if not path.startswith("/") or not segments or any(s in ("", ".", "..") or not _URL_SEGMENT.fullmatch(s)
                                                         for s in segments):
        return None
    canonical = f"{scheme}://{host}{f':{port}' if port and port != _DEFAULT_PORTS[scheme] else ''}{path}"
    return canonical if len(canonical) <= 300 else None


def _reparse_point(st: os.stat_result) -> bool:
    return bool(getattr(st, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def no_link_on_path(path: Path, what: str) -> Path:
    """The absolute path, refused when it or any folder above it is a link, junction or other reparse point: the
    release folder is read where it is, never through a link (execution requirement 5)."""
    absolute = Path(os.path.abspath(path))
    for part in (absolute, *absolute.parents):
        try:
            st = os.lstat(part)
        except OSError:
            raise LockError(what, "its path cannot be read") from None
        if stat.S_ISLNK(st.st_mode) or _reparse_point(st):
            raise LockError(what, "it, or a folder on its path, is a link, junction or other reparse point")
    return absolute


def read_once(path: Path, cap: int, what: str) -> bytes:
    """The bytes of a regular file, read once and bounded; a link, junction, FIFO, device or other file that is not
    regular is refused without blocking, and a file replaced while it is opened is refused."""
    try:
        before = os.lstat(path)
    except FileNotFoundError:
        raise LockError(what, "it is missing") from None
    except OSError:
        raise LockError(what, "it cannot be read") from None
    if not stat.S_ISREG(before.st_mode) or _reparse_point(before):
        raise LockError(what, "it is not a regular file")
    if before.st_size > cap:
        raise LockError(what, "it is too large")
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0)
    try:
        fd = os.open(path, flags)
    except OSError:
        raise LockError(what, "it cannot be opened") from None
    try:
        st = os.fstat(fd)
        if not stat.S_ISREG(st.st_mode) or (st.st_dev, st.st_ino) != (before.st_dev, before.st_ino):
            raise LockError(what, "it changed while it was opened")
        chunks, total = [], 0
        while chunk := os.read(fd, 65536):
            total += len(chunk)
            if total > cap:
                raise LockError(what, "it is too large")
            chunks.append(chunk)
    finally:
        os.close(fd)
    return b"".join(chunks)


def _signer_key(value: str, what: str) -> tuple[str, str]:
    m = SIGNER_LINE.fullmatch(value)
    if not m or m.group("principals") != PRINCIPAL or (m.group("options") or "") != OPEN_OPTIONS \
            or value != f"{PRINCIPAL} {OPEN_OPTIONS} {m.group('type')} {m.group('key')}":
        raise LockError("notice", f"the {what} field is not a release signer line")
    return m.group("type"), m.group("key")


def parse_notice(data: bytes, version: str) -> dict:
    """The header of a notice in the fixed form: first line `Norma release notice vX.Y.Z`, a blank line, then the
    fields in a fixed order (`key: value`), the next-key pair only in a release that announces a rotation, the units,
    and one `file: <name> <size> <sha256>` line per file; the body after the next blank line is for reading."""
    if len(data) > MAX_NOTICE_BYTES:
        raise LockError("notice", "it is too large")
    if data.startswith(b"\xef\xbb\xbf") or b"\r" in data or not data.endswith(b"\n"):
        raise LockError("notice", "it must be UTF-8 without a byte-order mark, with LF line ends, ending with a "
                                  "line feed")
    try:
        lines = data.decode("utf-8").split("\n")[:-1]
    except UnicodeDecodeError:
        raise LockError("notice", "it is not UTF-8 text") from None
    if not lines or lines[0] != f"Norma release notice v{version}":
        raise LockError("notice", "its first line is not exactly the notice line of the version being installed")
    if len(lines) < 3 or lines[1] != "":
        raise LockError("notice", "a blank line must follow the first line")
    fields: dict[str, str] = {}
    order: list[str] = []
    files: list[tuple[str, int, str]] = []
    for line in lines[2:]:
        if line == "":
            break
        key, sep, value = line.partition(": ")
        if not sep or not value or value != value.strip() or not value.isprintable():
            raise LockError("notice", "a header line is not in the form 'key: value'")
        order.append(key)
        if key == "file":
            parts = value.split(" ")
            if len(parts) != 3 or not FILE_NAME.fullmatch(parts[0]) \
                    or not re.fullmatch(r"0|[1-9][0-9]{0,11}", parts[1]) \
                    or not HEX64.fullmatch(parts[2]) or any(parts[0] == f[0] for f in files):
                raise LockError("notice", "a file line is not 'file: <name> <size> <sha256>', or names a file twice")
            files.append((parts[0], int(parts[1]), parts[2]))
        elif key in fields:
            raise LockError("notice", "a header field is repeated")
        else:
            fields[key] = value
    expected = [*NOTICE_FIELDS, *(NOTICE_NEXT if "next-key" in fields else ()), "units"]
    if order[:len(expected)] != expected or not files or order[len(expected):] != ["file"] * len(files):
        raise LockError("notice", "the header fields are missing, unknown or out of order")
    if fields["version"] != version or fields["tag"] != f"v{version}":
        raise LockError("notice", "its version or tag is not the version being installed")
    if not OBJECT_ID.fullmatch(fields["tag-object"]) or not OBJECT_ID.fullmatch(fields["tag-commit"]):
        raise LockError("notice", "the tag object or tag commit is not a full object id")
    try:
        if not ISO_DATE.fullmatch(fields["release-date"]):
            raise ValueError
        date.fromisoformat(fields["release-date"])
    except ValueError:
        raise LockError("notice", "the release date is not a real date in the form YYYY-MM-DD") from None
    _, key = _signer_key(fields["signing-key"], "signing-key")
    if fields["signing-key-fingerprint"] != key_fingerprint(key):
        raise LockError("notice", "the signing key's fingerprint field does not match its key")
    if "next-key" in fields:
        _, next_key = _signer_key(fields["next-key"], "next-key")
        if fields["next-key-fingerprint"] != key_fingerprint(next_key) or next_key == key:
            raise LockError("notice", "the next key's fingerprint field does not match its key, or it is the "
                                      "signing key")
    units = fields["units"].split(", ")
    if units[0] != "core" or len(set(units)) != len(units) or not all(UNIT_RE.fullmatch(u) for u in units):
        raise LockError("notice", "the units field is not 'core' followed by pack names, each once")
    return {**fields, "units": units, "files": files}


def check_status_record(data: bytes, own_sha256: str, today: date) -> str:
    """The status record from the independent source: issued at most 30 days before its valid-until date, current
    today, and not naming this lock.py as retired. Returns its issue date; raises LockError otherwise."""
    try:
        record = json.loads(data.decode("utf-8"), object_pairs_hook=_no_duplicates)
    except (ValueError, RecursionError):
        raise LockError("status record", "it is not valid JSON or has a duplicate key") from None
    keys = {"schema", "issued", "valid_until", "current_fixed_checkers", "retired_fixed_checkers", "retired_lock_py"}
    if not isinstance(record, dict) or set(record) != keys or type(record["schema"]) is not int \
            or record["schema"] != 1:
        raise LockError("status record", "it is not a schema 1 status record")
    try:
        if not all(isinstance(record[k], str) and ISO_DATE.fullmatch(record[k]) for k in ("issued", "valid_until")):
            raise ValueError
        issued, until = date.fromisoformat(record["issued"]), date.fromisoformat(record["valid_until"])
    except ValueError:
        raise LockError("status record", "its issued and valid_until are not real dates") from None
    if until < issued or (until - issued).days > STATUS_MAX_DAYS:
        raise LockError("status record", f"it is valid for more than {STATUS_MAX_DAYS} days, or ends before it starts")
    if today < issued or today > until:
        raise LockError("status record", "it is not current (expired, or issued later than today); obtain a "
                                         "current one from the independent source")
    hexes = lambda xs, k: isinstance(xs, list) and all(isinstance(x, dict) and isinstance(x.get(k), str)  # noqa: E731
                                                       and HEX64.fullmatch(x[k]) for x in xs)
    if not (hexes(record["current_fixed_checkers"], "sha256") and hexes(record["retired_lock_py"], "sha256")
            and isinstance(record["retired_fixed_checkers"], list)
            and all(isinstance(x, str) and HEX64.fullmatch(x) for x in record["retired_fixed_checkers"])):
        raise LockError("status record", "a checker entry is not in the record's form")
    if own_sha256 in {x["sha256"] for x in record["retired_lock_py"]}:
        raise LockError("status record", "this lock.py is retired; check the notice with the current fixed checker "
                                         "against the same snapshot, then install the release that replaces it")
    return record["issued"]


def snapshot_env(tmp: Path) -> dict[str, str]:
    """The environment of the check's own git calls: what authentication needs stays (credential helpers come from
    the user's git configuration, an SSH agent from SSH_AUTH_SOCK); every other GIT_ variable, which could redirect
    the repository or inject configuration, goes; replace refs and graft files are switched off."""
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith("GIT_") or k.upper() in GIT_KEEP}
    grafts = tmp / "no-grafts"
    try:
        grafts.write_bytes(b"")
    except OSError:
        raise LockError("snapshot", "the check's private folder cannot be written") from None
    env.update(GIT_NO_REPLACE_OBJECTS="1", GIT_GRAFT_FILE=str(grafts), GIT_TERMINAL_PROMPT="0")
    return env


def snapshot_git(root: Path, env: dict[str, str], *args: str, timeout: int = 120) -> bytes:
    program = trusted_program("git", root)
    if not program:
        raise LockError("snapshot", "git could not be run (not found on PATH outside the repository)")
    try:
        run = subprocess.run([program, "-C", str(root), "--no-replace-objects", "-c", "core.commitGraph=false",
                              "-c", "core.autocrlf=false", "-c", "core.eol=lf", *args], capture_output=True, env=env,
                             timeout=timeout, stdin=subprocess.DEVNULL)
    except (OSError, subprocess.TimeoutExpired):
        raise LockError("snapshot", "git could not be run to completion") from None
    if run.returncode != 0:
        raise LockError("snapshot", "a git command failed (is origin reachable, and is this a clone of the project?)")
    return run.stdout


# The three questions put to the remote; tests replace them, since a test remote is a local folder.
def contacted_url(root: Path, env: dict[str, str]) -> str:
    """The URL git contacts for origin, after its own URL rewriting; only its canonical form is ever used."""
    return snapshot_git(root, env, "ls-remote", "--get-url", "origin").decode("utf-8", "replace").strip()


def remote_head_branch(root: Path, env: dict[str, str]) -> str | None:
    """The branch the remote's HEAD names (`ref: refs/heads/<branch>\tHEAD`), or None."""
    out = snapshot_git(root, env, "ls-remote", "--symref", "origin", "HEAD").decode("utf-8", "replace")
    for line in out.split("\n"):
        m = re.fullmatch(r"ref: refs/heads/(\S+)\tHEAD", line)
        if m:
            return m.group(1)
    return None


def remote_tip(root: Path, env: dict[str, str], branch: str) -> list[str]:
    """Every object id the remote reports for exactly refs/heads/<branch> (ls-remote matches by suffix)."""
    ref = f"refs/heads/{branch}"
    out = snapshot_git(root, env, "ls-remote", "origin", ref).decode("utf-8", "replace")
    return [oid for oid, _, name in (line.partition("\t") for line in out.split("\n") if line) if name == ref]


def snapshot_read(root: Path, env: dict[str, str], commit: str, rel: str, cap: int = MAX_FILE_BYTES) -> bytes | None:
    """The bytes of a regular file in the snapshot commit, or None when it is absent; a link or folder is refused."""
    out = snapshot_git(root, env, "ls-tree", "-z", commit, "--", rel)
    if not out:
        return None
    meta, _, name = out.rstrip(b"\0").partition(b"\t")
    if name.decode("utf-8", "replace") != rel or meta.split(b" ")[0] not in (b"100644", b"100755"):
        raise LockError("snapshot", f"{safe(rel)} is not a regular file in the snapshot")
    size = snapshot_git(root, env, "cat-file", "-s", f"{commit}:{rel}").strip()
    if not size.isdigit():
        raise LockError("snapshot", f"the size of {safe(rel)} cannot be read")
    if int(size) > cap:  # the size first: a blob over the cap is never read whole
        raise LockError("snapshot", f"{safe(rel)} is too large")
    blob = snapshot_git(root, env, "cat-file", "blob", f"{commit}:{rel}")
    if len(blob) > cap:
        raise LockError("snapshot", f"{safe(rel)} is too large")
    return blob


def take_snapshot(root: Path, expected_url: str, branch: str, env: dict[str, str]) -> dict:
    """The exact tip of the default branch on the intended remote, resolved once (ADR 0014, "The snapshot")."""
    expected = canonical_repository_url(expected_url)
    if expected is None:
        raise LockError("snapshot", "--expected-url is not an https or ssh repository URL in a form that can be made "
                                    "canonical (no query, fragment or local path)")
    if not BRANCH_RE.fullmatch(branch) or ".." in branch or branch.endswith((".lock", "/", ".")) or len(branch) > 200:
        raise LockError("snapshot", "--branch is not a branch name")
    contacted = canonical_repository_url(contacted_url(root, env))
    if contacted != expected:
        raise LockError("snapshot", f"origin is not the expected repository: git contacts "
                                    f"{contacted or 'a URL that cannot be made canonical'}, expected {expected}")
    head = remote_head_branch(root, env)
    if head != branch:
        raise LockError("snapshot", f"the remote's default branch is {safe(head) if head else 'not reported'}, "
                                    f"not {safe(branch)}")
    tips = remote_tip(root, env, branch)
    if len(tips) != 1 or not OBJECT_ID.fullmatch(tips[0]):
        raise LockError("snapshot", "the remote does not report exactly one commit for the default branch")
    commit = tips[0]
    try:
        snapshot_git(root, env, "cat-file", "-e", f"{commit}^{{commit}}")
    except LockError:
        raise LockError("snapshot", "the default branch's tip is not in this clone; fetch it, then run the check "
                                    "again") from None
    when = snapshot_git(root, env, "show", "-s", "--format=%cI", commit).decode("ascii", "replace").strip()
    return {"commit": commit, "date": when, "branch": branch, "url": expected}


def verify_notice(notice: bytes, signature: bytes, entries: bytes, revoked_data: bytes | None,
                  root: Path) -> str:
    """ssh-keygen -Y verify under norma-notice against the canonical notice-signers entries, with the snapshot's
    revoked keys, at the current time. Returns the fingerprint of the exact Good line; raises LockError otherwise."""
    if not signature.startswith(BEGIN) or not signature.rstrip(b"\n").endswith(END):
        raise LockError("notice", "NOTICE.md.sig is not an SSH signature block")
    lines = entries.decode("ascii").splitlines()
    fingerprints = [key_fingerprint(line.split(" ")[-1]) for line in lines]
    binary = ssh_keygen(root)
    if len(lines) == 2:
        found = openssh_version(binary)
        if found is None or found < OPENSSH_MIN:
            raise LockError("notice", f"a rotation's second line needs OpenSSH {OPENSSH_MIN[0]}.{OPENSSH_MIN[1]} or "
                                      "later for ssh-keygen")
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp).resolve()
        (tmp_path / "notice.signers").write_bytes(entries)
        (tmp_path / "notice.sig").write_bytes(signature)
        argv = [binary, "-Y", "verify", "-f", str(tmp_path / "notice.signers"), "-I", PRINCIPAL,
                "-n", NOTICE_NAMESPACE, "-s", str(tmp_path / "notice.sig")]
        if revoked_data is not None:
            (tmp_path / "revoked").write_bytes(revoked_data)
            argv += ["-r", str(tmp_path / "revoked")]
        try:
            run = subprocess.run(argv, input=notice, capture_output=True, cwd=tmp_path, env=ssh_env(),
                                 timeout=SSH_TIMEOUT)
        except (OSError, subprocess.TimeoutExpired):
            raise LockError("notice", "ssh-keygen could not run to completion") from None
    good = GOOD_NOTICE.fullmatch(run.stdout.strip())
    if run.returncode != 0 or not good:  # any non-zero exit: Windows' ssh-keygen exits 127 for a revoked key
        raise LockError("notice", "the notice's signature does not verify under norma-notice against the trusted keys")
    fingerprint = good.group(2).decode("ascii")
    if fingerprint not in fingerprints:
        raise LockError("notice", "the signing key is not one of the trusted keys")
    return fingerprint


def check_notice(root: Path, version: str, release_folder: Path, expected_url: str, branch: str,
                 status_record: Path, downgrade: str | None = None, today: date | None = None) -> dict:
    """`lock.py notice`: authenticate the complete notice of release vX.Y.Z against a fresh snapshot."""
    today = today or date.today()
    version_tuple(version)
    folder = no_link_on_path(release_folder, "release folder")
    if folder.name != f"v{version}":
        raise LockError("release folder", "its name is not v followed by --version")
    try:
        if not stat.S_ISDIR(os.lstat(folder).st_mode):
            raise LockError("release folder", "it is not a folder")
        own = Path(__file__).read_bytes()
    except OSError:
        raise LockError("release folder", "it, or this checker, cannot be read") from None
    # each file's path is checked again just before its single read, so a folder swapped for a link in between is
    # refused too
    notice = read_once(no_link_on_path(folder / NOTICE_FILE, "NOTICE.md"), MAX_NOTICE_BYTES, "NOTICE.md")
    signature = read_once(no_link_on_path(folder / NOTICE_SIG, "NOTICE.md.sig"), MAX_NOTICE_SIG_BYTES, "NOTICE.md.sig")
    record = read_once(no_link_on_path(status_record, "status record"), MAX_STATUS_BYTES, "status record")
    with tempfile.TemporaryDirectory() as tmp:
        env = snapshot_env(Path(tmp))
        snap = take_snapshot(root, expected_url, branch, env)
        read = lambda rel: snapshot_read(root, env, snap["commit"], rel)  # noqa: E731
        raw_lock = read(LOCK_REL)
        if raw_lock is None:
            raise LockError("snapshot", "the snapshot has no core.lock.json; a first install uses the fixed checker")
        installed = parse_lock(raw_lock)["norma"]
        raw_project = read(PROJECT_REL)
        if raw_project is None:
            raise LockError("snapshot", "the snapshot has no .claude/project.json")
        paths = lock_paths(raw_project)
        signers = read(paths["signers_file"])
        if signers is None:
            raise LockError("snapshot", "the snapshot has no signers file")
        revoked = read(paths["revoked_file"]) if paths["revoked_file"] else None
        if paths["revoked_file"] and revoked is None:
            raise LockError("snapshot", "lock.revoked_file names a file the snapshot does not hold")
        try:
            entries = notice_signer_lines(signers.decode("utf-8-sig"), today)
        except UnicodeDecodeError:
            raise LockError("snapshot", "the signers file is not UTF-8 text") from None
        stored = read(paths["notice_signers_file"])
        bump = version_tuple(installed) < NOTICE_FROM  # the second check at the 0.5.0 bump (ADR 0014)
        if not bump:
            if own != read(SELF_REL):
                raise LockError("checker", "this lock.py is not the snapshot's own copy; take it from the snapshot "
                                           "commit and run it with python -I")
            if stored is None:
                raise LockError("snapshot", "the snapshot has no notice-signers file")
            if stored != entries:
                raise LockError("snapshot", "the notice-signers file differs from the one derived from the "
                                            "signers file")
    issued = check_status_record(record, hashlib.sha256(own).hexdigest(), today)
    header = parse_notice(notice, version)
    fingerprint = verify_notice(notice, signature, entries, revoked, root)
    if fingerprint != header["signing-key-fingerprint"]:
        raise LockError("notice", "the key that signed the notice is not the key its signing-key field names")
    marker = None
    if downgrade is not None and version_tuple(version) >= version_tuple(installed):
        raise LockError("notice", "--downgrade is only for a version lower than the installed one")
    if version_tuple(version) < version_tuple(installed):
        if downgrade is None:
            raise LockError("notice", "the version is lower than the installed one; a rollback needs --downgrade with "
                                      "a reason")
        if not 1 <= len(downgrade) <= 300 or not downgrade.isprintable():
            raise LockError("notice", "--downgrade needs a printable reason of 1 to 300 characters")
        marker = {"from": installed, "to": version, "reason": downgrade}
    return {"version": version, "fingerprint": fingerprint, "snapshot": snap, "issued": issued,
            "files": header["files"], "units": header["units"], "downgrade": marker, "bump": bump,
            "installed": installed}


def notice_signers(root: Path, write: bool) -> str:
    """`lock.py notice-signers [--write]`: compare the working tree's notice-signers file with the one derived from its
    signers file, or write the derived one (at the 0.5.0 bump, and in every key change that changes signer lines)."""
    source = Source(root)
    raw_project = source.read(PROJECT_REL)
    if raw_project is None:
        raise LockError("project", ".claude/project.json is missing")
    paths = lock_paths(raw_project)
    signers = source.read(paths["signers_file"])
    if signers is None:
        raise LockError("signers", "the signers file is missing")
    try:
        derived = notice_signer_lines(signers.decode("utf-8-sig"))
    except UnicodeDecodeError:
        raise LockError("signers", "the signers file is not UTF-8 text") from None
    rel = paths["notice_signers_file"]
    current = source.read(rel)
    # a checkout may turn LF into CRLF (Git for Windows does by default); the committed blob, which the snapshot
    # checks read, keeps LF
    if current is not None and current.replace(b"\r\n", b"\n") == derived:
        return "matches"
    if not write:
        raise LockError("notice-signers", "the notice-signers file is missing or differs from the one derived from the "
                                          "signers file; run lock.py notice-signers --write")
    if _link_on_path(Path(root), rel):
        raise LockError("notice-signers", "the notice-signers file or a folder on its path is a link")
    target = Path(root) / rel
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        if _link_on_path(Path(root), rel):  # a folder made just now is checked again
            raise LockError("notice-signers", "a folder on the notice-signers file's path is a link")
        # a new file of its own (O_EXCL), never a name a planted link could hold, then moved over the target
        fd, temporary = tempfile.mkstemp(prefix=".notice-signers-", dir=target.parent)
        try:
            with os.fdopen(fd, "wb") as fh:
                fh.write(derived)
            os.replace(temporary, target)
        except BaseException:
            Path(temporary).unlink(missing_ok=True)
            raise
    except OSError:
        raise LockError("notice-signers", "the notice-signers file cannot be written") from None
    return "written"


# ---------------------------------------------------------------- the read-only key-change check (ADR 0014)
#
# `lock.py key-change-check` runs the reviewer's checks of one key change (ADR 0014, "Key changes", and its amendment
# "a read-only key-change check"): against the snapshot and the pull request's head commit, which must contain it.
# Every trust input of both sides is read from git objects, never the working tree; it writes nothing, calls no
# hosting service and decides no approval. Malformed or ambiguous history fails closed. Its output is the
# Key-change evidence record.

KEY_CHANGE_NAMESPACE = "norma-key-change"
KEY_CHANGE_KINDS = ("planned-rotation", "complete", "abandon", "extend", "restore", "revoke", "path-change",
                    "setup-0.5.0", "revert-0.5.0", "enroll", "identity-change")
KC_STATEMENT_KINDS = ("abandon", "extend", "restore")
KC_STATEMENT_DAYS = 30
KC_MAX_STATEMENT_BYTES = 16384
KC_SERIAL_MAX = 2 ** 53 - 1
KC_MAX_WALK = 5000  # first-parent commits the history walks read at most; a longer history fails closed
KC_EXTEND_DAYS = (60, MAX_VALID_DAYS)  # an extended date: at least 60 days and at most one year ahead
KC_RESTORE_DAYS = (60, 90)  # a restored former key returns as the expiring line, 60 to 90 days ahead
KC_FINGERPRINT = re.compile(r"SHA256:[A-Za-z0-9+/]{43}")
KC_PROJECT_ID = re.compile(r"GL-[0-9]{4}")
KC_REV = re.compile(r"[A-Za-z0-9_][A-Za-z0-9._/^~@{}-]{0,199}")
KC_STATEMENT_FIELDS = ("kind", "serial", "issued", "valid-until", "signer-domain", "project-id", "repository-url",
                       "outgoing", "incoming", "release")
GOOD_KEY_CHANGE = re.compile(rb'Good "norma-key-change" signature for norma-release with (\S+) key '
                             rb'(SHA256:[A-Za-z0-9+/]{43})')
REVOKED_LINE = re.compile(rf"(?:(?P<options>\S*\"[^\"]*\"\S*|(?!{KEYTYPE}\s)\S+)\s+)?"
                          rf"(?P<type>{KEYTYPE})\s+(?P<key>[A-Za-z0-9+/]+={{0,2}})(?:\s.*)?")
# the fields each kind may change; every other field of both sides stays byte-identical
KC_CHANGES = {
    "complete": {"signers file"},
    "abandon": {"signers file", "lock.key_change_serial"},
    "extend": {"signers file", "lock.key_change_serial"},
    "restore": {"signers file", "lock.key_change_serial"},
    "revoke": {"signers file", "revoked-keys file", "lock.revoked_file"},
    "path-change": {"lock.signers_file", "lock.revoked_file", "lock.notice_signers_file"},
    "setup-0.5.0": {"revoked-keys file", "lock.revoked_file", "lock.notice_signers_file", "core.lock.json"},
    "revert-0.5.0": {"lock.notice_signers_file", "core.lock.json"},
    "enroll": {"lock.key_change_serial", "lock.repository_url"},
    "identity-change": {"lock.repository_url", "usage.project_id"},
}
# the inputs each kind takes: (required, optional)
KC_INPUTS = {
    "complete": (set(), set()),
    "abandon": ({"statement", "statement_sig"}, set()),
    "extend": ({"statement", "statement_sig"}, set()),
    "restore": ({"statement", "statement_sig", "former_commit"}, set()),
    "revoke": ({"revoke", "new_key"}, set()),
    "path-change": (set(), set()),
    "setup-0.5.0": (set(), {"ci_revoked"}),
    "revert-0.5.0": (set(), set()),
    "enroll": ({"record_project_id", "record_url"}, set()),
    "identity-change": ({"record_project_id", "record_url"}, set()),
}


def revoked_fingerprints(data: bytes | None, where: str) -> frozenset[str]:
    """The key fingerprints of a revoked-keys file: each line that is not blank or a comment is `<type> <base64>`,
    optionally with options before them. A line that cannot be read, a binary key revocation list or text that is not
    printable ASCII fails closed: a key this check cannot see could be one ssh-keygen refuses, or the reverse."""
    if data is None:
        return frozenset()
    if data.startswith(b"SSHKRL"):
        raise LockError(where, "the revoked-keys file is a binary key revocation list, which this check cannot read")
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise LockError(where, "the revoked-keys file is not UTF-8 text") from None
    found = set()
    for raw in text.split("\n"):
        line = raw.removesuffix("\r").strip()
        if not line or line.startswith("#"):
            continue
        m = REVOKED_LINE.fullmatch(line)
        if not m or not all(" " <= ch <= "~" or ch == "\t" for ch in line):
            raise LockError(where, "a line of the revoked-keys file cannot be read as a key")
        try:
            found.add(key_fingerprint(m.group("key")))
        except LockError:
            raise LockError(where, "a key in the revoked-keys file is not valid base64") from None
    return frozenset(found)


def _kc_configuration(entries: list[tuple[str, str, str | None]]) -> list[tuple[str, str]]:
    """[(fingerprint, valid-before or 'open')] of select_signers' lines, the open-ended line first."""
    return [(key_fingerprint(key), ends or "open") for _, key, ends in entries]


def _kc_signers(root: Path, env: dict[str, str], commit: str, rel: str, today: date) -> dict:
    raw = snapshot_read(root, env, commit, rel)
    if raw is None:
        raise LockError("signers", "the signers file is missing")
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        raise LockError("signers", "the signers file is not UTF-8 text") from None
    entries = select_signers(text, today)
    return {"signers": raw, "text": text, "entries": entries, "config": _kc_configuration(entries)}


def _kc_side(root: Path, env: dict[str, str], commit: str, today: date, where: str) -> dict:
    """Every trust input of one side (the snapshot or the head), read from the commit's objects."""
    try:
        raw_project = snapshot_read(root, env, commit, PROJECT_REL)
        if raw_project is None:
            raise LockError("project", ".claude/project.json is missing")
        paths = lock_paths(raw_project)
        project = json.loads(raw_project.decode("utf-8-sig"), object_pairs_hook=_no_duplicates)
        section = project["lock"]
        serial = section.get("key_change_serial")
        if serial is not None and (type(serial) is not int or not 0 <= serial <= KC_SERIAL_MAX):
            raise LockError("project", f"lock.key_change_serial is not an integer from 0 to {KC_SERIAL_MAX}")
        url = section.get("repository_url")
        if url is not None and (not isinstance(url, str) or canonical_repository_url(url) != url):
            raise LockError("project", "lock.repository_url is not a canonical https or ssh repository URL")
        usage = project.get("usage")
        project_id = usage.get("project_id") if isinstance(usage, dict) else None
        if "usage" in project and not (isinstance(project_id, str) and KC_PROJECT_ID.fullmatch(project_id)):
            raise LockError("project", "usage.project_id is not GL- and four digits")
        side = _kc_signers(root, env, commit, paths["signers_file"], today)
        revoked = snapshot_read(root, env, commit, paths["revoked_file"]) if paths["revoked_file"] else None
        if paths["revoked_file"] and revoked is None:
            raise LockError("revoked", "lock.revoked_file names a file the commit does not hold")
        raw_lock = snapshot_read(root, env, commit, LOCK_REL)
        if raw_lock is None:
            raise LockError("lock", "the commit has no core.lock.json")
        installed = parse_lock(raw_lock)
        side.update(paths=paths, serial=serial, url=url, project_id=project_id, revoked=revoked, lock_bytes=raw_lock,
                    notice_key="notice_signers_file" in section,
                    revoked_set=revoked_fingerprints(revoked, "revoked"),
                    notice=snapshot_read(root, env, commit, paths["notice_signers_file"]),
                    installed=installed["norma"], signer=installed["signer"], downgrade=installed.get("downgrade"))
        return side
    except LockError as exc:
        raise LockError(where, str(exc)) from None
    except (ValueError, KeyError, TypeError, AttributeError, RecursionError):
        raise LockError(where, ".claude/project.json cannot be read") from None


def _kc_fields(side: dict) -> dict:
    return {"signers file": side["signers"], "revoked-keys file": side["revoked"],
            "lock.signers_file": side["paths"]["signers_file"], "lock.revoked_file": side["paths"]["revoked_file"],
            "lock.notice_signers_file": side["paths"]["notice_signers_file"],
            "lock.key_change_serial": side["serial"], "lock.repository_url": side["url"],
            "usage.project_id": side["project_id"], "core.lock.json": side["lock_bytes"]}


def _kc_other_lines(side: dict) -> list[str]:
    """The signers file's lines other than blank lines, comments and the selected release signer lines themselves
    (principal norma-release, a selected key); a line of another principal is kept, even with a release key."""
    keys = {key for _, key, _ in side["entries"]}
    out = []
    for raw in side["text"].split("\n"):
        line = raw.removesuffix("\r").strip()
        if not line or line.startswith("#"):
            continue
        m = SIGNER_LINE.fullmatch(line)
        if m and m.group("principals") == PRINCIPAL and m.group("key") in keys:
            continue
        out.append(line)
    return out


def _kc_commit(root: Path, env: dict[str, str], rev: str, what: str) -> str:
    """The full id of the commit a revision names in this clone."""
    if not isinstance(rev, str) or not KC_REV.fullmatch(rev):
        raise LockError(what, "it is not a commit id or revision")
    try:
        out = snapshot_git(root, env, "rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}")
    except LockError:
        raise LockError(what, "it does not name a commit in this clone") from None
    commit = out.decode("ascii", "replace").strip()
    if not OBJECT_ID.fullmatch(commit):
        raise LockError(what, "it does not name exactly one commit")
    return commit


def _kc_no_partial_clone(root: Path, env: dict[str, str]) -> None:
    """A partial (promisor) clone could fetch a missing object from the remote while the check reads it: refused
    before any read, as is a value that cannot be read."""
    for key in ("remote.origin.promisor", "extensions.partialclone"):
        try:
            value = snapshot_git(root, env, "config", "--default", "", "--get", key).strip()
        except LockError:
            raise LockError("clone", f"git config {key} cannot be read") from None
        if value:
            raise LockError("clone", "this is a partial clone; make a full clone of the project and check there")


def _kc_first_parents(root: Path, env: dict[str, str], commit: str) -> list[str]:
    """The first-parent history from a commit back, newest first; a shallow clone or a history longer than the walk
    reads fails closed."""
    if snapshot_git(root, env, "rev-parse", "--is-shallow-repository").strip() != b"false":
        raise LockError("history", "the clone is shallow; fetch the full history of the default branch")
    out = snapshot_git(root, env, "rev-list", "--first-parent", f"--max-count={KC_MAX_WALK + 1}", commit, "--")
    commits = out.decode("ascii", "replace").split()
    if not commits or not all(OBJECT_ID.fullmatch(c) for c in commits):
        raise LockError("history", "the first-parent history cannot be read")
    if len(commits) > KC_MAX_WALK:  # owner's decision of 2026-11-16: no bypass
        raise LockError("history", f"history limit exceeded: the default branch has more than {KC_MAX_WALK} "
                                   "first-parent commits; this check has no bypass, and such a project waits for "
                                   "the follow-up history walk recorded in #61's follow-ups")
    return commits


# a `lock.py verify` call in a workflow and its `--revoked <file>`: the same reading as lint.py's
KC_VERIFY_CALL = re.compile(r"lock\.py[\"']?\s+verify\b")
KC_REVOKED_ARG = re.compile(r"--revoked(?:=|\s+)([\"']?)([^\s\"']+)\1")
# every spelling argparse takes for --revoked (a unique prefix from --re on): counted on every line, so a --revoked
# the call reading does not follow is seen and refused
KC_REVOKED_TOKEN = re.compile(r"--re(?:v(?:o(?:k(?:e(?:d)?)?)?)?)?(?![A-Za-z0-9_-])")


def _kc_logical_lines(lines: list[str]) -> list[tuple[int, str, list[str]]]:
    """(first line index, joined text, its physical lines) of each logical line: a line ending in a backslash joins
    the next one, but a comment line ends its logical line and is never joined onto, as a shell reads it."""
    out = []
    n = 0
    while n < len(lines):
        first, text = n, lines[n]
        if not text.lstrip().startswith("#"):
            while text.rstrip().endswith("\\") and n + 1 < len(lines) and not lines[n + 1].lstrip().startswith("#"):
                n += 1
                text = text.rstrip()[:-1] + " " + lines[n]
        n += 1
        out.append((first, text, lines[first:n]))
    return out


def _kc_ci_revoked(root: Path, env: dict[str, str], commit: str) -> tuple[set[str], int]:
    """(the revoked-keys files the commit's CI passes to `lock.py verify --revoked`, the number of `lock.py verify`
    calls that pass none), read from its own .github (the snapshot's, never the pull request's): the verify calls of
    every .yml and .yaml file (workflows and composite actions), and a --revoked in any file. Fails closed on a value
    built from a variable or that is no repository-relative path, on any --revoked the reading does not follow (a
    quoted value with spaces, a call continued other than by a trailing backslash, an abbreviation, --revoked outside
    a verify call of a YAML file, as in a script under .github), and on an entry that is not a regular file."""
    out = snapshot_git(root, env, "ls-tree", "-r", "-z", commit, "--", ".github/")
    found: set[str] = set()
    without = 0
    for entry in out.split(b"\0"):
        if not entry:
            continue
        meta, _, name = entry.partition(b"\t")
        rel = name.decode("utf-8", "replace")
        if meta.split(b" ")[0] not in (b"100644", b"100755"):
            raise LockError("ci", f"{safe(rel)} is not a regular file in the snapshot (a link or a submodule below "
                                  ".github, which this check cannot read)")
        raw = snapshot_read(root, env, commit, rel) or b""
        if not rel.lower().endswith((".yml", ".yaml")):
            # any byte maps to one Latin-1 character; a script in UTF-16 or UTF-32 is searched too, at every alignment
            if KC_REVOKED_TOKEN.search(raw.decode("latin-1")) or any(
                    KC_REVOKED_TOKEN.search(raw[shift:].decode(encoding, "ignore"))
                    for encoding, width in (("utf-16-le", 2), ("utf-16-be", 2), ("utf-32-le", 4), ("utf-32-be", 4))
                    for shift in range(width)):
                raise LockError("ci", f"{safe(rel)}: a --revoked outside a YAML file, which this check cannot follow; "
                                      "pass --revoked <file> on the lock.py verify call of the workflow itself")
            continue
        try:
            lines = raw.decode("utf-8-sig").splitlines()
        except UnicodeDecodeError:
            raise LockError("ci", f"{safe(rel)} is not UTF-8 text") from None
        for first, text, physical in _kc_logical_lines(lines):
            comment = text.lstrip().startswith("#")
            tokens = 0 if comment else sum(len(KC_REVOKED_TOKEN.findall(line)) for line in physical)
            call = not comment and KC_VERIFY_CALL.search(text)
            matches = list(KC_REVOKED_ARG.finditer(text)) if call else []
            if tokens != len(matches):
                raise LockError("ci", f"{safe(rel)} line {first + 1}: a --revoked this check cannot follow (a quoted "
                                      "value with spaces, a call continued other than by a trailing backslash, an "
                                      "abbreviated option, or --revoked outside a lock.py verify call); write it as "
                                      "--revoked <file> on the verify call")
            if not call:
                continue
            if not matches:
                without += 1
            for m in matches:
                value = m.group(2).replace("\\", "/").removeprefix("./")
                if not (len(value) <= 200 and REL_PATH.fullmatch(value)
                        and not any(seg in (".", "..") for seg in value.split("/"))):
                    raise LockError("ci", f"a lock.py verify call in {safe(rel)} passes --revoked a value this check "
                                          "cannot follow (a variable or no repository-relative path)")
                found.add(value)
    return found, without


def _kc_history_paths(root: Path, env: dict[str, str], commit: str) -> dict[str, str | None]:
    try:
        raw_project = snapshot_read(root, env, commit, PROJECT_REL)
        if raw_project is None:
            raise LockError("project", ".claude/project.json is missing")
        return lock_paths(raw_project)
    except LockError as exc:
        raise LockError("history", f"commit {commit}: {exc}") from None


def _kc_pre_rotation(root: Path, env: dict[str, str], snapshot: str, incoming: str, outgoing: str,
                     today: date) -> tuple[str, bytes]:
    """(commit, signers bytes) of the first-parent parent of the commit that first made the signers file rotating:
    walk back from the snapshot while the file rotates from the outgoing to the incoming key (its date may have been
    extended); the first commit that does not rotate must trust exactly the outgoing key. Anything else is refused."""
    for commit in _kc_first_parents(root, env, snapshot):
        paths = _kc_history_paths(root, env, commit)
        try:
            side = _kc_signers(root, env, commit, paths["signers_file"], today)
        except LockError as exc:
            raise LockError("history", f"commit {commit}: {exc}") from None
        fingerprints = [f for f, _ in side["config"]]
        if len(fingerprints) == 2:
            if fingerprints != [incoming, outgoing]:
                raise LockError("history", f"commit {commit} rotates between other keys; the rotation's start is "
                                           "ambiguous")
            continue
        if side["config"] != [(outgoing, "open")]:
            raise LockError("history", f"commit {commit}, before the rotation, does not trust exactly the outgoing "
                                       "key")
        return commit, side["signers"]
    raise LockError("history", "no commit before the rotation was found in the first-parent history")


def _kc_never_revoked(root: Path, env: dict[str, str], snapshot: str, former_commit: str, fingerprint: str,
                      today: date) -> int:
    """Execution requirement 7: the former commit is a first-parent commit of the snapshot that trusted the key, and no
    first-parent commit of the whole history up to the snapshot held the key in its revoked-keys file, read at the
    path each commit's lock.revoked_file names (renames followed; a merge commit's tree holds what it merged). A
    commit without a project.json, or whose project.json has no lock section, holds no revoked keys; anything else
    that cannot be read fails closed. Returns the number of commits read."""
    commits = _kc_first_parents(root, env, snapshot)
    if former_commit not in commits:
        raise LockError("history", "--former-commit is not a first-parent commit of the snapshot")
    paths = _kc_history_paths(root, env, former_commit)
    try:
        trusted = _kc_signers(root, env, former_commit, paths["signers_file"], today)["config"]
    except LockError as exc:
        raise LockError("history", f"commit {former_commit}: {exc}") from None
    if fingerprint not in [f for f, _ in trusted]:
        raise LockError("history", "the former commit's signers file does not trust the former key")
    for commit in commits:
        try:
            raw_project = snapshot_read(root, env, commit, PROJECT_REL)
            if raw_project is None:
                continue  # before the project file existed: no revoked-keys file can exist there
            parsed = json.loads(raw_project.decode("utf-8-sig"), object_pairs_hook=_no_duplicates)
            if not isinstance(parsed, dict):
                raise LockError("history", "a historic project.json is not a JSON object")
            if "lock" not in parsed:
                continue  # before the lock section existed
            paths = lock_paths(raw_project)
            revoked = snapshot_read(root, env, commit, paths["revoked_file"]) if paths["revoked_file"] else None
            if paths["revoked_file"] and revoked is None:
                raise LockError("revoked", "lock.revoked_file names a file the commit does not hold")
            revoked_set = revoked_fingerprints(revoked, "revoked")
        except LockError as exc:
            raise LockError("history", f"commit {commit}: {exc}") from None
        except (ValueError, TypeError, RecursionError):
            raise LockError("history", f"commit {commit}: .claude/project.json cannot be read") from None
        if fingerprint in revoked_set:
            raise LockError("history", f"the former key was in the revoked-keys file at commit {commit}; a revoked "
                                       "key is never restored")
    return len(commits)


def _kc_entry(value: str) -> tuple[str, str]:
    parts = value.split(" ")
    if len(parts) != 2 or not KC_FINGERPRINT.fullmatch(parts[0]) or not (
            parts[1] == "open" or re.fullmatch(r"[0-9]{8}", parts[1])):
        raise LockError("statement", "a current or target line is not '<fingerprint> <YYYYMMDD or open>'")
    if parts[1] != "open":
        try:
            datetime.strptime(parts[1], "%Y%m%d")
        except ValueError:
            raise LockError("statement", "a current or target date is not a real date") from None
    return parts[0], parts[1]


def _kc_config_shape(config: list[tuple[str, str]], what: str) -> None:
    if not 1 <= len(config) <= 2 or config[0][1] != "open" or (len(config) == 2 and config[1][1] == "open") \
            or len({f for f, _ in config}) != len(config):
        raise LockError("statement", f"the {what} configuration is not one open-ended entry plus at most one "
                                     "expiring entry for another key")


def parse_key_change_statement(data: bytes) -> dict:
    """A key-change statement in the exact form `release.py key-change` writes: ASCII, LF, first line
    `Norma key-change statement`, the fixed fields in order, then the current and the target lines."""
    if len(data) > KC_MAX_STATEMENT_BYTES:
        raise LockError("statement", "it is too large")
    if b"\r" in data or not data.endswith(b"\n"):
        raise LockError("statement", "it must use LF line ends and end with a line feed")
    try:
        lines = data.decode("ascii").split("\n")[:-1]
    except UnicodeDecodeError:
        raise LockError("statement", "it is not ASCII text") from None
    if not lines or lines[0] != "Norma key-change statement":
        raise LockError("statement", "its first line is not 'Norma key-change statement'")
    rest = lines[1:]
    fields: dict = {}
    for name, line in zip(KC_STATEMENT_FIELDS, rest):
        key, sep, value = line.partition(": ")
        if key != name or not sep or not value or value != value.strip() or not value.isprintable():
            raise LockError("statement", "its fields are missing, unknown or out of order")
        fields[name] = value
    if len(fields) != len(KC_STATEMENT_FIELDS):
        raise LockError("statement", "its fields are missing, unknown or out of order")
    current: list[tuple[str, str]] = []
    target: list[tuple[str, str]] = []
    for line in rest[len(KC_STATEMENT_FIELDS):]:
        key, sep, value = line.partition(": ")
        if key == "current" and sep and not target:
            current.append(_kc_entry(value))
        elif key == "target" and sep and current:
            target.append(_kc_entry(value))
        else:
            raise LockError("statement", "a line after the fields is not a current or target line, in that order")
    _kc_config_shape(current, "current")
    _kc_config_shape(target, "target")
    if fields["kind"] not in KC_STATEMENT_KINDS:
        raise LockError("statement", f"its kind is not one of {', '.join(KC_STATEMENT_KINDS)}")
    if not re.fullmatch(r"[1-9][0-9]{0,15}", fields["serial"]) or int(fields["serial"]) > KC_SERIAL_MAX:
        raise LockError("statement", "its serial is not a positive integer in range")
    try:
        if not (ISO_DATE.fullmatch(fields["issued"]) and ISO_DATE.fullmatch(fields["valid-until"])):
            raise ValueError
        issued, until = date.fromisoformat(fields["issued"]), date.fromisoformat(fields["valid-until"])
    except ValueError:
        raise LockError("statement", "its issued and valid-until are not real dates") from None
    if fields["signer-domain"] != PRINCIPAL:
        raise LockError("statement", f"its signer domain is not {PRINCIPAL}")
    if not KC_PROJECT_ID.fullmatch(fields["project-id"]):
        raise LockError("statement", "its project-id is not GL- and four digits")
    if canonical_repository_url(fields["repository-url"]) != fields["repository-url"]:
        raise LockError("statement", "its repository-url is not a canonical repository URL")
    if not (KC_FINGERPRINT.fullmatch(fields["outgoing"]) and KC_FINGERPRINT.fullmatch(fields["incoming"])) \
            or fields["outgoing"] == fields["incoming"]:
        raise LockError("statement", "its outgoing and incoming are not two different fingerprints")
    if not (fields["release"].startswith("v") and VERSION_RE.fullmatch(fields["release"][1:])):
        raise LockError("statement", "its release is not vX.Y.Z")
    return {**fields, "serial": int(fields["serial"]), "issued": issued, "valid-until": until,
            "current": current, "target": target}


def verify_key_change_statement(statement: bytes, signature: bytes, entry: tuple[str, str, str | None],
                                revoked_data: bytes | None, root: Path) -> str:
    """ssh-keygen -Y verify under norma-key-change against the one signer line the kind names (with its valid-before),
    with the snapshot's revoked keys, at the current time. Returns the fingerprint of the exact Good line."""
    if not signature.startswith(BEGIN) or not signature.rstrip(b"\n").endswith(END):
        raise LockError("statement", "the statement's signature file is not an SSH signature block")
    key_type, key, ends = entry
    binary = ssh_keygen(root)
    if ends:
        found = openssh_version(binary)
        if found is None or found < OPENSSH_MIN:
            raise LockError("statement", f"an expiring signer line needs OpenSSH {OPENSSH_MIN[0]}.{OPENSSH_MIN[1]} or "
                                         "later for ssh-keygen")
    options = f'namespaces="{KEY_CHANGE_NAMESPACE}"' + (f',valid-before="{ends}"' if ends else "")
    try:
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            tmp_path = Path(tmp).resolve()
            line = f"{PRINCIPAL} {options} {key_type} {key}\n"
            (tmp_path / "statement.signers").write_bytes(line.encode("ascii"))
            (tmp_path / "statement.sig").write_bytes(signature)
            argv = [binary, "-Y", "verify", "-f", str(tmp_path / "statement.signers"), "-I", PRINCIPAL,
                    "-n", KEY_CHANGE_NAMESPACE, "-s", str(tmp_path / "statement.sig")]
            if revoked_data is not None:
                (tmp_path / "revoked").write_bytes(revoked_data)
                argv += ["-r", str(tmp_path / "revoked")]
            run = subprocess.run(argv, input=statement, capture_output=True, cwd=tmp_path, env=ssh_env(),
                                 timeout=SSH_TIMEOUT)
    except (OSError, subprocess.TimeoutExpired):
        raise LockError("statement", "ssh-keygen could not run to completion, or its private folder cannot be "
                                     "written") from None
    good = GOOD_KEY_CHANGE.fullmatch(run.stdout.strip())
    if run.returncode != 0 or not good:
        raise LockError("statement", "the statement's signature does not verify under norma-key-change against the "
                                     "key the kind names")
    fingerprint = good.group(2).decode("ascii")
    if fingerprint != key_fingerprint(key):
        raise LockError("statement", "the signing key is not the key the kind names")
    return fingerprint


def _kc_statement_checks(st: dict, kind: str, s: dict, h: dict, snap: dict, today: date) -> list[str]:
    if st["kind"] != kind:
        raise LockError("statement", f"it is a statement of kind {st['kind']}, not {kind}")
    if s["url"] is None or s["serial"] is None:
        raise LockError("statement", "the snapshot is not enrolled (no lock.repository_url and key_change_serial); "
                                     "a project that is not enrolled accepts no key-change statement")
    if st["serial"] <= s["serial"]:
        raise LockError("statement", f"its serial {st['serial']} is not greater than the snapshot's "
                                     f"lock.key_change_serial {s['serial']}; an old statement is never replayed")
    if h["serial"] != st["serial"]:
        raise LockError("statement", "the head's lock.key_change_serial is not exactly the statement's serial")
    if not st["issued"] <= today <= st["valid-until"] \
            or (st["valid-until"] - st["issued"]).days > KC_STATEMENT_DAYS:
        raise LockError("statement", "it is not within its validity dates (issued, valid-until at most "
                                     f"{KC_STATEMENT_DAYS} days later)")
    if st["project-id"] != s["project_id"]:
        raise LockError("statement", "its project-id is not the snapshot's usage.project_id")
    if st["repository-url"] != s["url"] or st["repository-url"] != snap["url"]:
        raise LockError("statement", "its repository-url is not the snapshot's lock.repository_url and the "
                                     "repository git contacts")
    if st["current"] != s["config"]:
        raise LockError("statement", "its current configuration is not the snapshot's signer configuration")
    if st["target"] != h["config"]:
        raise LockError("statement", "its target configuration is not the head's signer configuration")
    if st["release"] != f"v{s['installed']}":  # a statement kind leaves core.lock.json as it is
        raise LockError("statement", "its release is not the snapshot's installed release")
    return [f"checked: the statement is of kind {kind}, for project {st['project-id']} at {st['repository-url']}",
            f"checked: serial {st['serial']} is greater than the snapshot's {s['serial']} and is the head's "
            "lock.key_change_serial",
            f"checked: the statement is valid today (issued {st['issued']}, valid until {st['valid-until']})",
            "checked: the statement's current configuration is the snapshot's, its target the head's",
            f"checked: the statement's release {st['release']} is the snapshot's installed release"]


def _kc_days(value: str, today: date) -> int:
    return (datetime.strptime(value, "%Y%m%d").date() - today).days


def _kc_rotating(s: dict, today: date, kind: str) -> tuple[str, str]:
    """(incoming, outgoing) of a rotating snapshot before its date."""
    if len(s["config"]) != 2:
        raise LockError(kind, "the snapshot is not rotating (one open-ended and one expiring line)")
    if _kc_days(s["config"][1][1], today) <= 0:
        raise LockError(kind, "the outgoing key's date has passed; the expired key authorizes nothing")
    return s["config"][0][0], s["config"][1][0]


def _kc_same_other_lines(s: dict, h: dict) -> str:
    if _kc_other_lines(s) != _kc_other_lines(h):
        raise LockError("signers", "the head's signers file changes lines other than the release signer lines")
    return "checked: the signers file's other lines are unchanged"


def key_change_check(root: Path, expected_url: str, branch: str, head: str, kind: str, *,
                     statement: Path | None = None, statement_sig: Path | None = None,
                     former_commit: str | None = None, revoke: list[str] | None = None, new_key: str | None = None,
                     ci_revoked: str | None = None, record_project_id: str | None = None,
                     record_url: str | None = None, today: date | None = None) -> dict:
    """`lock.py key-change-check`: check one key change of kind `kind` from the snapshot to the head commit."""
    today = today or date.today()
    if kind == "planned-rotation":
        raise LockError("kind", "a planned rotation waits until the rotation-statement format of #58 is approved; "
                                "check it by the runbook")
    if kind not in KC_INPUTS:
        raise LockError("kind", f"--kind is one of {', '.join(KEY_CHANGE_KINDS)}")
    given = {name for name, value in (("statement", statement), ("statement_sig", statement_sig),
                                      ("former_commit", former_commit), ("revoke", revoke), ("new_key", new_key),
                                      ("ci_revoked", ci_revoked), ("record_project_id", record_project_id),
                                      ("record_url", record_url)) if value}
    required, optional = KC_INPUTS[kind]
    flag = lambda name: "--" + name.replace("_", "-")  # noqa: E731
    if required - given or given - required - optional:
        raise LockError("inputs", f"the kind {kind} takes {', '.join(sorted(map(flag, required))) or 'no input'}"
                                  + (f" and optionally {', '.join(sorted(map(flag, optional)))}" if optional else ""))
    st_data = sig_data = None
    if statement is not None:
        st_data = read_once(no_link_on_path(statement, "statement"), KC_MAX_STATEMENT_BYTES, "statement")
        sig_data = read_once(no_link_on_path(statement_sig, "statement signature"), MAX_NOTICE_SIG_BYTES,
                             "statement signature")
    named = sorted(set(revoke or ()))
    if any(not KC_FINGERPRINT.fullmatch(f) for f in named) or (new_key and not KC_FINGERPRINT.fullmatch(new_key)):
        raise LockError("inputs", "--revoke and --new-key are SHA256 key fingerprints")
    if ci_revoked is not None and not (len(ci_revoked) <= 200 and REL_PATH.fullmatch(ci_revoked)
                                       and not any(seg in (".", "..") for seg in ci_revoked.split("/"))):
        raise LockError("inputs", "--ci-revoked is not a repository-relative path")
    if record_project_id is not None and not KC_PROJECT_ID.fullmatch(record_project_id):
        raise LockError("inputs", "--record-project-id is not GL- and four digits")
    if record_url is not None and canonical_repository_url(record_url) != record_url:
        raise LockError("inputs", "--record-url is not a canonical https or ssh repository URL (no credentials, "
                                  "no trailing / or .git, a lower-case host, no default port)")
    try:
        own = Path(__file__).read_bytes()
    except OSError:
        raise LockError("checker", "this checker cannot be read") from None
    inputs: list[str] = []
    checked: list[str] = []
    try:
        private = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
    except OSError:
        raise LockError("key-change-check", "the check's private folder cannot be made") from None
    with private as tmp:
        env = {**snapshot_env(Path(tmp)), "GIT_NO_LAZY_FETCH": "1"}  # never a fetch while an object is read
        snap = take_snapshot(root, expected_url, branch, env)
        _kc_no_partial_clone(root, env)
        checked.append("checked: the clone is not a partial clone")
        head_id = _kc_commit(root, env, head, "--head")
        try:
            snapshot_git(root, env, "merge-base", "--is-ancestor", snap["commit"], head_id)
        except LockError:
            raise LockError("head", "the head commit does not contain the snapshot; rebase the pull request on the "
                                    "default branch's tip, or fetch, and check again") from None
        checked.append("checked: the head commit contains the snapshot")
        s = _kc_side(root, env, snap["commit"], today, "snapshot")
        h = _kc_side(root, env, head_id, today, "head")
        s_new, h_new = version_tuple(s["installed"]) >= NOTICE_FROM, version_tuple(h["installed"]) >= NOTICE_FROM
        if kind == "setup-0.5.0" and not s_new:
            # never a tool from the pull request: the reviewer runs lock.py from the 0.5.0 bundle whose notice
            # passed the check, and that copy must equal what the head installs
            if own != snapshot_read(root, env, head_id, SELF_REL):
                raise LockError("checker", "the snapshot predates this command: run lock.py taken from the 0.5.0 "
                                           "bundle whose notice passed the check, with python -I; it must equal the "
                                           "copy the head installs, and this one does not")
            inputs.append(f"note: the snapshot is on {s['installed']}, before this command; this lock.py must come "
                          "from the checked 0.5.0 bundle, and it equals the copy the head installs")
            checked.append("checked: this lock.py equals the copy the head installs")
        else:
            if own != snapshot_read(root, env, snap["commit"], SELF_REL):
                raise LockError("checker", "this lock.py is not the snapshot's own copy; take it from the snapshot "
                                           "commit and run it with python -I")
            checked.append("checked: this lock.py is the snapshot's own copy")

        # the rules for every kind
        if h_new:
            if h["notice"] is None or h["notice"] != notice_signer_lines(h["text"], today):
                raise LockError("notice-signers", "the head's notice-signers file is missing or differs from the one "
                                                  "derived from its signers file; run lock.py notice-signers --write")
            checked.append("checked: the head's notice-signers file is the one derived from its signers file")
        if not s["revoked_set"] <= h["revoked_set"]:
            raise LockError("revoked", "the head's revoked-keys file drops a key the snapshot revokes; a revocation "
                                       "is irreversible")
        if s["paths"]["revoked_file"] is not None and h["paths"]["revoked_file"] is None:
            raise LockError("revoked", "the head removes lock.revoked_file; a revocation is irreversible")
        checked.append("checked: every key the snapshot revokes stays revoked, and lock.revoked_file stays")
        s_fields, h_fields = _kc_fields(s), _kc_fields(h)
        changed = sorted(name for name in s_fields if s_fields[name] != h_fields[name])
        refused = [name for name in changed if name not in KC_CHANGES[kind]]
        if refused:
            raise LockError(kind, f"this kind does not change {', '.join(refused)}")
        checked.append("checked: nothing this kind does not change has changed"
                       + (f" (changed: {', '.join(changed)})" if changed else ""))

        st = None
        if kind in KC_STATEMENT_KINDS:
            st = parse_key_change_statement(st_data)
            if kind == "restore":
                if len(s["config"]) != 1:
                    raise LockError(kind, "the snapshot is not stable (one open-ended line)")
                signer_entry = s["entries"][0]
            else:
                _kc_rotating(s, today, kind)
                signer_entry = s["entries"][1]
            fingerprint = verify_key_change_statement(st_data, sig_data, signer_entry, s["revoked"], root)
            inputs.append(f"input: statement serial {st['serial']}, kind {st['kind']}, issued {st['issued']}, "
                          f"valid until {st['valid-until']}, signed by {fingerprint}")
            checked.append(f"checked: the statement verifies under {KEY_CHANGE_NAMESPACE} with the key the kind "
                           f"names, {fingerprint}")
            checked += _kc_statement_checks(st, kind, s, h, snap, today)

        if kind == "complete":
            if len(s["config"]) != 2:
                raise LockError(kind, "the snapshot is not rotating (one open-ended and one expiring line)")
            if h["config"] != s["config"][:1]:
                raise LockError(kind, "the head must keep exactly the snapshot's open-ended line and drop the "
                                      "expiring line")
            checked.append(_kc_same_other_lines(s, h))
            passed = _kc_days(s["config"][1][1], today) <= 0
            if s["signer"] != s["config"][0][0] and not passed:
                raise LockError(kind, "the snapshot's installed release is not signed by the incoming key and the "
                                      "outgoing key's date has not passed")
            checked.append("checked: only the expiring line goes, "
                           + ("its date has passed" if passed else "the installed release is signed by the incoming "
                                                                   "key"))
        elif kind == "abandon":
            incoming, outgoing = _kc_rotating(s, today, kind)
            if (st["outgoing"], st["incoming"]) != (outgoing, incoming):
                raise LockError("statement", "its outgoing and incoming keys are not the snapshot's rotation")
            if s["signer"] != outgoing:
                raise LockError(kind, "the installed release is not signed by the outgoing key")
            if outgoing in h["revoked_set"]:
                raise LockError(kind, "the outgoing key is revoked in the head")
            commit, before = _kc_pre_rotation(root, env, snap["commit"], incoming, outgoing, today)
            if h["signers"] != before:
                raise LockError(kind, f"the head's signers file is not the exact pre-rotation file of commit {commit}")
            checked += ["checked: the snapshot rotates and the outgoing key's date has not passed",
                        "checked: the installed release is signed by the outgoing key, which is not revoked",
                        f"checked: the head's signers file is the exact pre-rotation file of commit {commit}"]
        elif kind == "extend":
            incoming, outgoing = _kc_rotating(s, today, kind)
            if (st["outgoing"], st["incoming"]) != (outgoing, incoming):
                raise LockError("statement", "its outgoing and incoming keys are not the snapshot's rotation")
            if len(h["config"]) != 2 or h["config"][0] != s["config"][0] or h["config"][1][0] != outgoing:
                raise LockError(kind, "only the expiring line's valid-before may change")
            old, new = s["config"][1][1], h["config"][1][1]
            if not new > old or not KC_EXTEND_DAYS[0] <= _kc_days(new, today) <= KC_EXTEND_DAYS[1]:
                raise LockError(kind, f"the new date must be later than {old}, and {KC_EXTEND_DAYS[0]} days to one "
                                      "year ahead")
            if outgoing in h["revoked_set"]:
                raise LockError(kind, "the outgoing key is revoked in the head")
            checked += [_kc_same_other_lines(s, h),
                        f"checked: only the expiring line's date moves, from {old} to {new}; the outgoing key is not "
                        "revoked"]
        elif kind == "restore":
            current = s["config"][0][0]
            if len(h["config"]) != 2 or h["config"][0] != s["config"][0]:
                raise LockError(kind, "the head must keep the snapshot's open-ended line and add the former key as "
                                      "the expiring line")
            former, ends = h["config"][1]
            if (st["outgoing"], st["incoming"]) != (current, former):
                raise LockError("statement", "its outgoing key is not the current key, or its incoming key not the "
                                             "former key")
            if not KC_RESTORE_DAYS[0] <= _kc_days(ends, today) <= KC_RESTORE_DAYS[1]:
                raise LockError(kind, f"the former key returns {KC_RESTORE_DAYS[0]} to {KC_RESTORE_DAYS[1]} days "
                                      "ahead")
            if former in s["revoked_set"] or former in h["revoked_set"]:
                raise LockError(kind, "the former key is revoked; a revoked key is never restored")
            former_id = _kc_commit(root, env, former_commit, "--former-commit")
            walked = _kc_never_revoked(root, env, snap["commit"], former_id, former, today)
            inputs.append(f"input: former commit {former_id}, former key {former}")
            checked += [_kc_same_other_lines(s, h),
                        f"checked: the former key returns as the expiring line until {ends}",
                        f"checked: commit {former_id} is a first-parent commit of the snapshot and trusted the former "
                        "key",
                        f"checked: the former key was in no revoked-keys file in the {walked} first-parent commits "
                        "up to the snapshot"]
        elif kind == "revoke":
            inputs += [f"input: revoke {f}" for f in named] + [f"input: new key {new_key}"]
            trusted = {f for f, _ in s["config"]}
            for f in named:
                if f not in trusted and f not in s["revoked_set"]:
                    raise LockError(kind, f"{f} is neither trusted nor revoked in the snapshot")
            if new_key in named or new_key in h["revoked_set"]:
                raise LockError(kind, "the new key is revoked; a change that leaves no trusted signer is refused")
            if h["config"] != [(new_key, "open")]:
                raise LockError(kind, "the head must be stable with exactly the new key's open-ended line")
            if h["revoked_set"] != s["revoked_set"] | set(named):
                raise LockError(kind, "the head's revoked keys are not exactly the snapshot's plus the named keys")
            if s["paths"]["revoked_file"] is not None and h["paths"]["revoked_file"] != s["paths"]["revoked_file"]:
                raise LockError(kind, "lock.revoked_file may only be added, when the snapshot has none")
            checked += [_kc_same_other_lines(s, h),
                        "checked: every named key was trusted or revoked in the snapshot",
                        "checked: the head is stable with only the new key, which is not revoked",
                        "checked: the head revokes exactly the snapshot's keys plus the named keys"
                        + (", in an added lock.revoked_file" if s["paths"]["revoked_file"] is None else "")]
        elif kind == "path-change":
            moved = [k for k in ("signers_file", "revoked_file", "notice_signers_file")
                     if s["paths"][k] != h["paths"][k]]
            if not moved:
                raise LockError(kind, "no trust path changes")
            if s["notice"] is not None and h["notice"] != s["notice"]:
                raise LockError(kind, "the newly selected notice-signers file does not hold the old file's bytes")
            if h["config"] != s["config"] or h["revoked_set"] != s["revoked_set"]:
                raise LockError(kind, "a path change adds or drops a key")
            checked += [f"checked: lock.{k} moves from {s['paths'][k]} to {h['paths'][k]}" for k in moved]
            checked.append("checked: each newly selected file holds the old file's bytes; no key is added or dropped")
        elif kind == "setup-0.5.0":
            if s_new or not h_new:
                raise LockError(kind, "the snapshot must be below 0.5.0 and the head on 0.5.0 or later")
            # owner's decision of 2026-11-16: when the snapshot's CI passes --revoked, every call passes the same
            # file, and the bump must set lock.revoked_file to exactly that file with its content unchanged
            ci_paths, without = _kc_ci_revoked(root, env, snap["commit"])
            if len(ci_paths) > 1 or ci_paths and without:
                raise LockError(kind, "the snapshot's CI lock checks pass different revoked-keys files, or some pass "
                                      "none; make every CI lock check pass the same file first")
            ci = next(iter(ci_paths)) if ci_paths else None
            if ci_revoked is not None and ci_revoked != ci:
                raise LockError(kind, "--ci-revoked is not the file the snapshot's CI passes to lock.py verify")
            if ci is not None:
                inputs.append(f"input: the revoked-keys file the snapshot's CI passes, {safe(ci)}")
            s_path, h_path = s["paths"]["revoked_file"], h["paths"]["revoked_file"]
            if s_path is not None:
                if h_path != s_path or h["revoked"] != s["revoked"]:
                    raise LockError(kind, "the 0.5.0 bump changes no revoked-keys file or path")
                if ci != s_path and (ci is not None or without):
                    # lint.py reports it from 0.5.0: every CI lock check passes the file lock.revoked_file names
                    raise LockError(kind, "the snapshot's CI lock checks do not all pass lock.revoked_file; make them "
                                          "pass it first, in a pull request of its own")
            elif ci is not None:
                if h_path != ci:
                    raise LockError(kind, "the snapshot's CI passes --revoked, so the 0.5.0 bump must set "
                                          "lock.revoked_file to exactly that file")
                before = snapshot_read(root, env, snap["commit"], ci)
                if before is None or h["revoked"] != before:
                    raise LockError(kind, "the revoked-keys file CI passes is not in the snapshot, or its content "
                                          "changes")
                checked.append(f"checked: lock.revoked_file is set to {safe(ci)}, the file the snapshot's CI passes, "
                               "with its content unchanged")
            elif h_path is not None:
                raise LockError(kind, "the snapshot's CI passes no --revoked, so the 0.5.0 bump sets no "
                                      "lock.revoked_file")
            checked.append(f"checked: the head moves from {s['installed']} to {h['installed']}; the signers file "
                           "is unchanged")
        elif kind == "revert-0.5.0":
            if not s_new or h_new:
                raise LockError(kind, "the snapshot must be on 0.5.0 or later and the head below 0.5.0")
            if not h["downgrade"] or h["downgrade"]["from"] != s["installed"]:
                raise LockError(kind, "the head's core.lock.json carries no downgrade marker from the snapshot's "
                                      "release")
            if h["notice_key"] and h["paths"]["notice_signers_file"] != s["paths"]["notice_signers_file"]:
                raise LockError(kind, "lock.notice_signers_file may only be removed, never moved")
            if s["url"] is not None or s["serial"] is not None:
                raise LockError(kind, "the snapshot is enrolled (lock.repository_url and key_change_serial); "
                                      "reverting the 0.5.0 setup is refused")
            checked.append(f"checked: the head moves from {s['installed']} to {h['installed']} with a downgrade "
                           "marker; only the notice-signers file and its key may go")
        elif kind == "enroll":
            if not s_new or not h_new:
                raise LockError(kind, "enrollment needs 0.5.0 or later on both sides")
            inputs += [f"input: record project-id {record_project_id}", f"input: record url {record_url}"]
            if s["url"] is not None or s["serial"] is not None:
                raise LockError(kind, "the snapshot is already enrolled")
            if record_url != snap["url"]:
                raise LockError(kind, "the recorded URL is not the repository git contacts")
            if record_project_id != s["project_id"]:
                raise LockError(kind, "the recorded project id is not the snapshot's usage.project_id")
            if h["url"] != record_url or h["serial"] != 0:
                raise LockError(kind, "the head must add lock.repository_url as recorded and lock.key_change_serial 0")
            checked.append("checked: the head adds exactly the recorded repository URL and serial 0")
        elif kind == "identity-change":
            inputs += [f"input: record project-id {record_project_id}", f"input: record url {record_url}"]
            if s["url"] is None or s["serial"] is None:
                raise LockError(kind, "the snapshot is not enrolled; enroll it first")
            if record_url != snap["url"]:
                raise LockError(kind, "the recorded URL is not the repository git contacts")
            if h["url"] != record_url or h["project_id"] != record_project_id:
                raise LockError(kind, "the head's lock.repository_url and usage.project_id are not the recorded "
                                      "target identity")
            if (h["url"], h["project_id"]) == (s["url"], s["project_id"]):
                raise LockError(kind, "the identity does not change")
            checked.append(f"checked: the identity moves from {s['project_id']} at {s['url']} to the recorded "
                           f"{record_project_id} at {record_url}; the serial keeps {s['serial']}")
    return {"kind": kind, "snapshot": snap, "head": head_id, "inputs": inputs, "checked": checked}


def key_change_lines(result: dict) -> list[str]:
    """The Key-change evidence record: the result, the snapshot, the head, the inputs and each rule that passed."""
    snap = result["snapshot"]
    return [f"key-change-check: ok ({result['kind']})",
            f"snapshot: {snap['commit']} {snap['date']} refs/heads/{safe(snap['branch'])} {snap['url']}",
            f"head: {result['head']}", *result["inputs"], *result["checked"]]


# ---------------------------------------------------------------- command line

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)
    v = sub.add_parser("verify")
    v.add_argument("--root", default=str(ROOT))
    v.add_argument("--base", default=None)
    v.add_argument("--from-git", dest="from_git", default=None)
    v.add_argument("--revoked", default=None)
    w = sub.add_parser("write")
    w.add_argument("--root", default=str(ROOT))
    w.add_argument("--bundle", required=True)
    w.add_argument("--tag", required=True)
    w.add_argument("--packs", default="")
    w.add_argument("--revoked", default=None)
    n = sub.add_parser("notice")
    n.add_argument("--root", required=True)  # the clone: the checker runs as a copy taken out of the snapshot
    n.add_argument("--version", required=True)
    n.add_argument("--release-folder", dest="release_folder", required=True)
    n.add_argument("--expected-url", dest="expected_url", required=True)
    n.add_argument("--branch", required=True)
    n.add_argument("--status-record", dest="status_record", required=True)
    n.add_argument("--downgrade", default=None)
    s = sub.add_parser("notice-signers")
    s.add_argument("--root", default=str(ROOT))
    s.add_argument("--write", action="store_true")
    k = sub.add_parser("key-change-check")
    k.add_argument("--root", required=True)  # the clone: the checker runs as a copy taken out of the snapshot
    k.add_argument("--expected-url", dest="expected_url", required=True)
    k.add_argument("--branch", required=True)
    k.add_argument("--head", required=True)
    k.add_argument("--kind", required=True)
    k.add_argument("--statement", default=None)
    k.add_argument("--statement-sig", dest="statement_sig", default=None)
    k.add_argument("--former-commit", dest="former_commit", default=None)
    k.add_argument("--revoke", action="append", default=None)
    k.add_argument("--new-key", dest="new_key", default=None)
    k.add_argument("--ci-revoked", dest="ci_revoked", default=None)
    k.add_argument("--record-project-id", dest="record_project_id", default=None)
    k.add_argument("--record-url", dest="record_url", default=None)
    args = parser.parse_args(argv)
    revoked = Path(args.revoked) if getattr(args, "revoked", None) else None
    try:
        if args.command == "key-change-check":
            try:
                result = key_change_check(
                    Path(args.root), args.expected_url, args.branch, args.head, args.kind,
                    statement=Path(args.statement) if args.statement else None,
                    statement_sig=Path(args.statement_sig) if args.statement_sig else None,
                    former_commit=args.former_commit, revoke=args.revoke, new_key=args.new_key,
                    ci_revoked=args.ci_revoked, record_project_id=args.record_project_id, record_url=args.record_url)
            except OSError:
                raise LockError("key-change-check", "a file or folder cannot be read or written") from None
            for line in key_change_lines(result):
                print(line)
            return 0
        if args.command == "notice":
            try:
                result = check_notice(Path(args.root), args.version, Path(args.release_folder), args.expected_url,
                                      args.branch, Path(args.status_record), args.downgrade)
            except OSError:
                raise LockError("notice", "a file or folder cannot be read or written") from None
            snap = result["snapshot"]
            print(f"notice: ok (norma {result['version']}, signed by {result['fingerprint']})")
            print(f"snapshot: {snap['commit']} {snap['date']} refs/heads/{safe(snap['branch'])} {snap['url']}")
            print(f"status record: issued {result['issued']}")
            if result["bump"]:
                print(f"note: the snapshot is on {result['installed']}, before the notice command: this is the second "
                      "check of the 0.5.0 bump, after the fixed checker")
            print(f"units: {', '.join(result['units'])}")
            for name, size, digest in result["files"]:
                print(f"file: {name} {size} {digest}")
            if result["downgrade"]:
                print("downgrade: " + json.dumps(result["downgrade"]))
            return 0
        if args.command == "notice-signers":
            print(f"notice-signers: {notice_signers(Path(args.root), args.write)}")
            return 0
        if args.command == "verify":
            result = verify(Path(args.root), args.base, args.from_git, revoked)
        else:
            result = write(Path(args.root), Path(args.bundle), args.tag, [p for p in args.packs.split(",") if p], revoked)
    except LockError as exc:
        print(f"lock: {exc}", file=sys.stderr)
        return 2
    print(f"lock: ok (norma {result['norma']}, packs: {', '.join(result['packs']) or 'none'}, {result['files']} files)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
