#!/usr/bin/env python3
"""Release image pipeline: build, inspect, push, sign, verify, assets (issue #119, ADR-0017).

Stdlib only. Every tool (syft, cosign, gitleaks, grype) is a digest-pinned image run with an argv list and no
shell. Pins come from .github/release/*.Dockerfile (never built) and .github/image-scan/Dockerfile.

Subcommands (release.yml calls exactly these):
  crosscheck       context checks before any build step (G4-119-01); writes outputs tag, version
  build            dotnet publish -t:PublishContainer to <RUNNER_TEMP>/release/out/<name>.tar, no push
  inspect          canary, config and file checks, per-layer gitleaks, grype, syft SBOMs; writes the
                   archive config digest and SHA-256 outputs (G4-119-02, G4-119-04)
  push             verify the downloaded archives against those outputs, docker load, push, check that the
                   pushed config digest equals the archive's
  sign             cosign sign and attest, by digest
  verify           positive, attestation and negative verification (Done-when 2)
  assets           images.txt, VERIFY.txt, SHA256SUMS and gh release upload
  verify-command   print the exact consumer command (the runbook and #120 use this output)
  pins             parse and print the pins

Everything derived from tools, images or archives is printed between ::stop-commands:: markers with control
characters escaped (T-14, H-10). Outputs are written to $GITHUB_OUTPUT only after the markers are closed.
Exit codes: 0 pass, 1 failure (fail closed), 2 usage or configuration error.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import secrets
import shlex
import shutil
import string
import subprocess
import sys
import tarfile
import unicodedata
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RELEASE_DIR = ROOT / ".github" / "release"
BASE_PINS = RELEASE_DIR / "base-images.Dockerfile"
TOOL_PINS = RELEASE_DIR / "tools.Dockerfile"
GITLEAKS_CONFIG = RELEASE_DIR / "gitleaks-image.toml"
SCANNER_PINS = ROOT / ".github" / "image-scan" / "Dockerfile"
EXCEPTIONS_FILE = ROOT / ".github" / "image-scan" / "exceptions.json"
RELEASE_MANIFEST = ROOT / ".release-please-manifest.json"
SPA_DIR = ROOT / "src" / "Decisya.Web"

REPO = "mpcs2013/decisya"
REGISTRY = "ghcr.io"
OWNER = REPO.split("/")[0]
WORKFLOW_REF = "refs/heads/main"
# The one verification identity. Never a regular expression (G2 D4; test_release_workflow.py compares it).
IDENTITY = f"https://github.com/{REPO}/.github/workflows/release.yml@{WORKFLOW_REF}"
IDENTITY_CI = f"https://github.com/{REPO}/.github/workflows/ci.yml@{WORKFLOW_REF}"
ISSUER = "https://token.actions.githubusercontent.com"

# name -> (project, base alias in base-images.Dockerfile). The SPA is served by the BFF, so it has no image.
IMAGES = {
    "api": ("src/Decisya.Api/Decisya.Api.csproj", "aspnet"),
    "bff": ("src/Decisya.Bff/Decisya.Bff.csproj", "aspnet"),
    "migrator": ("src/Decisya.Infrastructure.Migrator/Decisya.Infrastructure.Migrator.csproj", "runtime"),
}
BASE_ALIASES = ("aspnet", "runtime")
TOOL_ALIASES = ("syft", "cosign", "gitleaks")

REF_RE = re.compile(r"^[a-z0-9.-]+(?::[0-9]+)?/[a-z0-9._/-]+:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$")
FROM_RE = re.compile(r"^FROM\s+(\S+)\s+AS\s+(\S+)\s*$")
SHA40_RE = re.compile(r"^[0-9a-f]{40}$")
HEX64_RE = re.compile(r"^[0-9a-f]{64}$")
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+$")
TAG_RE = re.compile(r"^(\d+\.\d+\.\d+|sha-[0-9a-f]{12})$")
OUTPUT_KEY_RE = re.compile(r"^[a-z0-9_]+$")
OUTPUT_VALUE_RE = re.compile(r"^[A-Za-z0-9._:-]*$")

PLATFORM_OS, PLATFORM_ARCH = "linux", "amd64"
USER_ID = "1654"
# The exact environment of the verified chiseled-extra bases (main session, 2026-10-04). Every addition is a
# reviewed diff of this tuple. ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT are never allowed.
ENV_ALLOW = ("PATH", "APP_UID", "ASPNETCORE_HTTP_PORTS", "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_VERSION", "ASPNET_VERSION")

FORBIDDEN_FILE_RES = (
    re.compile(r"(^|/)appsettings\.[^/]+\.json$", re.I),   # appsettings.json itself has no middle part
    re.compile(r"(^|/)\.env($|\.)", re.I),
    re.compile(r"(^|/)secrets\.json$", re.I),
    re.compile(r"(^|/)launchSettings\.json$", re.I),
    re.compile(r"\.(pfx|p12|key)$", re.I),
    re.compile(r"(^|/)id_(rsa|dsa|ecdsa|ed25519)$"),
    re.compile(r"(^|/)\.gitleaksignore$"),
    re.compile(r"(^|/)\.gitleaks\.toml$"),
)
CERT_RE = re.compile(r"\.(pem|crt)$", re.I)
CERT_ALLOWED_PREFIXES = ("etc/ssl/", "usr/share/ca-certificates/")
# OpenSSL's default CA file in the Ubuntu base: a symlink to /etc/ssl/certs/ca-certificates.crt. Exact path only;
# usr/lib/ssl/private stays flagged (main session, 2026-10-04, first local inspect run).
CERT_ALLOWED_FILES = ("usr/lib/ssl/cert.pem",)

# Negative verification classifier (G2 D4): both patterns are matched against cosign's own output. Anything
# else, a network error for example, means "proof not made" and fails the job.
NETWORK_RE = re.compile(
    r"dial tcp|no such host|i/o timeout|timed out|timeout|connection refused|connection reset|TLS handshake|"
    r"temporary failure|UNAUTHORIZED|DENIED|TOOMANYREQUESTS|\b50[0-4]\b|EOF", re.I)
MISMATCH_RE = re.compile(
    r"none of the expected identities matched|expected .{0,80}(not found in|does not match|mismatch).{0,40}certificate|"
    r"certificate identity|expected workflow (trigger|sha|ref)|expected GitHub workflow", re.I)
MISSING_RE = re.compile(r"no signatures found|no signatures associated|signature not found|no attestations? found", re.I)

LOG: list[str] = []


class ReleaseError(Exception):
    """A failed check. The message goes into the protected log, never into an unprotected line."""


# --------------------------------------------------------------------------- text hygiene

def neutralize(text: object) -> str:
    out = []
    for ch in str(text):
        if unicodedata.category(ch).startswith("C"):
            out.append("\\x%02x" % ord(ch) if ord(ch) < 256 else "\\u%04x" % ord(ch))
        else:
            out.append(ch)
    return "".join(out)


def emit_protected(lines: list[str], out=None) -> None:
    out = out or sys.stdout
    token = secrets.token_hex(16)
    print(f"::stop-commands::{token}", file=out)
    for line in lines:
        print(neutralize(line), file=out)
    print(f"::{token}::", file=out)
    out.flush()


def log(line: str) -> None:
    LOG.append(line)


def tail(text: str, n: int = 40) -> list[str]:
    return [f"    | {line}" for line in (text or "").splitlines()[-n:]]


# --------------------------------------------------------------------------- environment

def env_required(name: str, pattern: str | re.Pattern) -> str:
    value = os.environ.get(name, "")
    if not re.match(pattern, value):
        raise ReleaseError(f"environment variable {name} is missing or malformed")
    return value


def runner_temp() -> Path:
    value = os.environ.get("RUNNER_TEMP", "")
    if not value:
        raise ReleaseError("RUNNER_TEMP is not set; this script runs in CI")
    return Path(value)


def out_dir() -> Path:
    return runner_temp() / "release" / "out"


def in_dir() -> Path:
    return runner_temp() / "release-in"


def check_repository() -> None:
    actual = os.environ.get("GITHUB_REPOSITORY")
    if actual is not None and actual != REPO:
        raise ReleaseError(f"GITHUB_REPOSITORY is not {REPO}: the verify identity is bound to {REPO}; edit REPO in a reviewed diff")


def context() -> dict:
    """The build context. On push every release-please value is cross-checked against GITHUB_SHA and the
    checked-out manifest, and the build is always of RELEASE_SHA, never of an action output (G4-119-01)."""
    check_repository()
    event = env_required("RELEASE_EVENT", r"^(push|workflow_dispatch|pull_request)$")
    sha = env_required("RELEASE_SHA", SHA40_RE)
    if event == "push":
        if os.environ.get("RP_RELEASE_CREATED") != "true":
            raise ReleaseError("release-please did not create a release")
        rp_sha = env_required("RP_SHA", SHA40_RE)
        if rp_sha != sha:
            raise ReleaseError("release-please sha differs from the pushed commit (a newer push replaced this run, or the action is wrong); never edit the outputs, see docs/runbooks/release.md")
        version = env_required("RP_VERSION", VERSION_RE)
        tag_name = env_required("RP_TAG", r"^v\d+\.\d+\.\d+$")
        if tag_name != "v" + version:
            raise ReleaseError("release-please tag_name is not 'v' + version")
        try:
            manifest = json.loads(RELEASE_MANIFEST.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            raise ReleaseError(f".release-please-manifest.json is unreadable: {exc}")
        if not isinstance(manifest, dict) or manifest.get(".") != version:
            raise ReleaseError("release-please version differs from the '.' entry of .release-please-manifest.json in the checked-out tree")
        return {"event": event, "sha": sha, "version": version, "tag": version, "release_tag": tag_name}
    if event == "workflow_dispatch":
        short = sha[:12]
        return {"event": event, "sha": sha, "version": f"0.0.0-sha.{short}", "tag": f"sha-{short}", "release_tag": ""}
    return {"event": event, "sha": sha, "version": "0.0.0-pr", "tag": "pr-build", "release_tag": ""}


def publish_context() -> dict:
    """Context of the jobs that push, sign, verify and publish assets. Never a pull_request."""
    check_repository()
    event = env_required("RELEASE_EVENT", r"^(push|workflow_dispatch)$")
    sha = env_required("RELEASE_SHA", SHA40_RE)
    tag = env_required("RELEASE_TAG", TAG_RE)
    if event == "workflow_dispatch" and tag != f"sha-{sha[:12]}":
        raise ReleaseError("a dry-run tag must be sha-<first 12 hex of RELEASE_SHA>")
    if event == "push" and not VERSION_RE.match(tag):
        raise ReleaseError("a release tag must be a SemVer version")
    return {"event": event, "sha": sha, "tag": tag}


# --------------------------------------------------------------------------- pins

def parse_pins(path: Path, aliases: tuple[str, ...]) -> dict[str, str]:
    """alias -> tag@digest reference. Exact aliases, one line each, nothing else but comments; an all-zero
    digest is an unresolved placeholder and fails closed."""
    refs: dict[str, str] = {}
    for n, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        m = FROM_RE.match(line)
        if not m:
            raise ReleaseError(f"{path.name}:{n}: only 'FROM <tag@digest> AS <alias>' lines and comments are allowed")
        ref, alias = m.groups()
        if alias not in aliases:
            raise ReleaseError(f"{path.name}:{n}: unknown alias {alias!r}, expected one of {list(aliases)}")
        if alias in refs:
            raise ReleaseError(f"{path.name}:{n}: duplicate alias {alias}")
        if not REF_RE.match(ref):
            raise ReleaseError(f"{path.name}:{n}: {alias} is not a tag@sha256 reference")
        if re.search(r"@sha256:0{64}$", ref):
            raise ReleaseError(f"{path.name}:{n}: {alias} has an unresolved all-zero digest")
        refs[alias] = ref
    if set(refs) != set(aliases):
        raise ReleaseError(f"{path.name}: aliases are {sorted(refs)}, expected {sorted(aliases)}")
    return refs


def digest_only(ref: str) -> str:
    """repo:tag@sha256:x -> repo@sha256:x (what cosign verifies)."""
    repo_tag, _, digest = ref.partition("@")
    return repo_tag.rsplit(":", 1)[0] + "@" + digest


def image_ref(name: str, digest: str) -> str:
    if not DIGEST_RE.match(digest):
        raise ReleaseError(f"{name}: not a sha256 digest")
    return f"{REGISTRY}/{OWNER}/decisya-{name}@{digest}"


# --------------------------------------------------------------------------- process helpers

def run(argv: list[str], timeout: int = 1800) -> subprocess.CompletedProcess:
    # argv list, no shell, output captured so it only reaches the log inside the stop window.
    return subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def run_checked(argv: list[str], what: str, timeout: int = 1800) -> subprocess.CompletedProcess:
    proc = run(argv, timeout)
    if proc.returncode != 0:
        LOG.extend(tail(proc.stdout, 20) + tail(proc.stderr, 40))
        raise ReleaseError(f"{what} failed (exit {proc.returncode})")
    return proc


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# --------------------------------------------------------------------------- archives

def normalize(name: str) -> str:
    n = name.replace("\\", "/")
    while n.startswith("./") or n.startswith("/"):
        n = n[2:] if n.startswith("./") else n[1:]
    return n


def read_archive(path: Path) -> dict:
    """The docker-save archive that dotnet publish writes: manifest.json, the config json, the layer tars."""
    with tarfile.open(path, "r:*") as outer:
        members = {normalize(m.name): m for m in outer.getmembers()}

        def blob(name: str) -> bytes:
            n = normalize(name)
            if n.startswith("..") or "/../" in n or n not in members:
                raise ReleaseError(f"archive member {name!r} is missing or unsafe")
            member = members[n]
            if not member.isreg():
                raise ReleaseError(f"archive member {name!r} is not a regular file")
            fh = outer.extractfile(member)
            if fh is None:
                raise ReleaseError(f"archive member {name!r} cannot be read")
            return fh.read()

        try:
            manifest = json.loads(blob("manifest.json"))
        except ValueError as exc:
            raise ReleaseError(f"manifest.json is not JSON: {exc}")
        if not isinstance(manifest, list) or len(manifest) != 1 or not isinstance(manifest[0], dict):
            raise ReleaseError("manifest.json must hold exactly one image")
        entry = manifest[0]
        config_bytes = blob(str(entry.get("Config", "")))
        layer_names = entry.get("Layers")
        if not isinstance(layer_names, list) or not layer_names:
            raise ReleaseError("manifest.json lists no layers")
        layers = [(str(n), blob(str(n))) for n in layer_names]
    try:
        config = json.loads(config_bytes)
    except ValueError as exc:
        raise ReleaseError(f"image config is not JSON: {exc}")
    return {
        "repo_tags": entry.get("RepoTags") or [],
        "config": config,
        "config_bytes": config_bytes,
        "config_digest": "sha256:" + hashlib.sha256(config_bytes).hexdigest(),
        "layers": layers,
    }


def unpack_layer(data: bytes, dest: Path) -> tuple[list[str], list[str]]:
    """Unpack ONE layer into dest with tarfile filter="data" (G4-119-02). Returns (member names, skipped links).
    Only a rejected *link* may be skipped (gitleaks does not follow links, and a link's target is scanned where it
    lives). A rejected regular file, device or any other member fails the job. Never the 'tar' or 'fully_trusted'
    filter."""
    dest.mkdir(parents=True, exist_ok=True)
    names: list[str] = []
    skipped: list[str] = []
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:*", errorlevel=2) as tar:
        for member in tar:
            names.append(normalize(member.name))
            try:
                tarfile.data_filter(member, str(dest))
            except tarfile.FilterError as exc:
                if member.issym() or member.islnk():
                    skipped.append(f"{normalize(member.name)} -> {member.linkname} ({type(exc).__name__})")
                    continue
                raise ReleaseError(f"layer member {normalize(member.name)!r} rejected by the data filter ({type(exc).__name__}): not skipped")
            if member.islnk() and not (dest / normalize(member.linkname)).exists():
                skipped.append(f"{normalize(member.name)} => {member.linkname} (hard link to another layer)")
                continue
            tar.extract(member, path=dest, filter="data")
    return names, skipped


# --------------------------------------------------------------------------- image checks

def check_config(name: str, cfg: dict) -> list[str]:
    problems: list[str] = []
    inner = cfg.get("config") if isinstance(cfg.get("config"), dict) else {}
    if cfg.get("os") != PLATFORM_OS or cfg.get("architecture") != PLATFORM_ARCH:
        problems.append(f"{name}: platform is {cfg.get('os')}/{cfg.get('architecture')}, expected {PLATFORM_OS}/{PLATFORM_ARCH}")
    if str(inner.get("User", "")) != USER_ID:
        problems.append(f"{name}: User is {inner.get('User')!r}, expected {USER_ID!r}")
    keys = []
    for item in inner.get("Env") or []:
        keys.append(str(item).split("=", 1)[0])
    extra = sorted(set(keys) - set(ENV_ALLOW))
    if extra:
        problems.append(f"{name}: Env keys outside the allow-list: {extra}")
    for banned in ("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT"):
        if banned in keys:
            problems.append(f"{name}: {banned} must not be set in the image")
    entry = inner.get("Entrypoint")
    if not (isinstance(entry, list) and len(entry) >= 2 and entry[0] == "dotnet" and str(entry[1]).startswith("/app/") and str(entry[1]).endswith(".dll")):
        problems.append(f"{name}: Entrypoint is not 'dotnet /app/<App>.dll'")
    return problems


def check_names(name: str, layer_index: int, names: list[str]) -> list[str]:
    problems: list[str] = []
    for member in names:
        base = member.rsplit("/", 1)[-1]
        if base.startswith(".wh."):
            continue  # a whiteout: the deleted file is checked in the layer that holds it
        if any(rx.search(member) for rx in FORBIDDEN_FILE_RES):
            problems.append(f"{name}: layer {layer_index}: forbidden file {member}")
        elif CERT_RE.search(member) and not member.startswith(CERT_ALLOWED_PREFIXES) and member not in CERT_ALLOWED_FILES:
            problems.append(f"{name}: layer {layer_index}: certificate or key file outside the CA store: {member}")
    return problems


# --------------------------------------------------------------------------- gitleaks

def gitleaks_argv(tool: str, scan_dir: Path, cfg_dir: Path) -> list[str]:
    """G4-119-02: the config and the ignore file are mounted read-only OUTSIDE the scanned tree, a
    'gitleaks:allow' comment is ignored, findings are redacted, and the report goes to stdout."""
    return [
        "docker", "run", "--rm", "--network", "none",
        "-v", f"{scan_dir}:/scan:ro",
        "-v", f"{cfg_dir}:/cfg:ro",
        tool, "dir", "/scan",
        "--config", "/cfg/gitleaks-image.toml",
        "--gitleaks-ignore-path", "/cfg/empty.gitleaksignore",
        "--ignore-gitleaks-allow",
        "--redact", "--no-banner", "--no-color",
        "--exit-code", "1",
        "--report-format", "json", "--report-path", "-",  # "-" is stdout; /dev/stdout wrote nothing (main session, 2026-10-04)
    ]


def scan_dir_with_gitleaks(tool: str, scan_dir: Path, cfg_dir: Path) -> dict:
    """Returns {'status': 'clean'|'leak'|'error', 'findings': [(rule, file, line)]}. Redacted: no secret text."""
    scan_dir.chmod(0o755)
    proc = run(gitleaks_argv(tool, scan_dir, cfg_dir), timeout=1800)
    try:
        report = json.loads(proc.stdout) if proc.stdout.strip() else []
    except ValueError:
        report = None
    if not isinstance(report, list):
        LOG.extend(tail(proc.stderr, 20))
        return {"status": "error", "findings": []}
    findings = [(str(f.get("RuleID", "?")), str(f.get("File", "?")), f.get("StartLine", 0)) for f in report if isinstance(f, dict)]
    if proc.returncode == 0 and not findings:
        return {"status": "clean", "findings": []}
    if proc.returncode == 1 and findings:
        return {"status": "leak", "findings": findings}
    LOG.extend(tail(proc.stderr, 20))
    return {"status": "error", "findings": findings}


def make_cfg_dir(base: Path) -> Path:
    cfg = base / "cfg"
    cfg.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(GITLEAKS_CONFIG, cfg / "gitleaks-image.toml")
    (cfg / "empty.gitleaksignore").write_text("", encoding="utf-8")
    cfg.chmod(0o755)
    return cfg


def synthetic_layer(files: dict[str, bytes]) -> bytes:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as tar:
        for name, content in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(content)
            info.mode = 0o644
            tar.addfile(info, io.BytesIO(content))
    return buf.getvalue()


def canary(tool: str, work: Path, cfg_dir: Path) -> None:
    """The gate must be shown to fail closed in the real run (G4-119-02). Layer 0 holds a token that the default
    rules detect (generated now, never committed, masked); layer 1 deletes it with a whiteout. The scan must
    report layer 0 and pass layer 1, through the same code path as the real scan."""
    alphabet = string.ascii_letters + string.digits
    token = "ghp_" + "".join(secrets.choice(alphabet) for _ in range(36))
    print(f"::add-mask::{token}")
    base = work / "canary"
    if base.exists():
        shutil.rmtree(base)
    layer0 = synthetic_layer({"app/canary-token.txt": f'github_token = "{token}"\n'.encode()})
    layer1 = synthetic_layer({"app/.wh.canary-token.txt": b""})
    unpack_layer(layer0, base / "0")
    unpack_layer(layer1, base / "1")
    first = scan_dir_with_gitleaks(tool, base / "0", cfg_dir)
    second = scan_dir_with_gitleaks(tool, base / "1", cfg_dir)
    if first["status"] != "leak" or not any("canary-token.txt" in f[1] for f in first["findings"]):
        raise ReleaseError("canary: the layer scan did not report the seeded token in layer 0; the secret gate cannot be trusted")
    if second["status"] != "clean":
        raise ReleaseError(f"canary: the whiteout-only layer was not clean ({second['status']}); the secret gate cannot be trusted")
    log("canary: gitleaks reported the seeded token in layer 0 and passed the whiteout layer 1")


# --------------------------------------------------------------------------- subcommands

def cmd_pins(_args) -> dict:
    for path, aliases in ((BASE_PINS, BASE_ALIASES), (TOOL_PINS, TOOL_ALIASES)):
        for alias, ref in parse_pins(path, aliases).items():
            log(f"{path.name}: {alias} = {ref}")
    return {}


def cmd_crosscheck(_args) -> dict:
    ctx = context()
    log(f"context: event={ctx['event']} tag={ctx['tag']} version={ctx['version']} sha={ctx['sha']}")
    return {"tag": ctx["tag"], "version": ctx["version"]}


def check_stable_sdk(version: str) -> str:
    """Release images are built with a released SDK only, never a preview or release candidate: setup-dotnet's
    dotnet-quality: preview installs one next to the runner's SDKs (first dry run, #119)."""
    version = version.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ReleaseError(f"the selected .NET SDK {version!r} is not a stable release; release images need one")
    return version


