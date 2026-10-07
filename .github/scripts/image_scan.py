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

import contextlib
import json
import os
import re
import secrets
import subprocess
import sys
import tempfile
import shutil
import unicodedata
import urllib.parse
import urllib.request
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
# Images built from scratch (a static Go binary, no OS package database): Grype reports no distro for
# them, so check_provenance accepts an empty distro.name only for these aliases, and then requires the
# same run's CycloneDX SBOM to list Go-module components of the pinned reference (#120, ADR-0015
# amendments 2026-10-06 and 2026-10-07, G3 G4-120-08 and G4-120-10). The value is the Registry/Image the
# exemption is valid for (S-120-15): re-pointing ContainerImages.cs elsewhere loses it. A code constant
# on purpose: never read from exceptions.json, the workflow or the environment, and never consulted by
# release_images.py. Growing it needs a G3 review and a test change.
BINARY_ONLY_ALIASES = {"otelcollector": "docker.io/otel/opentelemetry-collector"}
assert set(BINARY_ONLY_ALIASES) <= set(ALIASES), "BINARY_ONLY_ALIASES must be a subset of ALIASES"
# Package types of operating-system packages. Only these may keep an exception when Grype reports a fix
# and the tag has not moved (G4-120-09): a distro fix arrives as a rebuild under the same tag, while a fix
# in a vendor binary or a bundled Java library arrives as a new tag that the check cannot see.
OS_PACKAGE_TYPES = frozenset({"apk", "deb", "rpm"})
MAX_FIXED_EXCEPTION_DAYS = 30
TAG_UNCHANGED, TAG_MOVED, TAG_UNKNOWN = "unchanged", "moved", "unknown"
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
OUT_MOUNT = "/out"  # fresh per-alias directory for the CycloneDX SBOM (G4-120-10)
SBOM_NAME = "sbom.cdx.json"
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


def scan_argv(scanner: str, ref: str, db_dir: str, out_dir: str | None = None) -> list[str]:
    """One docker run: JSON on stdout and, with out_dir, the CycloneDX SBOM into a fresh directory that
    is mounted for this run only (G4-120-10)."""
    argv = ["docker", "run", "--rm", "-v", f"{db_dir}:{DB_MOUNT}"]
    if out_dir is not None:
        argv += ["-v", f"{out_dir}:{OUT_MOUNT}"]
    for k, v in SCAN_ENV.items():
        argv += ["-e", f"{k}={v}"]
    argv += [scanner, f"registry:{ref}", "--platform", PLATFORM, "-o", "json"]
    if out_dir is not None:
        argv += ["-o", f"cyclonedx-json={OUT_MOUNT}/{SBOM_NAME}"]
    return argv


def read_sbom(out_dir: str) -> object:
    """The CycloneDX file of this run, or None when it is missing, a link, too large or not JSON."""
    path = os.path.join(out_dir, SBOM_NAME)
    try:
        if os.path.islink(path) or not os.path.isfile(path) or not 0 < os.path.getsize(path) <= 64 * 1024 * 1024:
            return None
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


# --------------------------------------------------------------------------- tag resolution (G4-120-09)

# Registry name as written in ContainerImages.cs -> (API host, token URL template). A code table: an
# unknown registry is "unknown". {image} is the percent-encoded repository path.
REGISTRY_API = {
    "docker.io": ("registry-1.docker.io",
                  "https://auth.docker.io/token?service=registry.docker.io&scope=repository:{image}:pull"),
    "quay.io": ("quay.io", "https://quay.io/v2/auth?service=quay.io&scope=repository:{image}:pull"),
    "ghcr.io": ("ghcr.io", "https://ghcr.io/token?service=ghcr.io&scope=repository:{image}:pull"),
}
MANIFEST_ACCEPT = ", ".join((
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
))
HTTP_TIMEOUT = 20  # seconds, at most 30 (G4-120-09)
DIGEST_RE = re.compile(r"sha256:[0-9a-f]{64}")
IMAGE_PATH_RE = re.compile(r"[a-z0-9._/-]+")
TAG_RE = re.compile(r"[A-Za-z0-9._-]+")
# Validated new digests of tags that moved, keyed by (registry, image, tag), for the log only.
MOVED_DIGESTS: dict[tuple[str, str, str], str] = {}


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # urllib then raises HTTPError for the 3xx: a redirect counts as a failure


