#!/usr/bin/env python3
"""Lock tooling: write and verify a consumer's copy of a signed Norma release.

Usage:
  python .claude/scripts/lock.py verify [--root DIR] [--base REV] [--from-git REV] [--revoked FILE]
  python .claude/scripts/lock.py write --bundle FILE --tag TAG --packs a,b [--root DIR] [--revoked FILE]
Exit 0 = ok; exit 2 = a check failed or an input cannot be used (each failure is printed, naming steps and paths,
never file contents).

What it proves. The release is a signed tag (namespace `git`, principal `norma-release`). The tag message carries
the SHA-256 of `.claude/core.manifest`, which lists the SHA-256 of every core and pack file. `verify` works offline:
it checks the tag signature against one signer line of the project's signers file, the tag payload, the manifest
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


def select_signer(text: str) -> tuple[str, str]:
    """(key type, key) of the one line that can verify principal norma-release. Exactly one line may match the
    principal; it must name only that principal and carry exactly `namespaces="git"`; no other line may match."""
    matches = []
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
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
    if len(matches) != 1:
        raise LockError("signers", "exactly one signer line must match the release principal")
    m, names = matches[0]
    if names != [PRINCIPAL]:
        raise LockError("signers", "the release signer line must name only the release principal, without wildcards")
    if m.group("options") != f'namespaces="{NAMESPACE}"':
        raise LockError("signers", f'the release signer line must carry exactly namespaces="{NAMESPACE}"')
    return m.group("type"), m.group("key")


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


def ssh_keygen() -> str:
    no_cwd_lookup()
    found = shutil.which("ssh-keygen")
    if not found or not os.path.isabs(found):
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
                     revoked_data: bytes | None = None) -> tuple[str, str, str, str, str]:
    """Check the signature with a one-line signers file built from the selected key. Returns the payload fields
    plus the key fingerprint ssh-keygen reported."""
    payload, signature = split_tag_object(raw_tag)
    fields = tag_fields(payload)
    key_type, key = select_signer(signers_text)
    fingerprint = key_fingerprint(key)
    binary = ssh_keygen()
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp).resolve()
        (tmp_path / "signers").write_text(f'{PRINCIPAL} namespaces="{NAMESPACE}" {key_type} {key}\n', encoding="ascii")
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
    if good.group(2).decode("ascii") != fingerprint:
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
    no_cwd_lookup()
    try:
        # no line-ending conversion: git must hand over the bytes that were committed
        run = subprocess.run(["git", "-C", str(root), "-c", "core.autocrlf=false", "-c", "core.eol=lf", *args],
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


def _local_bytecode(path: str, regular: bool, tracked: set[str] | None) -> bool:
    """Where Python writes bytecode locally: a .pyc under __pycache__ that git does not track (compared without case)."""
    return (tracked is not None and "/__pycache__/" in path and path.endswith(".pyc") and regular
            and path.casefold() not in tracked)


def _checked_bytecode(root: Path, path: str) -> bool:
    """A .pyc whose header (PEP 552) makes Python check it is not stale before using it: timestamp-based (flags 0)
    or checked-hash (flags 3). An unchecked-hash .pyc (flags 1) is used without that check, so after a bump it would
    keep running the old code; it, and anything without a bytecode header, is refused. The header is no proof of where
    the code came from: whoever can write the file can copy the source's timestamp or hash into it, and that is code
    already running as the user, which the lock does not cover (docs/architecture.md)."""
    try:
        with open(Path(root) / path, "rb") as fh:
            header = fh.read(16)
    except OSError:
        return False
    return len(header) == 16 and header[2:4] == b"\r\n" and int.from_bytes(header[4:8], "little") in (0, 3)


def _tolerated_bytecode(root: Path, path: str, regular: bool, tracked: set[str] | None) -> bool:
    """Bytecode Python wrote locally and checks against its source before using it."""
    return _local_bytecode(path, regular, tracked) and _checked_bytecode(root, path)


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
    obj, tag, message_tag, manifest_hash, fingerprint = verify_signature(raw_tag, signers_text, revoked, revoked_data)
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
            # Bytecode that Python wrote locally is tolerated only when git does not track it: a tracked or committed
            # .pyc would be loaded without being compared to its source, so it counts as an extra file. An untracked
            # one must also be bytecode that Python checks against its source (timestamp-based or checked-hash).
            folder_problems, tracked = core_folder_checks(source, root, folder)
            problems += folder_problems
            if folder_problems and not source.rev:
                continue  # never list through a link
            for path, regular in source.listing(folder):
                if not source.rev and _local_bytecode(path, regular, tracked):
                    if not _checked_bytecode(root, path):
                        problems.append(f"folders: {safe(path)} is bytecode that Python would use without checking it "
                                        "is current (unchecked-hash or not valid); delete it")
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


def previous_release_files(source: "Source", signers_text: str, revoked: Path | None) -> set[str]:
    """The files of the release installed now, from its manifest, but only when that manifest matches the tag in the
    current lock and the tag's signature still verifies; otherwise none (nothing is removed, and `verify` reports any
    file left behind)."""
    old_manifest, old_lock = source.read(MANIFEST_REL), source.read(LOCK_REL)
    if old_manifest is None or old_lock is None:
        return set()
    try:
        raw_tag = base64.b64decode(parse_lock(old_lock)["tag_object"], validate=True)
        manifest_hash = verify_signature(raw_tag, signers_text, revoked)[3]
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
    obj, tag_name, message_tag, manifest_hash, fingerprint = verify_signature(raw_tag, signers_raw.decode("utf-8-sig"), revoked)
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
    gone = previous_release_files(source, signers_raw.decode("utf-8-sig"), revoked) - set(expected)
    # files in the core-owned folders that neither this release nor the previous one has (older copies, for example):
    # verify would refuse them after the write, so they are named before anything is written
    aliases = alias_problems(source)
    if aliases:
        raise LockError("write", f"{aliases[0].split(': ', 1)[1]}; nothing was written")
    known = set(expected) | gone | {MANIFEST_REL, LOCK_REL}
    leftover = []
    for folder in (f for unit in installed for f in manifest["units"][unit]["dirs"]):
        folder_problems, tracked = core_folder_checks(source, root, folder)  # the same rules as verify
        if folder_problems:
            raise LockError("write", f"{folder_problems[0].split(': ', 1)[1]}; nothing was written")
        leftover += [path for path, regular in source.listing(folder)
                     if path not in known and not _tolerated_bytecode(root, path, regular, tracked)]
    if leftover:
        leftover.sort()
        shown = ", ".join(safe(p) for p in leftover[:5]) + (", ..." if len(leftover) > 5 else "")
        raise LockError("write", f"the core-owned folders hold files that are not part of the release ({shown}); "
                                 "remove them (older copies) and run write again; nothing was written. After a "
                                 "release-key rotation or revocation the previous release cannot be checked, so its "
                                 "own files are listed too: compare them with the old lock's file list")
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
    args = parser.parse_args(argv)
    revoked = Path(args.revoked) if args.revoked else None
    try:
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