def cmd_build(_args) -> dict:
    ctx = context()
    sdk = check_stable_sdk(run_checked(["dotnet", "--version"], "dotnet --version").stdout)
    log(f"build: .NET SDK {sdk}")
    bases = parse_pins(BASE_PINS, BASE_ALIASES)
    out = out_dir()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    for name, (project, alias) in IMAGES.items():
        archive = out / f"{name}.tar"
        argv = [
            "dotnet", "publish", project, "-c", "Release", "-r", "linux-x64", "--self-contained", "false",
            "-t:PublishContainer", "--nologo",
            f"-p:ContainerBaseImage={bases[alias]}",
            f"-p:ContainerArchiveOutputPath={archive}",
            f"-p:ContainerRepository=decisya-{name}",
            f"-p:ContainerImageTag={ctx['tag']}",
            f"-p:Version={ctx['version']}",
        ]
        log(f"build {name}: base {bases[alias]}")
        proc = run_checked(argv, f"dotnet publish {name}")
        LOG.extend(tail(proc.stdout, 8))
        if not archive.is_file() or archive.is_symlink():
            raise ReleaseError(f"{name}: no archive was written")
        log(f"build {name}: {archive.name} {archive.stat().st_size} bytes")
    return {}


def _grype(scanner: str, db_dir: Path, archive_dir: Path, name: str, ims, entries, today) -> list[str]:
    argv = ["docker", "run", "--rm", "-v", f"{db_dir}:{ims.DB_MOUNT}", "-v", f"{archive_dir}:/in:ro"]
    for k, v in ims.SCAN_ENV.items():
        argv += ["-e", f"{k}={v}"]
    argv += [scanner, f"docker-archive:/in/{name}.tar", "-o", "json"]
    proc = run(argv, timeout=1800)
    if proc.returncode != 0:
        LOG.extend(tail(proc.stderr, 20))
        return [f"{name}: grype exit {proc.returncode}"]
    try:
        doc = json.loads(proc.stdout)
    except ValueError as exc:
        return [f"{name}: grype output is not JSON ({exc})"]
    problems = []
    source = doc.get("source") if isinstance(doc, dict) and isinstance(doc.get("source"), dict) else {}
    target = source.get("target") if isinstance(source.get("target"), dict) else {}
    if not isinstance(doc, dict) or not isinstance(doc.get("matches"), list):
        return [f"{name}: grype JSON has no 'matches' list"]
    if source.get("type") != "image":
        problems.append(f"{name}: grype source.type is not 'image'")
    if not isinstance(target.get("layers"), list) or not target["layers"]:
        problems.append(f"{name}: grype catalogued no layers")
    if not (doc.get("distro") or {}).get("name"):
        problems.append(f"{name}: grype did not recognise the OS")
    if not ims._db_built(doc):
        problems.append(f"{name}: grype database is not identified")
    if problems:
        return problems
    result = ims.evaluate(doc, name, entries, today)
    log(f"grype {name}: {len(doc['matches'])} matches, {len(result['findings'])} blocking, {len(result['excepted'])} excepted")
    for f in result["findings"]:
        log(f"  FAIL {f['package']} {f['version']} {f['id']} {f['severity']} fix={f['fix']}")
    return [f"{name}: {len(result['findings'])} High or Critical finding(s) without an active exception"] if result["findings"] else []


