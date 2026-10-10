#!/usr/bin/env python3
"""Full-stack CI harness (ADR-0019, issue #123): AppHost tests, Playwright E2E and the ZAP baseline.

Stdlib only. Runs only inside GitHub Actions (GITHUB_ACTIONS=true): it trusts a development certificate,
starts containers and writes under RUNNER_TEMP, none of which belongs on a developer machine.

Subcommands
  install-aspire  install the Aspire CLI at the exact Aspire.AppHost.Sdk version of Decisya.AppHost.csproj
  apphost-tests   the Category=AppHost tests, with a per-run dev password
  e2e             start the AppHost, run Playwright (Firefox and Chromium), scan, stage
  zap             start the AppHost, run the ZAP baseline in its pinned container, scan, stage, judge

Boundaries (docs/security/threat-models/full-stack-ci.md):
- G4-123-03 The dev password is generated with `secrets`, masked on the very first output line (flushed)
  and passed only in explicit child environments (the AppHost and Playwright). Never argv, never this
  process's own environment, never a file, never a runner file command. The AppHost console goes to a
  0600 file because the dashboard prints a browser token there; the failure tail drops those lines.
  The dashboard is never made anonymous. If a start-up problem seems to need that, stop and report.
- G4-123-02 Nothing is uploaded that a completed scan did not pass: scan the whole result directory for
  the needles, then copy regular files into $RUNNER_TEMP/upload/<job>/. A hit or a scan error (symlink,
  FIFO, device, unreadable file) stages nothing and deletes the source directory.
- G4-123-01 ZAP runs with the exact argv of zap_run_argv(): host network, one non-root user, no
  capabilities, one mount (the work directory), no credential of any kind.
- G4-123-04 The ZAP reference is the single FROM line of .github/zap/Dockerfile. The Aspire CLI version
  is read from the csproj, must be x.y.z and must equal the version Marco approved (M3).

TODO (only if the dotnet-tool route fails in the spike, S1): the fallback is the release archive of that
exact version from Microsoft's release host (no redirector, no `latest`), with a SHA-256 committed in this
repository and checked before extraction into a fresh directory. Never an install script.
"""
from __future__ import annotations

import argparse
import http.client
import json
import os
import re
import secrets
import shutil
import signal
import ssl
import stat
import subprocess
import sys
import threading
import time
import traceback
import unicodedata
import urllib.parse
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPHOST_DIR = ROOT / "src" / "Decisya.AppHost"
APPHOST_CSPROJ = APPHOST_DIR / "Decisya.AppHost.csproj"
LAUNCH_SETTINGS = APPHOST_DIR / "Properties" / "launchSettings.json"
SPA_DIR = ROOT / "src" / "Decisya.Web"
ZAP_DIR = ROOT / ".github" / "zap"
ZAP_DOCKERFILE = ZAP_DIR / "Dockerfile"
POLICY_SCRIPT = ROOT / ".github" / "scripts" / "zap_policy.py"

# The Aspire CLI version Marco approved (M3, 2026-10-09). A different Aspire.AppHost.Sdk version fails the
# install until a human approves the new tool version and changes this constant.
APPROVED_ASPIRE_CLI_VERSION = "13.5.4"
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+$")
SDK_RE = re.compile(r'<Project\s+Sdk="Aspire\.AppHost\.Sdk/([^"]*)"')

BFF_HOST, BFF_PORT = "localhost", 7200
BFF_ORIGIN = f"https://{BFF_HOST}:{BFF_PORT}"
KEYCLOAK_PORT = 8080
KEYCLOAK_WELL_KNOWN = "/realms/decisya/.well-known/openid-configuration"
READY_DEADLINE_SECONDS = 360
APPHOST_TEST_TIMEOUT = 1200
PLAYWRIGHT_TIMEOUT = 1200
ZAP_TIMEOUT = 900
ZAP_UID_GID = "1000:1000"
ZAP_WORK_MOUNT = "/zap/wrk"
ZAP_REPORT_JSON = "report.json"
ZAP_REPORT_HTML = "report.html"
ZAP_URLS_FILE = "urls.txt"
ZAP_REF_RE = re.compile(r"^docker\.io/zaproxy/zap-stable:\d{8}@sha256:[0-9a-f]{64}$")
ZAP_FROM_RE = re.compile(r"^FROM\s+(\S+)\s+AS\s+zap\s*$", re.MULTILINE)
VOLUME_RE = re.compile(r"^decisya-apphosttests-[0-9a-f]{32}$")
MAX_SCAN_FILE_BYTES = 512 * 1024 * 1024

# Aspire parameters the AppHost would otherwise generate and persist (S-123-03), and the one it requires.
# The last three names are the resource-password parameters Aspire creates for Postgres, Redis and Keycloak;
# if a name is wrong the value is simply unused, and the persisted-parameters line in the log says so.
STACK_PARAMETERS = (
    "dev-user-password",
    "bff-client-secret",
    "keycloak-db-password",
    "tenancy-db-password",
    "entitlements-db-password",
    "postgres-password",
    "redis-password",
    "keycloak-password",
)
PASSWORD_PARAMETER = "dev-user-password"
LAUNCH_ENV_KEYS = (
    "ASPNETCORE_ENVIRONMENT",
    "DOTNET_ENVIRONMENT",
    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL",
    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL",
)


class HarnessError(Exception):
    """A failure with a message that is safe to print (never carries a value from the environment)."""


# --------------------------------------------------------------------------- output

def neutralize(text: object) -> str:
    out = []
    for ch in str(text):
        if unicodedata.category(ch).startswith("C"):
            out.append("\\x%02x" % ord(ch) if ord(ch) < 256 else "\\u%04x" % ord(ch))
        else:
            out.append(ch)
    return "".join(out)


