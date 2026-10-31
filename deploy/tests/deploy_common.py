"""Shared helpers for deploy/tests (stdlib only; issue #120, architecture note D10).

Run the whole suite from the repository root:

    python -m unittest discover -s deploy/tests -p "test_*.py" -v

Tests that need Docker skip cleanly when Docker is missing, except when DECISYA_DEPLOY_REQUIRE_DOCKER=1
(set by the `deploy-guards` CI job): then a missing Docker is a failure, so the job cannot go green
by skipping its main checks.
"""
from __future__ import annotations

import base64
import contextlib
import copy
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
COMPOSE_DIR = ROOT / "deploy" / "compose"
GENERATED = COMPOSE_DIR / "docker-compose.yaml"
OVERLAY = COMPOSE_DIR / "docker-compose.stack.yaml"

if str(COMPOSE_DIR) not in sys.path:
    sys.path.insert(0, str(COMPOSE_DIR))

import stackctl  # noqa: E402
import stackguards as guards  # noqa: E402

REQUIRE_DOCKER = os.environ.get("DECISYA_DEPLOY_REQUIRE_DOCKER") == "1"

# Documentation ranges only (RFC 5737). Never a real address.
FIXTURE_VALUES = {
    "DECISYA_BIND_ADDRESS": "192.0.2.10",
    "DECISYA_HTTPS_PORT": "8443",
    "DECISYA_ADMIN_PORT": "8444",
    "DECISYA_APP_HOST": "app.p0.home.arpa",
    "DECISYA_ID_HOST": "id.p0.home.arpa",
    "DECISYA_API_HOST": "api.p0.home.arpa",
    "DECISYA_API_IMAGE": "ghcr.io/mpcs2013/decisya-api@sha256:" + "a" * 64,
    "DECISYA_BFF_IMAGE": "ghcr.io/mpcs2013/decisya-bff@sha256:" + "b" * 64,
    "DECISYA_MIGRATOR_IMAGE": "ghcr.io/mpcs2013/decisya-migrator@sha256:" + "c" * 64,
}
FIXTURE_ROOT_PEM = "-----BEGIN CERTIFICATE-----\nZml4dHVyZS1ub3QtYS1yZWFsLWNlcnRpZmljYXRl\n-----END CERTIFICATE-----\n"

_docker_state: dict = {}


def docker_available() -> bool:
    if "ok" not in _docker_state:
        ok = False
        if shutil.which("docker"):
            try:
                info = subprocess.run(["docker", "info"], capture_output=True, timeout=60)
                compose = subprocess.run(["docker", "compose", "version"], capture_output=True, timeout=60)
                ok = info.returncode == 0 and compose.returncode == 0
            except (OSError, subprocess.SubprocessError):
                ok = False
        _docker_state["ok"] = ok
    return _docker_state["ok"]


class DockerCase(unittest.TestCase):
    """Base class: skip without Docker locally, fail without Docker in CI."""

    @classmethod
    def setUpClass(cls):
        if not docker_available():
            if REQUIRE_DOCKER:
                raise RuntimeError("Docker is required here (DECISYA_DEPLOY_REQUIRE_DOCKER=1) but is not available")
            raise unittest.SkipTest("Docker is not available")
        super().setUpClass()


def force_rmtree(path) -> None:
    """Remove a folder that holds read-only (0444) secret files, also on Windows."""
    path = str(path)
    for folder, dirs, files in os.walk(path):
        for name in dirs + files:
            with contextlib.suppress(OSError):
                os.chmod(os.path.join(folder, name), 0o777)
    shutil.rmtree(path, ignore_errors=True)


def write_force(path, data: bytes) -> None:
    """Overwrite a file that may be read-only (a 0444 secret): Windows refuses a plain write."""
    path = Path(path)
    if path.exists():
        os.chmod(path, 0o666)
    path.write_bytes(data)


def unlink_force(path) -> None:
    path = Path(path)
    os.chmod(path, 0o666)
    path.unlink()


@contextlib.contextmanager
def scratch_dir():
    folder = Path(tempfile.mkdtemp(prefix="decisya-deploy-test-"))
    try:
        yield folder
    finally:
        force_rmtree(folder)