def _syft(tool: str, mount: Path, source: str, sbom_dir: Path, filename: str, require_components: bool) -> None:
    argv = [
        "docker", "run", "--rm", "--network", "none", "-e", "SYFT_CHECK_FOR_APP_UPDATE=false",
        "-e", "SYFT_JAVASCRIPT_INCLUDE_DEV_DEPENDENCIES=false",
        "-v", f"{mount}:/in:ro", "-v", f"{sbom_dir}:/out",
        tool, "scan", source, "-o", f"cyclonedx-json=/out/{filename}",
    ]
    run_checked(argv, f"syft {filename}")
    path = sbom_dir / filename
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise ReleaseError(f"{filename}: not valid JSON ({exc})")
    if not isinstance(doc, dict) or doc.get("bomFormat") != "CycloneDX":
        raise ReleaseError(f"{filename}: not a CycloneDX document")
    if require_components and not doc.get("components"):
        raise ReleaseError(f"{filename}: no components were catalogued")
    log(f"sbom {filename}: {len(doc.get('components') or [])} components")


def cmd_inspect(_args) -> dict:
    ctx = context()
    tools = parse_pins(TOOL_PINS, TOOL_ALIASES)
    bases = parse_pins(BASE_PINS, BASE_ALIASES)
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import image_scan as ims  # reuse the ADR-0015 grype policy; only this subcommand needs it

    work = runner_temp() / "release"
    out = out_dir()
    cfg_dir = make_cfg_dir(work)
    canary(tools["gitleaks"], work, cfg_dir)  # fatal before any real scan

    scanner = ims.parse_scanner_ref(SCANNER_PINS.read_text(encoding="utf-8"))
    entries = ims.validate_exceptions(json.loads(EXCEPTIONS_FILE.read_text(encoding="utf-8")))
    today = datetime.now(timezone.utc).date()
    db_dir = work / "grype-db"
    db_dir.mkdir(parents=True, exist_ok=True)
    db_dir.chmod(0o777)
    run_checked(ims.db_update_argv(scanner, str(db_dir)), "grype database update")

    sbom_dir = work / "sbom"
    if sbom_dir.exists():
        shutil.rmtree(sbom_dir)
    sbom_dir.mkdir(parents=True)
    sbom_dir.chmod(0o777)

    problems: list[str] = []
    outputs: dict[str, str] = {"tag": ctx["tag"]}
    for name in IMAGES:
        archive = out / f"{name}.tar"
        info = read_archive(archive)
        expected_tag = f"decisya-{name}:{ctx['tag']}"
        if info["repo_tags"] != [expected_tag]:
            problems.append(f"{name}: archive RepoTags are {info['repo_tags']}, expected [{expected_tag!r}]")
        problems += check_config(name, info["config"])
        labels = (info["config"].get("config") or {}).get("Labels") or {}
        log(f"{name}: base.digest label {labels.get('org.opencontainers.image.base.digest')}; pinned {bases[IMAGES[name][1]]}")

        # The image config (Env, Labels, history) is scanned as well as the layers.
        cfg_scan = work / "cfgscan" / name
        if cfg_scan.exists():
            shutil.rmtree(cfg_scan)
        cfg_scan.mkdir(parents=True)
        (cfg_scan / "config.json").write_bytes(info["config_bytes"])
        res = scan_dir_with_gitleaks(tools["gitleaks"], cfg_scan, cfg_dir)
        if res["status"] != "clean":
            problems.append(f"{name}: gitleaks on the image config: {res['status']} {res['findings']}")

        all_names: list[str] = []
        for index, (layer_name, data) in enumerate(info["layers"]):
            dest = work / "layers" / name / f"{index:02d}"
            if dest.exists():
                shutil.rmtree(dest)
            names, skipped = unpack_layer(data, dest)  # a rejected non-link member raises ReleaseError
            all_names += names
            problems += check_names(name, index, names)
            for s in skipped:
                log(f"{name}: layer {index}: skipped link {s}")
            res = scan_dir_with_gitleaks(tools["gitleaks"], dest, cfg_dir)
            log(f"gitleaks {name} layer {index}: {res['status']} ({len(names)} members, {len(skipped)} links skipped)")
            if res["status"] != "clean":
                problems.append(f"{name}: gitleaks on layer {index}: {res['status']} {res['findings']}")
        if name == "bff" and "app/wwwroot/index.html" not in all_names:
            problems.append("bff: app/wwwroot/index.html is missing (the SPA was not built into the image)")

        problems += _grype(scanner, db_dir, out, name, ims, entries, today)
        _syft(tools["syft"], out, f"docker-archive:/in/{name}.tar", sbom_dir, f"decisya-{name}-{ctx['tag']}.cdx.json", True)

        outputs[f"{name}_config"] = info["config_digest"]
        outputs[f"{name}_sha256"] = sha256_file(archive)

    # The SPA ships inside the BFF image as bundled JS without package metadata, so its lockfile is its own SBOM.
    spa_stage = work / "spa"
    if spa_stage.exists():
        shutil.rmtree(spa_stage)
    spa_stage.mkdir(parents=True)
    for fname in ("package.json", "package-lock.json"):
        shutil.copyfile(SPA_DIR / fname, spa_stage / fname)
    _syft(tools["syft"], spa_stage, "dir:/in", sbom_dir, f"decisya-bff-spa-{ctx['tag']}.cdx.json", True)

    for sbom in sorted(sbom_dir.glob("*.cdx.json")):
        shutil.copyfile(sbom, out / sbom.name)
    if problems:
        for p in problems:
            log(f"PROBLEM: {p}")
        raise ReleaseError(f"{len(problems)} pre-push check(s) failed; nothing was pushed")
    log("inspect: all pre-push checks passed")
    return outputs


