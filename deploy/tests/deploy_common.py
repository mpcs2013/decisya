"""Shared helpers for deploy/tests (stdlib only; issue #120, architecture note D10).

Run the whole suite from the repository root:

    python -m unittest discover -s deploy/tests -p "test_*.py" -v

Tests that need Docker skip cleanly when Docker is missing, except when DECISYA_DEPLOY_REQUIRE_DOCKER=1
(set by the `deploy-guards` CI job): then a missing Docker is a failure, so the job cannot go green
by skipping its main checks.
"""
from __future__ import annotations

import contextlib
import copy
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
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
    "DECISYA_LAN_SUBNET": "198.51.100.0/24",
    "DECISYA_WORKSTATION_ADDRESS": "192.0.2.20",
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
        code, output = quiet(stackctl.main, ["secrets", "init", "--stack", str(stack)])
        if code != 0:
            raise AssertionError("secrets init failed: " + output)
    return stack


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


class FakeRunner:
    """Stands in for stackctl.Runner where Docker must not be touched."""

    docker = "docker"

    def __init__(self, returncode: int = 0):
        self.returncode = returncode
        self.calls: list = []

    def compose(self, args, input_text=None, timeout=600):
        self.calls.append((list(args), input_text))
        return subprocess.CompletedProcess(args, self.returncode, "", "")


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