def quiet(func, *args, **kwargs):
    """Call func with stdout and stderr captured. Returns (result, output)."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        result = func(*args, **kwargs)
    return result, out.getvalue() + err.getvalue()


def env_text(values: dict) -> str:
    return "".join("%s=%s\n" % (key, values[key]) for key in guards.ENV_KEYS if key in values)


def images_txt(values: dict) -> str:
    return "".join(values[key] + "\n" for key in guards.IMAGE_KEYS)


def make_stack(parent: Path, values: dict | None = None, *, secrets_init: bool = True) -> Path:
    """An assembled stack folder in the fixture's documentation-range values."""
    values = FIXTURE_VALUES if values is None else values
    stack = Path(parent) / "stack"
    code, output = quiet(stackctl.main, ["assemble", "--stack", str(stack), "--published", str(GENERATED), "--repo", str(ROOT)])
    if code != 0:
        raise AssertionError("assemble failed: " + output)
    env_path = stack / stackctl.ENV_NAME
    env_path.write_text(env_text(values), encoding="utf-8")
    (stack / stackctl.IMAGES_NAME).write_text(images_txt(values), encoding="utf-8")
    (stack / "trust" / "caddy-root.crt").write_text(FIXTURE_ROOT_PEM, encoding="ascii")
    if secrets_init:
        # The certificate generator runs in the BFF image: a fake runner stands in for Docker here.
        code, output = quiet(stackctl.main, ["secrets", "init", "--stack", str(stack)], runner_factory=lambda s: FakeRunner())
        if code != 0:
            raise AssertionError("secrets init failed: " + output)
    return stack


def vendor_entry(id: str = "CVE-2026-99001", package: str = "stdlib", image: str = "caddy", *, added: str = "2026-10-17",
                 expires: str = "2026-10-31", issue: str = "#142", approved: str = "2026-10-17", justification: str | None = None) -> dict:
    """One exceptions.json entry. With the defaults it is a valid ADR-0015 item-4 entry (14 days, approved)."""
    if justification is None:
        justification = "Fixture: a Go finding with no vendor fix; reachable once deployed. Exception approved by Marco %s under %s." % (approved, issue)
    return {"id": id, "image": image, "package": package, "justification": justification, "issue": issue, "added": added, "expires": expires}


def make_scan_repo(parent: Path, entries=(), *, exceptions_text: str | None = None, scan_module: bool = True) -> Path:
    """A fixture clone for the deploy guard: the real image_scan.py and ContainerImages.cs, and an
    exceptions.json that holds exactly `entries` (or `exceptions_text` as written). Never the live file."""
    repo = Path(parent) / "scan-repo"
    (repo / ".github" / "scripts").mkdir(parents=True)
    (repo / ".github" / "image-scan").mkdir(parents=True)
    (repo / "src" / "Decisya.AppHost").mkdir(parents=True)
    if scan_module:
        shutil.copyfile(ROOT / ".github" / "scripts" / "image_scan.py", repo / ".github" / "scripts" / "image_scan.py")
    shutil.copyfile(ROOT / "src" / "Decisya.AppHost" / "ContainerImages.cs", repo / "src" / "Decisya.AppHost" / "ContainerImages.cs")
    # #222: the scan result is bound to the scanner pin, so the fixture clone carries the real one.
    shutil.copyfile(ROOT / ".github" / "image-scan" / "Dockerfile", repo / ".github" / "image-scan" / "Dockerfile")
    if exceptions_text is None:
        exceptions_text = json.dumps({"exceptions": list(entries)}, indent=2)
    if exceptions_text:
        (repo / ".github" / "image-scan" / "exceptions.json").write_text(exceptions_text, encoding="utf-8")
    return repo


RESULT_TIME_FORMAT = "%Y-%m-%dT%H:%M:%SZ"


def write_fresh_result(stack: Path, repo: Path, *, now: datetime | None = None, age: timedelta = timedelta(minutes=1),
                       mutate=None) -> Path:
    """Write a green `stackctl scan` result for this stack and clone, built from the real scan_bindings
    (never a mock of the freshness gate). `age` is how long before `now` the scan ran; the database was
    built one hour before the scan. `mutate(record)` edits the record before it is written. Returns the path."""
    now = now or datetime.now(timezone.utc)
    bindings = stackctl.scan_bindings(Path(stack), Path(repo))
    scanned_at = now - age
    record = {
        "schema": guards.SCAN_RESULT_SCHEMA,
        "scanned_at": scanned_at.strftime(RESULT_TIME_FORMAT),
        "scanner": bindings["scanner"],
        "db_built": (scanned_at - timedelta(hours=1)).strftime(RESULT_TIME_FORMAT),
        "exceptions_sha256": bindings["exceptions_sha256"],
        "image_scan_sha256": bindings["image_scan_sha256"],
        "refs": bindings["refs"],
        "untrusted": [],
        "findings": [],
        "excepted": [],
        "verdict": "green",
    }
    if mutate is not None:
        mutate(record)
    path = stackctl.scan_result_path(Path(stack))
    path.write_text(json.dumps(record, indent=2), encoding="utf-8")
    if os.name == "posix":
        os.chmod(path, 0o600)
    return path