def emit_protected(lines: list[str]) -> None:
    """Print lines between stop-commands markers with a token that no program output can know."""
    token = secrets.token_hex(16)
    print(f"::stop-commands::{token}")
    for line in lines:
        print(neutralize(line))
    print(f"::{token}::", flush=True)


def log(message: str) -> None:
    print(f"[fullstack] {neutralize(message)}", flush=True)


def _line_for_log(text: str) -> str:
    """A program output line as safe text: a line that starts like a workflow command is shifted."""
    if text.lstrip().startswith(("::", "##[")):
        return "| " + text
    return text


# --------------------------------------------------------------------------- secrets and needles

def _mask(value: str) -> None:
    sys.stdout.write(f"::add-mask::{value}\n")
    sys.stdout.flush()


class RunSecrets:
    """The per-run values. Constructing it prints the masks before anything else can print or run (G4-123-03 a)."""

    def __init__(self) -> None:
        self.password = secrets.token_hex(24)
        _mask(self.password)
        self.parameters: dict[str, str] = {PASSWORD_PARAMETER: self.password}
        for name in STACK_PARAMETERS:
            if name not in self.parameters:
                value = secrets.token_hex(24)
                _mask(value)
                self.parameters[name] = value

    def all_values(self) -> list[str]:
        return list(self.parameters.values())


def secret_forms(value: str) -> set[str]:
    """The four forms the Playwright teardown looks for: raw, URL-encoded, form-encoded, JSON-escaped."""
    return {
        value,
        urllib.parse.quote(value, safe="-_.!~*'()"),
        urllib.parse.urlencode({"p": value})[2:],
        json.dumps(value)[1:-1],
    } - {""}


JWT_RE = re.compile(rb"eyJ[\w-]+\.[\w-]+\.", re.ASCII)
SESSION_COOKIE_RE = re.compile(rb"__Host-decisya-session(?:C\d+)?=", re.IGNORECASE)
LOGIN_TOKEN_RE = re.compile(rb"login\?t=", re.IGNORECASE)
KEYCLOAK_COOKIE_RE = re.compile(
    rb"(?:AUTH_SESSION_ID|KEYCLOAK_IDENTITY|KEYCLOAK_SESSION|KC_RESTART)(?:_LEGACY)?=", re.IGNORECASE)
# #123 spike 2: the Aspire tunnel proxy prints its TLS key as base64 JSON. The base64 hides the PEM
# header, so match the PEM header, the base64 of "-----BEGIN" (any encoded PEM block, certificates
# included; intended, fail closed) and the JSON field names.
PRIVATE_KEY_RE = re.compile(rb"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----")
BASE64_PEM_RE = re.compile(rb"LS0tLS1CRUdJTi")
KEY_FIELD_RE = re.compile(rb"_key_base64|\"(?:server|client|private)_key\"", re.IGNORECASE)
PATTERN_NEEDLES = (JWT_RE, SESSION_COOKIE_RE, LOGIN_TOKEN_RE, KEYCLOAK_COOKIE_RE,
                   PRIVATE_KEY_RE, BASE64_PEM_RE, KEY_FIELD_RE)


class Needles:
    """What must never reach a log tail or an artifact (G4-123-02 b, S-123-07)."""

    def __init__(self, values: list[str] | None = None) -> None:
        literals: set[bytes] = set()
        for value in values or []:
            literals |= {form.encode("utf-8") for form in secret_forms(value)}
        self.literals = sorted(literals)

    def matches(self, data: bytes) -> bool:
        return any(lit in data for lit in self.literals) or any(p.search(data) for p in PATTERN_NEEDLES)

    def line_matches(self, text: str) -> bool:
        return self.matches(text.encode("utf-8", errors="replace"))


# --------------------------------------------------------------------------- environments

def _runner_key(name: str, *parts: str) -> str:
    return name + "".join(parts)


def base_env() -> dict[str, str]:
    """A child environment without credentials: no GITHUB_* command files, no ACTIONS_*, nothing that
    looks like a token, secret or password, no Aspire parameter, no E2E password. The caller adds what
    its one child needs (G4-123-03 b)."""
    env: dict[str, str] = {}
    for key, value in os.environ.items():
        upper = key.upper()
        if key.startswith(("Parameters__", "ACTIONS_", "E2E_")):
            continue
        if upper.startswith("GITHUB_") and key != "GITHUB_ACTIONS":
            continue
        if any(word in upper for word in ("TOKEN", "SECRET", "PASSWORD")):
            continue
        if key == _runner_key("DOTNET_NUGET_SIGNATURE_", "VERIFICATION"):
            continue  # NuGet signature verification stays at its default (G4-123-04 a)
        env[key] = value
    env.setdefault("PATH", os.defpath)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    return env


def runner_temp() -> Path:
    value = os.environ.get("RUNNER_TEMP")
    if not value:
        raise HarnessError("RUNNER_TEMP is not set")
    return Path(value)


def require_ci() -> None:
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise HarnessError("fullstack.py runs only inside GitHub Actions (it trusts a dev certificate and starts containers)")


def aspire_dir() -> Path:
    return runner_temp() / "aspire-cli"


def dev_cert_trust_dir() -> Path:
    return Path.home() / ".aspnet" / "dev-certs" / "trust"


def dev_cert_export_dir() -> Path:
    return runner_temp() / "devcert"


def scoped_env() -> dict[str, str]:
    """base_env plus the Aspire CLI on PATH and the dev certificate trust directory for OpenSSL clients.
    SSL_CERT_DIR is set here, for one child at a time, never for the job (S-123-04)."""
    env = base_env()
    env["PATH"] = str(aspire_dir()) + os.pathsep + env["PATH"]
    env["SSL_CERT_DIR"] = f"{dev_cert_trust_dir()}{os.pathsep}/usr/lib/ssl/certs"
    return env