def _open(req: urllib.request.Request, limit: int):
    opener = urllib.request.build_opener(_NoRedirect)  # default HTTPS handler: certificates verified
    with opener.open(req, timeout=HTTP_TIMEOUT) as resp:
        return resp.status, {k.lower(): v for k, v in resp.headers.items()}, resp.read(limit)


def _http_head(url: str, headers: dict[str, str]) -> tuple[int, dict[str, str]]:
    """Seam for tests. HEAD over HTTPS; returns (status, lower-cased headers). Raises on any error."""
    if not url.startswith("https://"):
        raise ValueError("only https is allowed")
    status, hdrs, _ = _open(urllib.request.Request(url, headers=headers, method="HEAD"), 0)
    return status, hdrs


def _http_get(url: str, headers: dict[str, str]) -> tuple[int, bytes]:
    """Seam for tests. GET over HTTPS for the anonymous token; the body is capped at 64 KiB."""
    if not url.startswith("https://"):
        raise ValueError("only https is allowed")
    status, _, body = _open(urllib.request.Request(url, headers=headers, method="GET"), 64 * 1024)
    return status, body


def _anonymous_token(token_url: str) -> str:
    status, body = _http_get(token_url, {"Accept": "application/json"})
    if status != 200:
        raise ValueError("token endpoint did not return 200")
    data = json.loads(body)
    token = data.get("token") or data.get("access_token") if isinstance(data, dict) else None
    if not isinstance(token, str) or not token:
        raise ValueError("no token in response")
    return token  # never logged


def resolve_tag_state(registry: str, image: str, tag: str, pinned_sha256: str) -> str:
    """'unchanged' only if the tag's index digest is exactly sha256:<pinned_sha256>; 'moved' if it is a
    valid other digest; 'unknown' on any error. Inputs come only from the parsed ContainerImages.cs
    constants; nothing here reads the environment."""
    try:
        if registry not in REGISTRY_API:
            return TAG_UNKNOWN
        if not (IMAGE_PATH_RE.fullmatch(image) and TAG_RE.fullmatch(tag) and re.fullmatch(r"[0-9a-f]{64}", pinned_sha256)):
            return TAG_UNKNOWN
        host, token_tpl = REGISTRY_API[registry]
        token = _anonymous_token(token_tpl.format(image=urllib.parse.quote(image, safe="")))
        url = f"https://{host}/v2/{urllib.parse.quote(image, safe='/')}/manifests/{urllib.parse.quote(tag, safe='')}"
        status, headers = _http_head(url, {"Accept": MANIFEST_ACCEPT, "Authorization": f"Bearer {token}"})
        digest = headers.get("docker-content-digest")
        if status != 200 or not isinstance(digest, str) or not DIGEST_RE.fullmatch(digest):
            return TAG_UNKNOWN
        if digest == f"sha256:{pinned_sha256}":
            return TAG_UNCHANGED
        MOVED_DIGESTS[(registry, image, tag)] = digest
        return TAG_MOVED
    except Exception:  # fail closed: any exception, timeout, redirect or HTTP error is "unknown"
        return TAG_UNKNOWN


def split_ref(ref: str) -> tuple[str, str, str, str] | None:
    """(registry, image, tag, sha256 hex) of a digest-pinned reference, or None."""
    if not REF_RE.match(ref):
        return None
    registry, rest = ref.split("/", 1)
    name_tag, digest = rest.split("@sha256:", 1)
    image, tag = name_tag.rsplit(":", 1)
    return registry, image, tag, digest


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


LAYER_PROP_RE = re.compile(r"syft:location:\d+:layerID")


def _sbom_go_components(sbom: dict) -> tuple[set[str] | None, set[str]]:
    """(purls, layer ids) of the SBOM components that are Go modules (syft:package:type == go-module and
    a pkg:golang/ purl). purls is None when 'components' is not a list."""
    components = sbom.get("components")
    if not isinstance(components, list):
        return None, set()
    purls: set[str] = set()
    layer_ids: set[str] = set()
    for c in components:
        if not isinstance(c, dict):
            continue
        purl = c.get("purl")
        props = c.get("properties")
        if not (isinstance(purl, str) and purl.startswith("pkg:golang/") and isinstance(props, list)):
            continue
        if any(isinstance(p, dict) and p.get("name") == "syft:package:type" and p.get("value") == "go-module" for p in props):
            purls.add(purl)
            for p in props:
                if isinstance(p, dict) and isinstance(p.get("name"), str) and LAYER_PROP_RE.fullmatch(p["name"]) and isinstance(p.get("value"), str):
                    layer_ids.add(p["value"])
    return purls, layer_ids