# --------------------------------------------------------------------------- a fake scanner (Grype)

GRYPE_LAYER = "sha256:" + "1" * 64
_TAGGED_REF = re.compile(r"([a-z0-9.-]+)/([a-z0-9._/-]+):([A-Za-z0-9._-]+)@sha256:[0-9a-f]{64}")


def grype_match(name: str = "libexample", type_: str = "apk", id: str = "CVE-2026-00001", severity: str = "High",
                version: str = "1.0.0", fix_state: str = "not-fixed", fix_versions=()) -> dict:
    """One match of Grype's JSON."""
    return {
        "vulnerability": {"id": id, "severity": severity, "fix": {"state": fix_state, "versions": list(fix_versions)}},
        "relatedVulnerabilities": [],
        "artifact": {"name": name, "version": version, "type": type_},
    }


def grype_doc(ref: str, matches=(), built: str = "2026-10-30T01:00:00Z") -> dict:
    """The JSON Grype prints for a trusted scan of `ref`."""
    return {
        "matches": list(matches),
        "source": {"type": "image", "target": {"userInput": ref, "layers": [{"digest": GRYPE_LAYER}]}},
        "distro": {"name": "debian"},
        "descriptor": {"db": {"built": built}},
    }


def grype_sbom(ref: str) -> dict | None:
    """The CycloneDX file the binary-only image needs (a Go-module component in a listed layer)."""
    found = _TAGGED_REF.fullmatch(ref)
    if not found:
        return None
    return {
        "metadata": {"component": {"type": "container", "name": "%s/%s" % (found.group(1), found.group(2)), "version": found.group(3)}},
        "components": [{
            "purl": "pkg:golang/example.test/module@v1.0.0",
            "properties": [{"name": "syft:package:type", "value": "go-module"}, {"name": "syft:location:0:layerID", "value": GRYPE_LAYER}],
        }],
    }


@contextlib.contextmanager
def offline_scan_module():
    """`stackctl scan` loads the clone's image_scan.py; its registry lookup would reach the network. Every
    tag resolves as unchanged here, and the module's clock is fixed to the day the fixtures are written for."""
    real = guards.load_scan_module

    def loader(repo):
        module = real(repo)
        module.resolve_tag_state = lambda *args, **kwargs: "unchanged"
        module._utc_today = lambda: date(2026, 10, 30)
        return module

    with mock.patch.object(guards, "load_scan_module", loader):
        yield


def clean_environment():
    """No COMPOSE_* variable steers a test (the check refuses them on purpose)."""
    cleaned = {k: v for k, v in os.environ.items() if not k.startswith("COMPOSE_")}
    return mock.patch.dict(os.environ, cleaned, clear=True)


def compose_json(files, *, env_file=None, project_dir=None, interpolate: bool) -> dict:
    """`docker compose config --format json` of the given files, with no path resolution."""
    argv = ["docker", "compose"]
    if project_dir is not None:
        argv += ["--project-directory", str(project_dir)]
    if env_file is not None:
        argv += ["--env-file", str(env_file)]
    for file in files:
        argv += ["-f", str(file)]
    argv += ["config", "--no-path-resolution", "--format", "json"]
    if not interpolate:
        argv.append("--no-interpolate")
    environment = {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "DECISYA_"))}
    proc = subprocess.run(argv, capture_output=True, text=True, encoding="utf-8", errors="replace", env=environment, timeout=300)
    if proc.returncode != 0:
        raise AssertionError("docker compose config failed: " + (proc.stderr or "")[-600:])
    return json.loads(proc.stdout)


def deep(obj):
    return copy.deepcopy(obj)