def _digest_env(name: str, suffix: str, pattern: str | re.Pattern) -> str:
    return env_required(f"{name.upper()}_{suffix}", pattern)


def docker_config() -> Path:
    value = os.environ.get("DOCKER_CONFIG", "")
    if not value or not Path(value).is_dir():
        raise ReleaseError("DOCKER_CONFIG must point at the directory written by docker login")
    return Path(value)


def cmd_push(_args) -> dict:
    ctx = publish_context()
    src = in_dir()
    outputs: dict[str, str] = {}
    for name in IMAGES:
        archive = src / f"{name}.tar"
        if not archive.is_file() or archive.is_symlink():
            raise ReleaseError(f"{name}: downloaded archive is missing")
        if sha256_file(archive) != _digest_env(name, "SHA256", HEX64_RE):
            raise ReleaseError(f"{name}: archive SHA-256 differs from the one the build job scanned")
        info = read_archive(archive)
        if info["config_digest"] != _digest_env(name, "CONFIG", DIGEST_RE):
            raise ReleaseError(f"{name}: archive config digest differs from the one the build job scanned")
        local = f"decisya-{name}:{ctx['tag']}"
        if info["repo_tags"] != [local]:
            raise ReleaseError(f"{name}: archive RepoTags are not [{local!r}]")
        remote = f"{REGISTRY}/{OWNER}/decisya-{name}:{ctx['tag']}"
        run_checked(["docker", "load", "-i", str(archive)], f"docker load {name}")
        run_checked(["docker", "tag", local, remote], f"docker tag {name}")
        pushed = run_checked(["docker", "push", remote], f"docker push {name}")
        m = re.search(r"digest:\s+(sha256:[0-9a-f]{64})\s+size:", pushed.stdout)
        if not m:
            raise ReleaseError(f"{name}: docker push printed no digest")
        digest = m.group(1)
        manifest = run_checked(["docker", "manifest", "inspect", image_ref(name, digest)], f"docker manifest inspect {name}")
        try:
            pushed_config = (json.loads(manifest.stdout).get("config") or {}).get("digest")
        except (ValueError, AttributeError):
            pushed_config = None
        if pushed_config != info["config_digest"]:
            raise ReleaseError(f"{name}: the pushed config digest differs from the scanned archive's (or the manifest has an unexpected shape)")
        log(f"push {name}: {remote} -> {digest}, config digest equals the scanned archive's")
        outputs[f"{name}_digest"] = digest
    return outputs