def launch_profile() -> dict:
    """The https launch profile of the AppHost, reduced to what a direct DLL start needs.

    Returns {"env": {...}, "ports": {...}}. Only the four allow-listed variables are taken, the dashboard
    address is the https one only, and every URL must be https on localhost (G4-123-03 e)."""
    data = json.loads(LAUNCH_SETTINGS.read_text(encoding="utf-8"))
    profiles = data["profiles"]
    https = profiles["https"]
    env = {k: https["environmentVariables"][k] for k in LAUNCH_ENV_KEYS}
    https_urls = [u for u in https["applicationUrl"].split(";") if u.startswith("https://")]
    if len(https_urls) != 1:
        raise HarnessError("the https launch profile must have exactly one https applicationUrl")
    env["ASPNETCORE_URLS"] = https_urls[0]
    ports: set[int] = set()
    for key in ("ASPNETCORE_URLS", "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"):
        parts = urllib.parse.urlsplit(env[key])
        if parts.scheme != "https" or parts.hostname != "localhost" or parts.port is None:
            raise HarnessError(f"launch profile value for {key} is not an https localhost URL")
        ports.add(parts.port)
    for profile in profiles.values():
        for url in str(profile.get("applicationUrl", "")).split(";"):
            if url:
                ports.add(urllib.parse.urlsplit(url).port)
        for key in ("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"):
            url = profile.get("environmentVariables", {}).get(key)
            if url:
                ports.add(urllib.parse.urlsplit(url).port)
    ports.discard(None)
    ports.add(KEYCLOAK_PORT)
    return {"env": env, "ports": ports}


def apphost_env(run_secrets: RunSecrets) -> dict[str, str]:
    """The environment of the AppHost child (G4-123-03 b): the only place the stack's values go."""
    env = scoped_env()
    env.update(launch_profile()["env"])
    for name, value in run_secrets.parameters.items():
        env[f"Parameters__{name}"] = value
    return env


# --------------------------------------------------------------------------- inputs

def read_aspire_version(csproj_text: str) -> str:
    """The exact Aspire.AppHost.Sdk version, validated before it reaches any command line (G4-123-04 a)."""
    found = SDK_RE.findall(csproj_text)
    if len(found) != 1:
        raise HarnessError("Decisya.AppHost.csproj must name Aspire.AppHost.Sdk/<x.y.z> exactly once")
    version = found[0]
    if not VERSION_RE.fullmatch(version):
        raise HarnessError("the Aspire.AppHost.Sdk version is not of the form x.y.z")
    if version != APPROVED_ASPIRE_CLI_VERSION:
        raise HarnessError(
            "the Aspire.AppHost.Sdk version is not the Aspire CLI version Marco approved (M3): "
            "a new tool version needs his approval and a change of APPROVED_ASPIRE_CLI_VERSION")
    return version


def parse_zap_ref(dockerfile_text: str) -> str:
    """The one reference of .github/zap/Dockerfile: dated build tag plus digest, nothing else (G4-123-04 c)."""
    found = ZAP_FROM_RE.findall(dockerfile_text)
    if len(found) != 1:
        raise HarnessError("zap/Dockerfile: expected exactly one 'FROM <ref> AS zap' line")
    if not ZAP_REF_RE.fullmatch(found[0]):
        raise HarnessError("zap/Dockerfile: the reference is not docker.io/zaproxy/zap-stable:<8 digits>@sha256:<64 hex>")
    return found[0]


def zap_run_argv(image: str, workdir: str | Path) -> list[str]:
    """The one docker run of the ZAP container (G4-123-01 a, S-123-01). No socket, no environment option,
    no other mount, no extra capability, no root.

    The scan is the traditional spider plus the hook's seeds only. There is no AJAX spider (`-j`): it drives
    a browser through its own internal proxy, escapes the context and the hook's exclusions, and followed
    /bff/login into Keycloak (#123 spike 3, Marco 2026-10-09). Binding it is backlog #83."""
    if not ZAP_REF_RE.fullmatch(image):
        raise HarnessError("refusing to run a ZAP image reference that is not the digest-pinned one")
    return [
        "docker", "run", "--rm",
        "--network", "host",
        "--user", ZAP_UID_GID,
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--pids-limit", "2048",
        "--memory", "8g",
        "-v", f"{workdir}:{ZAP_WORK_MOUNT}:rw",
        image,
        "zap-baseline.py",
        "-t", BFF_ORIGIN,
        "-n", "context.context",
        "--hook", f"{ZAP_WORK_MOUNT}/hook.py",
        "--autooff",
        "-m", "2",
        "-T", "10",
        "-P", "18090",
        "-z", "-silent -config start.checkForUpdates=false",
        "-J", ZAP_REPORT_JSON,
        "-r", ZAP_REPORT_HTML,
    ]


def check_zap_context(context_text: str, launch_ports: set[int]) -> list[str]:
    """Problems with the committed ZAP context (G4-123-01 b): exactly one include (the BFF origin), and an
    exclude for every port the AppHost opens on the runner, in both schemes."""
    problems: list[str] = []
    try:
        root = ET.fromstring(context_text)
    except ET.ParseError:
        return ["context.context is not well-formed XML"]
    includes = [(e.text or "").strip() for e in root.findall("./context/incregexes")]
    if includes != [f"^https://{BFF_HOST}:{BFF_PORT}(/.*)?$"]:
        problems.append("context.context must include exactly the BFF origin")
    excludes = [(e.text or "").strip() for e in root.findall("./context/excregexes")]
    for port in sorted(launch_ports):
        if port == BFF_PORT:
            continue
        if f"^https?://localhost:{port}(/.*)?$" not in excludes:
            problems.append(f"context.context does not exclude localhost:{port} (both schemes)")
    return problems