def check_binary_sbom(sbom: object, ref: str, doc: dict) -> list[str]:
    """Reasons to distrust the CycloneDX evidence of a binary-only image (G4-120-10, S-120-16). Pure.
    Empty list means: container component named Registry/Image at the pinned tag, Go-module components
    present with at least one in a layer of the JSON's source.target.layers (S-120-17), and every JSON
    go-module match has a component with the same purl (S-120-16)."""
    if not isinstance(sbom, dict):
        return ["SBOM missing or not a JSON object: binary-only evidence unavailable"]
    problems: list[str] = []
    meta = sbom.get("metadata")
    comp = meta.get("component") if isinstance(meta, dict) else None
    if not isinstance(comp, dict):
        return ["SBOM has no metadata.component: it does not identify the scanned image"]
    if comp.get("type") != "container":
        problems.append("SBOM metadata.component.type is not 'container'")
    parts = split_ref(ref)
    if parts is None:
        return ["pinned reference is not a digest-pinned reference"]
    # Grype v0.120.0 CycloneDX carries no digest: name is Registry/Image and version is the Tag (G3 ruling
    # 2026-10-07). The digest binding is structural: a fresh per-alias directory written by the run whose
    # JSON proved the digest-pinned scan.
    if comp.get("name") != f"{parts[0]}/{parts[1]}":
        problems.append("SBOM metadata.component.name does not equal the pinned Registry/Image")
    if comp.get("version") != parts[2]:
        problems.append("SBOM metadata.component.version does not equal the pinned tag")
    purls, layer_ids = _sbom_go_components(sbom)
    if purls is None:
        problems.append("SBOM has no 'components' list")
    elif not purls:
        problems.append("SBOM lists no Go-module component (syft:package:type go-module with a pkg:golang/ purl): the binary was not catalogued")
    else:
        source = doc.get("source") if isinstance(doc.get("source"), dict) else {}
        target = source.get("target") if isinstance(source.get("target"), dict) else {}
        layers = target.get("layers")
        json_layers = {l.get("digest") for l in layers if isinstance(l, dict)} if isinstance(layers, list) else set()
        json_layers.discard(None)
        if not (layer_ids & json_layers):  # S-120-17
            problems.append("no Go-module component belongs to a layer listed in the JSON source.target.layers")
        for m in doc.get("matches") or []:
            artifact = m.get("artifact") if isinstance(m, dict) else None
            if isinstance(artifact, dict) and artifact.get("type") == "go-module" and artifact.get("purl") not in purls:
                problems.append("a JSON go-module match has no SBOM component with the same purl: the two outputs disagree")
                break
    return problems


def check_provenance(doc: object, ref: str, alias: str | None = None, sbom: object = None) -> list[str]:
    """Reasons to distrust a scan result (G4-28-05). Empty list means the scan proves it scanned ref.

    alias is the scan target's alias. For an alias in BINARY_ONLY_ALIASES whose reference still points at
    the mapped Registry/Image (S-120-15), the empty-distro check is replaced by check_binary_sbom on the
    same run's CycloneDX SBOM (G4-120-10); every other check applies to all aliases. With alias None, any
    other alias or a re-pointed reference the behaviour is the strict one (a distro is required)."""
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
    parts = split_ref(ref)
    binary_only = bool(alias in BINARY_ONLY_ALIASES and parts and f"{parts[0]}/{parts[1]}" == BINARY_ONLY_ALIASES[alias])
    if not binary_only and not distro.get("name"):
        problems.append("distro.name is empty: the image was not recognised as an OS image")
    if not _db_built(doc):
        problems.append("descriptor.db.built is missing: the vulnerability database is not identified")
    if binary_only and not problems:  # read the SBOM only after every other check passed (G4-120-10 (3))
        problems += check_binary_sbom(sbom() if callable(sbom) else sbom, ref, doc)
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