def as_list(value):
    return value if isinstance(value, list) else ([] if value is None else [value])


def networks_dict(service: dict) -> dict:
    """The service's networks as a dict, whatever form Compose printed."""
    nets = service.get("networks")
    if isinstance(nets, list):
        nets = {n: None for n in nets}
    service["networks"] = nets if nets is not None else {}
    return service["networks"]


def clean_keyring_report() -> str:
    """What the read-only key-ring check prints for a volume with nothing wrong."""
    return "".join("%s=0\n" % key for key in guards.KEYRING_REPORT_KEYS)


class FakeRunner:
    """Stands in for stackctl.Runner where Docker must not be touched.

    `compose` answers with `returncode`. `run` (every other Docker call) answers 0, and a clean key-ring
    report for a one-shot container (an argv with --mount). `run_bytes` is the certificate generator: a
    different, well-formed base64 value on each call. Nothing is executed; every call is recorded."""

    docker = "docker"

    def __init__(self, returncode: int = 0):
        self.returncode = returncode
        self.calls: list = []
        self.runs: list = []
        self.generated: list = []
        self._count = 0

    def compose(self, args, input_text=None, timeout=600):
        self.calls.append((list(args), input_text))
        return subprocess.CompletedProcess(args, self.returncode, "", "")

    def run(self, argv, input_text=None, timeout=600):
        self.runs.append(list(argv))
        return subprocess.CompletedProcess(argv, 0, clean_keyring_report() if "--mount" in argv else "", "")

    def run_bytes(self, argv, input_bytes, timeout=600):
        self.generated.append((list(argv), input_bytes))
        self._count += 1
        # Random, so two runners never produce the same "certificate" (a rotation must change the value).
        fake = base64.b64encode(os.urandom(2200))
        return subprocess.CompletedProcess(argv, 0, fake, b"")


def completed(stdout: str = "", returncode: int = 0):
    return subprocess.CompletedProcess([], returncode, stdout, "")


class ScriptedRunner(FakeRunner):
    """A FakeRunner whose answers come from `answer(args, input_text)`: a CompletedProcess, or an
    exception to raise (a timeout, for example). It records every call, SQL on stdin included, so a
    test can assert what was sent and in which order. `inspect_project` and `docker_subnets` answer
    "nothing running, no other network", which is all `verify` needs besides the identity check."""

    def __init__(self, answer):
        super().__init__()
        self.answer = answer

    def compose(self, args, input_text=None, timeout=600):
        self.calls.append((list(args), input_text))
        result = self.answer(list(args), input_text)
        if isinstance(result, BaseException):
            raise result
        return result

    def inspect_project(self):
        return []

    def docker_subnets(self):
        return []


class ScanRunner(FakeRunner):
    """Stands in for the Runner of `stackctl scan`: answers `docker run` for the scanner like Grype.
    `matches` maps a reference to its matches; `fail_db` makes the database update fail; `fail_refs`
    makes those scans exit 1; `built_for` maps a reference to another database build time. Every argv is
    recorded in `runs`; a `compose` call is recorded in `calls` (a scan makes none)."""

    def __init__(self, matches: dict | None = None, *, fail_db: bool = False, fail_refs=(), built_for: dict | None = None):
        super().__init__()
        self.matches = matches or {}
        self.fail_db = fail_db
        self.fail_refs = set(fail_refs)
        self.built_for = built_for or {}

    def run(self, argv, input_text=None, timeout=600):
        self.runs.append(list(argv))
        if argv[-2:] == ["db", "update"]:
            return subprocess.CompletedProcess(argv, 1 if self.fail_db else 0, "", "")
        ref = next(a[len("registry:"):] for a in argv if a.startswith("registry:"))
        if ref in self.fail_refs:
            return subprocess.CompletedProcess(argv, 1, "", "")
        out = next((argv[i + 1][:-len(":/out")] for i, a in enumerate(argv[:-1]) if a == "-v" and argv[i + 1].endswith(":/out")), None)
        sbom = grype_sbom(ref)
        if out is not None and sbom is not None:
            Path(out, "sbom.cdx.json").write_text(json.dumps(sbom), encoding="utf-8")
        doc = grype_doc(ref, self.matches.get(ref, ()), self.built_for.get(ref, "2026-10-30T01:00:00Z"))
        return subprocess.CompletedProcess(argv, 0, json.dumps(doc), "")