def cosign_argv(tool: str, mounts: list[str], token: bool) -> list[str]:
    """The cosign container gets no environment except DOCKER_CONFIG and HOME (paths) and, for sign and attest, the
    two OIDC request variables (S-119-02). It runs as the runner's own user: docker login writes config.json readable
    by that user only, and the image's non-root user could not read it (second dry run, #119). The file's mode is
    never widened. HOME is a fresh directory of that user, for cosign's Sigstore trust-root cache."""
    home = runner_temp() / "cosign-home"
    home.mkdir(parents=True, exist_ok=True)
    argv = ["docker", "run", "--rm", "--user", f"{os.getuid()}:{os.getgid()}",
            "-e", "DOCKER_CONFIG=/dockercfg", "-v", f"{docker_config()}:/dockercfg:ro",
            "-e", "HOME=/cosign-home", "-v", f"{home}:/cosign-home"]
    if token:
        argv += ["-e", "ACTIONS_ID_TOKEN_REQUEST_URL", "-e", "ACTIONS_ID_TOKEN_REQUEST_TOKEN"]
    for mount in mounts:
        argv += ["-v", mount]
    return argv + [tool]


def cmd_sign(_args) -> dict:
    ctx = publish_context()
    tool = parse_pins(TOOL_PINS, TOOL_ALIASES)["cosign"]
    src = in_dir()
    for name in IMAGES:
        ref = image_ref(name, _digest_env(name, "DIGEST", DIGEST_RE))
        run_checked(cosign_argv(tool, [], True) + ["sign", "--yes", ref], f"cosign sign {name}")
        log(f"signed {ref}")
        sboms = [f"decisya-{name}-{ctx['tag']}.cdx.json"] + ([f"decisya-bff-spa-{ctx['tag']}.cdx.json"] if name == "bff" else [])
        for sbom in sboms:
            if not (src / sbom).is_file():
                raise ReleaseError(f"{sbom} is missing from the downloaded artifact")
            run_checked(cosign_argv(tool, [f"{src}:/in:ro"], True) + ["attest", "--yes", "--type", "cyclonedx", "--predicate", f"/in/{sbom}", ref], f"cosign attest {sbom}")
            log(f"attested {sbom} to {ref}")
    return {}