def _keeps_fixed_exception(hit: dict, alias: str, artifact: dict, tag_state: str | None) -> bool:
    """The narrow D-2 rule (G4-120-09): all of alias in ALIASES, OS package type, expires - added at most
    30 days, and the tag still resolves to the pinned digest."""
    added, expires = hit.get("added_date"), hit.get("expires_date")
    return (
        tag_state == TAG_UNCHANGED
        and alias in ALIASES
        and artifact.get("type") in OS_PACKAGE_TYPES
        and isinstance(added, date) and isinstance(expires, date)
        and expires - added <= timedelta(days=MAX_FIXED_EXCEPTION_DAYS)
    )


def evaluate(doc: dict, alias: str, exceptions: list[dict], today: date, *,
             tag_state: str | None = None, tag_label: str = "") -> dict:
    """Apply the policy to one image's scanner JSON. Returns findings, excepted and used exception keys.

    tag_state is the resolved state of the image's tag ('unchanged', 'moved', 'unknown'); the default
    None means not unchanged, so callers that do not pass it (release_images.py) keep the strict rule that
    an upstream fix ends an exception at once. tag_label is text for the notes only."""
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
            if _keeps_fixed_exception(hit, alias, artifact, tag_state):
                item["note"] = f"excepted (no rebuilt image: {tag_label or 'tag'} still at the pinned digest)"
            else:
                # An upstream fix ends the exception at once, not at expiry (G6 D-2): bump the image.
                note = "exception not applied: a fix is now available, bump the image"
                eligible = alias in ALIASES and artifact.get("type") in OS_PACKAGE_TYPES
                if eligible and tag_state == TAG_MOVED:
                    note += f" (tag moved: rescan and bump{': ' + tag_label if tag_label else ''})"
                elif eligible and tag_state == TAG_UNKNOWN:
                    note += " (tag resolution failed: D-2 applied)"
                item["note"] = note
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
            cells = [r[k] for k in ("image", "package", "version", "id", "severity", "fix")]
            cells.append(r["status"] + (f": {r['note']}" if r["status"] == "excepted" and r.get("note") else ""))
            lines.append("| " + " | ".join(md_cell(c) for c in cells) + " |")
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
    cleanup = contextlib.ExitStack()
    for alias, ref in refs.items():
        log.append(f"scanning {alias}: {ref}")
        # A fresh directory per alias, mounted for this one run only, so no stale or planted SBOM is read.
        out_dir = tempfile.mkdtemp(prefix=f"grype-sbom-{alias}-", dir=runner_temp)
        cleanup.callback(shutil.rmtree, out_dir, True)
        os.chmod(out_dir, 0o777)  # the scanner container runs as another uid
        if os.listdir(out_dir):
            failed.append(f"{alias}: SBOM directory is not empty before the run")
            continue
        proc = _run(scan_argv(scanner, ref, db_dir, out_dir), log)
        if proc.returncode != 0:
            failed.append(f"{alias}: scanner exit {proc.returncode}")
            continue
        try:
            doc = json.loads(proc.stdout)
        except ValueError as exc:
            failed.append(f"{alias}: scanner output is not JSON ({exc})")
            continue
        # The SBOM is read lazily, only after the run exited 0 and the JSON passed every other check.
        problems = check_provenance(doc, ref, alias, sbom=lambda d=out_dir: read_sbom(d))
        if problems:
            failed += [f"{alias}: {p}" for p in problems]
            continue
        registry, image, tag, sha = split_ref(ref)  # refs were validated by parse_targets
        tag_state = resolve_tag_state(registry, image, tag, sha)
        tag_label = f"{image}:{tag}"
        if tag_state == TAG_MOVED and (registry, image, tag) in MOVED_DIGESTS:
            tag_label += f" now at {MOVED_DIGESTS[(registry, image, tag)]}"
        log.append(f"  {alias}: tag state {tag_state}")
        result = evaluate(doc, alias, entries, today, tag_state=tag_state, tag_label=tag_label)
        used |= result["used"]
        rows += [{**f, "status": "FAIL"} for f in result["findings"]]
        rows += [{**f, "status": "excepted"} for f in result["excepted"]]
        log.append(f"  {alias}: {len(doc['matches'])} matches, {len(result['findings'])} blocking, {len(result['excepted'])} excepted")

    cleanup.close()
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