# --------------------------------------------------------------------------- processes

def _killpg(proc: subprocess.Popen, sig: int) -> None:
    try:
        os.killpg(proc.pid, sig)
    except (ProcessLookupError, PermissionError):
        pass


def run_bounded(argv: list[str], *, env: dict[str, str], timeout: float, cwd: Path | None = None,
                needles: Needles | None = None, protect: bool = False) -> int:
    """Run a child in its own process group with a hard deadline; stream its output to the job log with
    lines that match a needle withheld. Returns the exit code, or 124 after a timeout. With protect=True the
    streamed output sits between stop-commands markers (random token) and every line is neutralized."""
    token = secrets.token_hex(16) if protect else ""
    if protect:
        print(f"::stop-commands::{token}", flush=True)
    try:
        return _run_bounded(argv, env=env, timeout=timeout, cwd=cwd, needles=needles, protect=protect)
    finally:
        if protect:
            print(f"::{token}::", flush=True)


def _run_bounded(argv: list[str], *, env: dict[str, str], timeout: float, cwd: Path | None,
                 needles: Needles | None, protect: bool) -> int:
    sigkill = getattr(signal, "SIGKILL", signal.SIGTERM)
    proc = subprocess.Popen(argv, env=env, cwd=cwd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, start_new_session=True)
    timed_out = threading.Event()

    def _expire() -> None:
        timed_out.set()
        _killpg(proc, sigkill)

    timer = threading.Timer(timeout, _expire)
    timer.daemon = True
    timer.start()
    withheld = 0
    try:
        assert proc.stdout is not None
        for raw in iter(proc.stdout.readline, b""):
            text = raw.decode("utf-8", errors="replace").rstrip("\r\n")
            if needles is not None and needles.line_matches(text):
                withheld += 1
                continue
            line = _line_for_log(text)
            print(neutralize(line) if protect else line, flush=True)
        code = proc.wait()
    finally:
        timer.cancel()
        _killpg(proc, sigkill)
    if withheld:
        log(f"{withheld} output line(s) withheld (they matched a secret needle)")
    if timed_out.is_set():
        log(f"a child exceeded its {int(timeout)} s limit and was killed")
        return 124
    return code


# --------------------------------------------------------------------------- Aspire CLI and dev certificate

def cmd_install_aspire() -> int:
    require_ci()
    version = read_aspire_version(APPHOST_CSPROJ.read_text(encoding="utf-8"))
    dest = aspire_dir()
    started = time.monotonic()
    code = run_bounded(
        ["dotnet", "tool", "install", "Aspire.Cli", "--version", version, "--tool-path", str(dest)],
        env=base_env(), timeout=300, cwd=ROOT)
    if code != 0:
        raise HarnessError(f"dotnet tool install Aspire.Cli {version} failed (exit {code}); see the TODO in the module docstring")
    result = subprocess.run([str(dest / "aspire"), "--version"], env=scoped_env(), cwd=ROOT, timeout=120,
                            capture_output=True, text=True, errors="replace", stdin=subprocess.DEVNULL)
    lines = [ln.strip() for ln in (result.stdout or "").splitlines() if ln.strip()]
    ok = result.returncode == 0 and any(re.fullmatch(re.escape(version) + r"(\+[0-9A-Za-z.]+)?", ln) for ln in lines)
    log(f"aspire --version exit {result.returncode}: " + " | ".join(lines[-3:]))
    if not ok:
        raise HarnessError(f"aspire --version does not report the installed version {version}")
    log(f"step=install-aspire version={version} elapsed={time.monotonic() - started:.0f}s")
    return 0


def ensure_dev_cert() -> None:
    """Create, trust and confirm the dev certificate; export its public part for the readiness probe."""
    env = scoped_env()
    started = time.monotonic()
    for argv in (["dotnet", "dev-certs", "https"], ["dotnet", "dev-certs", "https", "--trust"]):
        run_bounded(argv, env=env, timeout=120, cwd=ROOT)  # exit codes are judged by --check below
    if run_bounded(["dotnet", "dev-certs", "https", "--check", "--trust"], env=env, timeout=120, cwd=ROOT) != 0:
        raise HarnessError("dotnet dev-certs https --check --trust says the dev certificate is not trusted (S-123-04)")
    export = dev_cert_export_dir()
    shutil.rmtree(export, ignore_errors=True)
    export.mkdir(parents=True, mode=0o700)
    # No --password and no --no-password: the public certificate only. A key file would be a failure.
    code = run_bounded(["dotnet", "dev-certs", "https", "--export-path", str(export / "devcert.pem"), "--format", "Pem"],
                       env=env, timeout=120, cwd=ROOT)
    keys = [p for p in export.iterdir() if p.suffix.lower() == ".key"]
    for key in keys:
        key.unlink()
    if keys:
        raise HarnessError("the dev certificate export produced a private key file; it was deleted and the run stops")
    if code != 0:
        log("exporting the public dev certificate failed; the probe will use the trust directory instead")
    log(f"step=dev-cert elapsed={time.monotonic() - started:.0f}s")


def trusted_context() -> ssl.SSLContext:
    """A TLS context that verifies the BFF's certificate against the dev certificate. Hostname and chain
    checks stay on (S-123-04); only OpenSSL's strict X.509 profile is relaxed, because a self-signed
    development certificate does not meet it."""
    ctx = ssl.create_default_context()
    strict = getattr(ssl, "VERIFY_X509_STRICT", 0)
    ctx.verify_flags &= ~strict
    loaded = 0
    candidates = sorted(dev_cert_export_dir().glob("*.pem")) if dev_cert_export_dir().is_dir() else []
    if not candidates and dev_cert_trust_dir().is_dir():
        candidates = sorted(dev_cert_trust_dir().glob("*.pem")) + sorted(dev_cert_trust_dir().glob("*.crt"))
    for path in candidates:
        ctx.load_verify_locations(cafile=str(path))
        loaded += 1
    if loaded == 0:
        raise HarnessError("no dev certificate file found to verify TLS against; refusing to probe without verification")
    return ctx