def verify_flags(sha: str, trigger: str, identity: str = IDENTITY) -> list[str]:
    """The one verification identity (G4-119-03). Refuses to build a command without the workflow sha."""
    if not SHA40_RE.match(sha or ""):
        raise ReleaseError("verification requires --certificate-github-workflow-sha (the release commit)")
    if trigger not in ("push", "workflow_dispatch"):
        raise ReleaseError("trigger must be push or workflow_dispatch")
    return [
        "--certificate-identity", identity,
        "--certificate-oidc-issuer", ISSUER,
        "--certificate-github-workflow-repository", REPO,
        "--certificate-github-workflow-trigger", trigger,
        "--certificate-github-workflow-sha", sha,
        "--certificate-github-workflow-ref", WORKFLOW_REF,
    ]


def classify_negative(returncode: int, output: str) -> str:
    """'missing' (no signature), 'mismatch' (identity or extension mismatch) or 'other' (proof not made)."""
    if returncode == 0:
        return "verified"
    if NETWORK_RE.search(output):
        return "other"
    if MISMATCH_RE.search(output):
        return "mismatch"
    if MISSING_RE.search(output):
        return "missing"
    return "other"


def _cosign_verify(tool: str, ref: str, flags: list[str], attestation: bool = False) -> subprocess.CompletedProcess:
    sub = ["verify-attestation", "--type", "cyclonedx"] if attestation else ["verify"]
    return run(cosign_argv(tool, [], False) + sub + flags + [ref], timeout=600)


def cmd_verify(_args) -> dict:
    ctx = publish_context()
    tool = parse_pins(TOOL_PINS, TOOL_ALIASES)["cosign"]
    bases = parse_pins(BASE_PINS, BASE_ALIASES)
    trigger = ctx["event"]
    flags = verify_flags(ctx["sha"], trigger)
    problems: list[str] = []
    refs = {name: image_ref(name, _digest_env(name, "DIGEST", DIGEST_RE)) for name in IMAGES}
    for name, ref in refs.items():
        for attestation in (False, True):
            proc = _cosign_verify(tool, ref, flags, attestation)
            kind = "verify-attestation" if attestation else "verify"
            if proc.returncode != 0:
                LOG.extend(tail(proc.stdout, 10) + tail(proc.stderr, 20))
                problems.append(f"{name}: cosign {kind} failed")
            else:
                log(f"{name}: cosign {kind} OK ({ref})")

    def negative(label: str, ref: str, neg_flags: list[str], expect: str) -> None:
        proc = _cosign_verify(tool, ref, neg_flags)
        verdict = classify_negative(proc.returncode, proc.stdout + "\n" + proc.stderr)
        if verdict != expect:
            LOG.extend(tail(proc.stdout, 10) + tail(proc.stderr, 20))
            problems.append(f"negative case {label}: expected a failure classified {expect!r}, got {verdict!r}; proof not made")
        else:
            log(f"negative case {label}: failed as required ({expect})")

    # 1. an image that this identity never signed: the digest-pinned aspnet base
    negative("1 unsigned base image", digest_only(bases["aspnet"]), flags, "missing")
    # 2. our own signed image under another identity
    first_ref = refs["api"]
    negative("2 wrong identity (ci.yml)", first_ref, verify_flags(ctx["sha"], trigger, IDENTITY_CI), "mismatch")
    # 3. a dry-run image must not verify as a release (G4-119-03)
    if trigger == "workflow_dispatch":
        negative("3 dry-run image as a push release", first_ref, verify_flags(ctx["sha"], "push"), "mismatch")
    if problems:
        for p in problems:
            log(f"PROBLEM: {p}")
        raise ReleaseError(f"{len(problems)} verification check(s) failed")
    return {}