# --------------------------------------------------------------------------- the stack

def resolve_apphost_dll() -> Path:
    result = subprocess.run(
        ["dotnet", "msbuild", str(APPHOST_CSPROJ), "-getProperty:TargetPath", "-p:Configuration=Release"],
        env=base_env(), cwd=ROOT, timeout=180, capture_output=True, text=True, errors="replace", stdin=subprocess.DEVNULL)
    lines = [ln.strip() for ln in (result.stdout or "").splitlines() if ln.strip()]
    if result.returncode != 0 or not lines:
        raise HarnessError("MSBuild returned no TargetPath for Decisya.AppHost")
    dll = Path(lines[-1])
    if dll.name != "Decisya.AppHost.dll" or not dll.is_file():
        raise HarnessError("Decisya.AppHost.dll was not found at the TargetPath MSBuild reported (was the Release build run?)")
    return dll


def http_get(host: str, port: int, path: str, *, tls: ssl.SSLContext | None, headers: dict[str, str] | None = None,
             timeout: float = 10) -> tuple[int, bytes]:
    conn: http.client.HTTPConnection
    if tls is not None:
        conn = http.client.HTTPSConnection(host, port, timeout=timeout, context=tls)
    else:
        conn = http.client.HTTPConnection(host, port, timeout=timeout)
    try:
        conn.request("GET", path, headers=headers or {})
        response = conn.getresponse()
        return response.status, response.read(1 << 20)
    finally:
        conn.close()