def consumer_command(name: str, digest: str, sha: str, runner: str) -> list[str]:
    """What a consumer runs. The trigger is always push: a dry-run image never verifies as a release."""
    argv = ["cosign", "verify"] + verify_flags(sha, "push") + [image_ref(name, digest)]
    if runner == "docker":
        tool = parse_pins(TOOL_PINS, TOOL_ALIASES)["cosign"]
        argv = ["docker", "run", "--rm", tool] + argv[1:]
    return argv


def cmd_verify_command(args) -> dict:
    if args.image not in IMAGES:
        raise ReleaseError(f"--image must be one of {sorted(IMAGES)}")
    print(" ".join(shlex.quote(a) for a in consumer_command(args.image, args.digest, args.sha, args.runner)))
    return {}


def cmd_assets(_args) -> dict:
    ctx = publish_context()
    if ctx["event"] != "push":
        raise ReleaseError("release assets are only uploaded for a release")
    src = in_dir()
    lines, verify_lines = [], []
    for name in IMAGES:
        digest = _digest_env(name, "DIGEST", DIGEST_RE)
        lines.append(image_ref(name, digest))
        verify_lines.append(" ".join(shlex.quote(a) for a in consumer_command(name, digest, ctx["sha"], "cosign")))
    (src / "images.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (src / "VERIFY.txt").write_text(
        "# Pull by digest only, and verify before you run. A valid signature proves the image was built by\n"
        f"# {IDENTITY} at commit {ctx['sha']}; it does not prove the dependencies were benign.\n"
        + "\n".join(verify_lines) + "\n", encoding="utf-8")
    sboms = sorted(p.name for p in src.glob("*.cdx.json"))
    if len(sboms) != 4:
        raise ReleaseError(f"expected 4 SBOMs, found {len(sboms)}")
    assets = sboms + ["images.txt", "VERIFY.txt"]
    sums = "".join(f"{sha256_file(src / a)}  {a}\n" for a in assets)
    (src / "SHA256SUMS").write_text(sums, encoding="utf-8")
    tag_name = "v" + ctx["tag"]
    run_checked(["gh", "release", "upload", tag_name, *[str(src / a) for a in assets + ["SHA256SUMS"]], "--clobber", "--repo", REPO], "gh release upload")
    log(f"uploaded {len(assets) + 1} assets to {tag_name}")
    return {}


COMMANDS = {
    "pins": cmd_pins, "crosscheck": cmd_crosscheck, "build": cmd_build, "inspect": cmd_inspect, "push": cmd_push,
    "sign": cmd_sign, "verify": cmd_verify, "assets": cmd_assets, "verify-command": cmd_verify_command,
}


def write_outputs(outputs: dict[str, str]) -> None:
    path = os.environ.get("GITHUB_OUTPUT")
    if not path or not outputs:
        return
    with open(path, "a", encoding="utf-8") as fh:
        for key, value in outputs.items():
            if not OUTPUT_KEY_RE.match(key) or not OUTPUT_VALUE_RE.match(value):
                raise ReleaseError(f"refusing to write output {key!r}: unexpected characters")
            fh.write(f"{key}={value}\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)
    for name in COMMANDS:
        p = sub.add_parser(name)
        if name == "verify-command":
            p.add_argument("--image", required=True)
            p.add_argument("--digest", required=True)
            p.add_argument("--sha", required=True)
            p.add_argument("--runner", choices=("cosign", "docker"), default="cosign")
    args = parser.parse_args(argv)
    outputs: dict[str, str] = {}
    failure = None
    try:
        outputs = COMMANDS[args.command](args) or {}
    except ReleaseError as exc:
        failure = str(exc)
    except SystemExit:
        raise
    except Exception as exc:  # fail closed, and never echo tool text outside the window
        failure = f"unexpected {type(exc).__name__}: {exc}"
    if LOG:
        emit_protected(LOG)
    if failure is not None:
        emit_protected([f"release_images {args.command}: {failure}"])
        print(f"::error::release_images {args.command} failed; see the protected log above")
        return 1
    try:
        write_outputs(outputs)
    except ReleaseError as exc:
        emit_protected([str(exc)])
        print(f"::error::release_images {args.command} failed writing outputs")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