class Stack:
    """The AppHost DLL in run mode, with ephemeral containers and a throwaway data volume (G2 D1)."""

    def __init__(self, run_secrets: RunSecrets, needles: Needles) -> None:
        self.secrets = run_secrets
        self.needles = needles
        self.log_path = runner_temp() / "apphost.log"
        self.volume = f"decisya-apphosttests-{secrets.token_hex(16)}"
        self.proc: subprocess.Popen | None = None

    def start(self, dll: Path) -> None:
        if not VOLUME_RE.fullmatch(self.volume):
            raise HarnessError("generated volume name has the wrong shape")
        argv = ["dotnet", str(dll), f"--Postgres:DataVolumeName={self.volume}", "--AppHost:UseEphemeralContainers=true"]
        fd = os.open(self.log_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        try:
            os.chmod(self.log_path, 0o600)
            self.proc = subprocess.Popen(argv, env=apphost_env(self.secrets), cwd=APPHOST_DIR, stdin=subprocess.DEVNULL,
                                         stdout=fd, stderr=subprocess.STDOUT, start_new_session=True)
        finally:
            os.close(fd)
        log("AppHost started; its console goes to a 0600 file, never to this log")

    def alive(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def wait_ready(self) -> None:
        """Three checks, one shared deadline; the failure names the check that did not pass (G2 D1 step 4)."""
        tls = trusted_context()
        json_headers = {"Accept": "application/json"}

        def keycloak() -> bool:
            # On the runner Aspire serves Keycloak's port-8080 endpoint as HTTPS via its proxy
            # (#123 spike 2 probes: http RemoteDisconnected, https 200).
            status, _ = http_get("localhost", KEYCLOAK_PORT, KEYCLOAK_WELL_KNOWN, tls=tls)
            return status == 200

        def bff_me() -> bool:
            status, body = http_get(BFF_HOST, BFF_PORT, "/bff/me", tls=tls, headers=json_headers)
            return status == 200 and isinstance(json.loads(body), dict) and json.loads(body).get("isAuthenticated") is False

        def api_via_bff() -> bool:
            status, _ = http_get(BFF_HOST, BFF_PORT, "/api/capabilities", tls=tls, headers=json_headers)
            return status == 401

        checks = [("keycloak discovery document (https)", keycloak), ("bff /bff/me", bff_me), ("api through the bff (401)", api_via_bff)]
        passed: set[str] = set()
        deadline = time.monotonic() + READY_DEADLINE_SECONDS
        started = time.monotonic()
        last_error: dict[str, str] = {}
        while time.monotonic() < deadline:
            if not self.alive():
                raise HarnessError("the AppHost process exited before the stack became ready")
            for name, check in checks:
                if name in passed:
                    continue
                try:
                    if check():
                        passed.add(name)
                        log(f"ready: {name} after {time.monotonic() - started:.0f}s")
                except (OSError, ValueError, http.client.HTTPException) as exc:
                    last_error[name] = type(exc).__name__
            if len(passed) == len(checks):
                break
            time.sleep(3)
        missing = [name for name, _ in checks if name not in passed]
        if missing:
            detail = ", ".join(f"{n} (last error: {last_error.get(n, 'none')})" for n in missing)
            raise HarnessError(f"readiness check(s) not passed within {READY_DEADLINE_SECONDS} s: {detail}")
        # One warm-up so the first sign-in in Playwright is not a cold start.
        try:
            http_get(BFF_HOST, BFF_PORT, "/", tls=tls, headers={"Accept": "text/html"}, timeout=60)
        except (OSError, http.client.HTTPException) as exc:
            raise HarnessError(f"the warm-up request to / failed ({type(exc).__name__})") from None
        log(f"step=ready elapsed={time.monotonic() - started:.0f}s")

    def tail(self, count: int = 200) -> None:
        """The last lines of the AppHost console, minus the dashboard token and every needle (G4-123-03 d)."""
        try:
            lines = self.log_path.read_text(encoding="utf-8", errors="replace").splitlines()
        except OSError:
            emit_protected(["AppHost log: unreadable"])
            return
        kept = [ln for ln in lines if not self.needles.line_matches(ln)]
        emit_protected([f"AppHost log tail ({len(lines) - len(kept)} line(s) withheld):"] + kept[-count:])

    def _diag_run(self, argv: list[str]) -> list[str]:
        """One diagnostic child (no shell, no credentials, 30 s). Returns its output lines, or one line
        naming the exception class; never raises (spike diagnostics, issue #123)."""
        try:
            result = subprocess.run(argv, env=base_env(), stdin=subprocess.DEVNULL, timeout=30,
                                    capture_output=True, text=True, errors="replace", check=False)
        except (OSError, subprocess.SubprocessError) as exc:
            return [f"{argv[0]} {argv[1] if len(argv) > 1 else ''}: failed ({type(exc).__name__})"]
        return ((result.stdout or "") + (result.stderr or "")).splitlines()

    def _diag_probe(self, scheme: str) -> str:
        """One probe of the Keycloak discovery document, reported as `status <n>` or the exception class
        (plus the reason of an ssl.SSLError). TLS is always verified; there is no unverified mode."""
        try:
            tls = None if scheme == "http" else trusted_context()
            status, _ = http_get("localhost", KEYCLOAK_PORT, KEYCLOAK_WELL_KNOWN, tls=tls, timeout=10)
            return f"status {status}"
        except (OSError, ValueError, http.client.HTTPException, HarnessError) as exc:
            name = type(exc).__name__
            if isinstance(exc, ssl.SSLError):
                name += f" ({getattr(exc, 'reason', None)})"
            return name

    def diagnose(self) -> None:
        """Evidence for a failed readiness check, printed while the containers still run. Never raises.
        Every line is dropped if it matches a needle (G4-123-03 d, same rule as tail)."""
        lines: list[str] = []
        try:
            lines.append("diagnose: docker ps")
            listing = self._diag_run(
                ["docker", "ps", "--all", "--format", "{{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}"])
            lines.extend(listing)
            name_re = re.compile(r"^[a-z0-9][a-z0-9_.-]{0,127}$")
            for row in listing:
                name = row.split("\t", 1)[0]
                if not name.startswith(("keycloak-", "aspire-container-network-tunnelproxy-")):
                    continue
                if not name_re.fullmatch(name):
                    continue
                lines.append(f"diagnose: {name} port")
                lines.extend(self._diag_run(["docker", "port", name]))
                lines.append(f"diagnose: {name} state/health")
                lines.extend(self._diag_run(
                    ["docker", "inspect", "--format",
                     "{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}", name]))
                if name.startswith("aspire-container-network-tunnelproxy-"):
                    # #123 spike 2: this container prints its TLS key as base64 JSON; never fetch its logs.
                    lines.append(f"diagnose: {name} logs not fetched (they carry a TLS private key)")
                    continue
                lines.append(f"diagnose: {name} logs (last 100)")
                lines.extend(self._diag_run(["docker", "logs", "--tail", "100", name]))
            for scheme in ("http", "https"):
                lines.append(f"diagnose: probe {scheme}://localhost:{KEYCLOAK_PORT} -> {self._diag_probe(scheme)}")
        except Exception as exc:  # diagnostics must never change the outcome of the run
            lines.append(f"diagnose: aborted ({type(exc).__name__})")
        kept = [ln for ln in lines if not self.needles.line_matches(ln)]
        withheld = len(lines) - len(kept)
        emit_protected(kept + [f"diagnose: {withheld} line(s) withheld (they matched a secret needle)"])

    def stop(self) -> None:
        proc = self.proc
        if proc is None:
            return
        sigint = signal.SIGINT
        sigterm = signal.SIGTERM
        sigkill = getattr(signal, "SIGKILL", signal.SIGTERM)
        try:
            # SIGINT goes to the AppHost alone so it can stop its containers in order; the later signals
            # go to the whole process group.
            for sig, wait in ((sigint, 30), (sigterm, 15), (sigkill, 10)):
                if proc.poll() is not None:
                    break
                if sig == sigint:
                    try:
                        os.kill(proc.pid, sig)
                    except (ProcessLookupError, PermissionError):
                        pass
                else:
                    _killpg(proc, sig)
                try:
                    proc.wait(timeout=wait)
                except subprocess.TimeoutExpired:
                    continue
        finally:
            _killpg(proc, sigkill)
        if VOLUME_RE.fullmatch(self.volume):
            subprocess.run(["docker", "volume", "rm", self.volume], env=base_env(), timeout=60,
                           stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        listing = subprocess.run(["docker", "ps", "--format", "{{.Names}}"], env=base_env(), timeout=60,
                                 capture_output=True, text=True, errors="replace", stdin=subprocess.DEVNULL, check=False)
        log("containers still running after stop: " + (", ".join((listing.stdout or "").split()) or "none"))


def report_persisted_parameters() -> None:
    """S-123-03 signal: did Aspire persist a stack parameter (a `Parameters:` key) into the runner's user store?
    S-123-03 is about stack parameters. Aspire's own AppHost:DashboardApiKey and AppHost:OtlpApiKey are an
    accepted residual (G6, backlog B-1). Key names only; a value is never read into the output."""
    match = re.search(r"<UserSecretsId>([^<]+)</UserSecretsId>", APPHOST_CSPROJ.read_text(encoding="utf-8"))
    if not match:
        return
    store = Path.home() / ".microsoft" / "usersecrets" / match.group(1).strip()
    if not store.is_dir():
        log("persisted stack parameters on the runner: none (no user-secrets store)")
        return
    unreadable = False
    parameter_names: list[str] = []
    # S-123-03 spike signal, approved by Marco 2026-10-09: file names and key NAMES only. A value is never
    # printed, logged, stored or compared.
    out: list[str] = []
    try:
        out.append("user-secrets files: " + (", ".join(sorted(neutralize(p.name) for p in store.iterdir())) or "none"))
    except OSError as exc:
        out.append(f"user-secrets files: unreadable ({type(exc).__name__})")
    secrets_file = store / "secrets.json"
    try:
        info = secrets_file.lstat() if secrets_file.exists() or secrets_file.is_symlink() else None
        if info is not None and stat.S_ISREG(info.st_mode) and info.st_size <= 1024 * 1024:
            data = json.loads(secrets_file.read_text(encoding="utf-8-sig"))  # .NET writes a BOM
            if isinstance(data, dict):
                name_ok = re.compile(r"^[A-Za-z0-9:_.\-]{1,200}$")
                names = [neutralize(k) if isinstance(k, str) and name_ok.fullmatch(k) else "<unprintable name>"
                         for k in sorted(str(key) for key in data)]
                out.append("secrets.json key names: " + (", ".join(names) or "none"))
                parameter_names = [n for k, n in zip(sorted(str(key) for key in data), names)
                                   if k.lower().startswith("parameters:")]
    except (OSError, ValueError) as exc:  # json.JSONDecodeError and UnicodeDecodeError are ValueErrors
        out.append(f"secrets.json: unreadable ({type(exc).__name__})")
        unreadable = True
    if unreadable:
        log("persisted stack parameters on the runner: unknown (secrets.json unreadable)")
    elif parameter_names:
        print(f"::warning::fullstack: persisted stack parameters on the runner (S-123-03): "
              f"{', '.join(parameter_names)}", flush=True)
    else:
        log("persisted stack parameters on the runner: none")
    emit_protected(out)


def run_with_stack(run_secrets: RunSecrets, needles: Needles, body) -> int:
    """Start the AppHost, wait for readiness, run body(stack) -> exit code, and always stop the stack."""
    ensure_dev_cert()
    dll = resolve_apphost_dll()
    stack = Stack(run_secrets, needles)
    code = 1
    try:
        stack.start(dll)
        stack.wait_ready()
        code = body(stack)
    except HarnessError as exc:
        print(f"::error::fullstack: {neutralize(exc)}", flush=True)
        code = 1
    finally:
        if code != 0:
            stack.tail()
            stack.diagnose()  # before stop(): the containers must still be running
        stack.stop()
        report_persisted_parameters()
    return code


# --------------------------------------------------------------------------- scan and stage

class ScanError(Exception):
    """The scan could not complete (a link, a special file, an unreadable or oversized file)."""


def scan_tree(root: Path, needles: Needles) -> list[str]:
    """Relative paths of the files that hold a needle. Raises ScanError or OSError when the scan cannot be
    completed. Files are read as bytes. A missing root has nothing to scan."""
    hits: list[str] = []
    if not os.path.lexists(root):
        return hits
    if not stat.S_ISDIR(os.lstat(root).st_mode):
        raise ScanError("the result root is not a directory")

    def _raise(err: OSError) -> None:
        raise err

    for dirpath, dirnames, filenames in os.walk(root, followlinks=False, onerror=_raise):
        for name in list(dirnames) + list(filenames):
            full = os.path.join(dirpath, name)
            mode = os.lstat(full).st_mode
            if not (stat.S_ISDIR(mode) or stat.S_ISREG(mode)):
                raise ScanError(f"link or special file: {os.path.relpath(full, root)}")
        for name in filenames:
            full = os.path.join(dirpath, name)
            flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0)
            fd = os.open(full, flags)
            try:
                info = os.fstat(fd)
                if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_SCAN_FILE_BYTES:
                    raise ScanError(f"not a regular file or too large: {os.path.relpath(full, root)}")
                with os.fdopen(fd, "rb", closefd=False) as handle:
                    data = handle.read()
            finally:
                os.close(fd)
            if needles.matches(data):
                hits.append(os.path.relpath(full, root))
    return hits


def _copy_regular_files(source: Path, staging: Path, only: tuple[str, ...] | None) -> int:
    count = 0
    staging.mkdir(parents=True, mode=0o700)
    for dirpath, _dirnames, filenames in os.walk(source, followlinks=False):
        for name in filenames:
            full = Path(dirpath) / name
            relative = full.relative_to(source)
            if only is not None and relative.as_posix() not in only:
                continue
            if not stat.S_ISREG(os.lstat(full).st_mode):
                raise ScanError(f"not a regular file at copy time: {relative}")
            target = staging / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(full, target)
            count += 1
    return count


def scan_and_stage(source: Path, staging: Path, needles: Needles, only: tuple[str, ...] | None = None) -> bool:
    """Scan the whole source directory; only if the scan completes clean, copy its regular files (or just
    the names in `only`) into a fresh staging directory. A hit or any scan error deletes the source and
    stages nothing (G4-123-02). Messages name files, never values. Returns True when it is safe."""
    def _fail(reason: str, files: list[str] | None = None) -> bool:
        shutil.rmtree(source, ignore_errors=True)
        shutil.rmtree(staging, ignore_errors=True)
        emit_protected([f"artifact scan: {reason}"] + [f"  {f}" for f in (files or [])])
        print("::error::fullstack: the artifact scan failed; the result directory was deleted and nothing is uploaded", flush=True)
        return False

    try:
        hits = scan_tree(source, needles)
    except (ScanError, OSError) as exc:
        return _fail(f"scan could not complete ({type(exc).__name__}: {neutralize(exc)})")
    if hits:
        return _fail("a secret needle was found in these files, which were deleted with their directory", hits)
    if not os.path.lexists(source):
        return True
    shutil.rmtree(staging, ignore_errors=True)
    try:
        staged = _copy_regular_files(source, staging, only)
    except (ScanError, OSError) as exc:
        return _fail(f"staging failed ({type(exc).__name__}: {neutralize(exc)})")
    log(f"staged {staged} file(s) for upload")
    return True


# --------------------------------------------------------------------------- subcommands

def cmd_apphost_tests() -> int:
    require_ci()
    run_secrets = RunSecrets()
    needles = Needles(run_secrets.all_values())
    ensure_dev_cert()
    env = scoped_env()
    for name, value in run_secrets.parameters.items():
        env[f"Parameters__{name}"] = value
    started = time.monotonic()
    code = run_bounded(
        ["dotnet", "test", "--project", "tests/Decisya.AppHost.Tests", "--no-build",
         "--filter-trait", "Category=AppHost", "--report-xunit-trx"],
        env=env, timeout=APPHOST_TEST_TIMEOUT, cwd=ROOT, needles=needles)
    log(f"step=apphost-tests exit={code} elapsed={time.monotonic() - started:.0f}s")
    return code


def cmd_e2e() -> int:
    require_ci()
    run_secrets = RunSecrets()
    needles = Needles(run_secrets.all_values())

    def body(_stack: Stack) -> int:
        env = base_env()
        env["E2E_DEV_PASSWORD"] = run_secrets.password
        started = time.monotonic()
        code = run_bounded(["npx", "--no-install", "playwright", "test"], env=env, timeout=PLAYWRIGHT_TIMEOUT,
                           cwd=SPA_DIR, needles=needles)
        log(f"step=playwright exit={code} elapsed={time.monotonic() - started:.0f}s")
        return code

    code = run_with_stack(run_secrets, needles, body)
    staged = scan_and_stage(SPA_DIR / "test-results", runner_temp() / "upload" / "e2e", needles)
    return code if staged else 1


def cmd_zap() -> int:
    require_ci()
    run_secrets = RunSecrets()
    needles = Needles(run_secrets.all_values())
    image = parse_zap_ref(ZAP_DOCKERFILE.read_text(encoding="utf-8"))
    problems = check_zap_context((ZAP_DIR / "context.context").read_text(encoding="utf-8"), launch_profile()["ports"])
    if problems:
        raise HarnessError("; ".join(problems))
    workdir = runner_temp() / "zap-wrk"
    shutil.rmtree(workdir, ignore_errors=True)
    workdir.mkdir(parents=True)
    os.chmod(workdir, 0o777)  # the container runs as UID 1000, not as the runner user
    shutil.copyfile(ZAP_DIR / "context.context", workdir / "context.context")
    shutil.copyfile(ZAP_DIR / "hook.py", workdir / "hook.py")

    started = time.monotonic()
    if run_bounded(["docker", "pull", image], env=base_env(), timeout=600, cwd=ROOT) != 0:
        raise HarnessError("docker pull of the pinned ZAP image failed")
    log(f"step=zap-pull elapsed={time.monotonic() - started:.0f}s")

    zap_exit = {"code": -1}

    def body(_stack: Stack) -> int:
        began = time.monotonic()
        code = run_bounded(zap_run_argv(image, workdir), env=base_env(), timeout=ZAP_TIMEOUT, cwd=ROOT, needles=needles,
                           protect=True)
        zap_exit["code"] = code
        log(f"step=zap-baseline exit={code} elapsed={time.monotonic() - began:.0f}s")
        return 0  # the verdict is zap_policy.py's, which also rejects a bad exit code

    stack_code = run_with_stack(run_secrets, needles, body)
    if stack_code != 0:
        return stack_code
    if not scan_and_stage(workdir, runner_temp() / "upload" / "zap", needles, only=(ZAP_REPORT_JSON, ZAP_REPORT_HTML)):
        return 1
    env = base_env()
    summary = os.environ.get(_runner_key("GITHUB_", "STEP_SUMMARY"))
    if summary:
        env[_runner_key("GITHUB_", "STEP_SUMMARY")] = summary
    sys.stdout.flush()
    result = subprocess.run(
        [sys.executable, str(POLICY_SCRIPT), "--report", str(workdir / ZAP_REPORT_JSON),
         "--urls", str(workdir / ZAP_URLS_FILE), "--zap-exit", str(zap_exit["code"])],
        env=env, cwd=ROOT, timeout=300, stdin=subprocess.DEVNULL, check=False)
    return result.returncode


COMMANDS = {
    "install-aspire": cmd_install_aspire,
    "apphost-tests": cmd_apphost_tests,
    "e2e": cmd_e2e,
    "zap": cmd_zap,
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Full-stack CI harness (issue #123)")
    parser.add_argument("command", choices=sorted(COMMANDS))
    args = parser.parse_args(argv)
    try:
        sys.stdout.reconfigure(line_buffering=True)  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass
    try:
        return COMMANDS[args.command]()
    except HarnessError as exc:
        print(f"::error::fullstack: {neutralize(exc)}", flush=True)
        return 1
    except Exception as exc:  # fail closed; the class, the last frame and a short message
        # No secret can be in the message: the run's values live only in child environment dictionaries
        # and in RunSecrets, and are never put in an argument, a path or an exception.
        frame = traceback.extract_tb(exc.__traceback__)[-1] if exc.__traceback__ else None
        where = f" at {Path(frame.filename).name}:{frame.lineno}" if frame else ""
        print(f"::error::fullstack: unexpected {type(exc).__name__}{where}: {neutralize(str(exc))[:300]}", flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
