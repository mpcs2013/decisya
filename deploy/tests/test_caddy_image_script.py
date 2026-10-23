"""Offline fixture tests for .github/scripts/caddy_image.py and the Dependabot entries (issue #188, G3 M-1 to M-3).

The script is imported by path and its pure functions run on hand-built fixture data: the image config, merged
layer views and real in-memory tar layers, SBOM files, build lists, a fake grype run. Nothing here starts Docker,
touches the network or reads a secret. What needs the built image (`caddy list-modules`, `go version -m`,
the per-layer gitleaks run) is checked by the `inspect` step in the build job and by the Docker-backed tests of
PR-2; it is not covered here.

Rules (one red case each, written red first):
  executables   regular files with an exec bit are exactly the two binaries and the four BASE_EXEC_FILES
  setuid        no setuid or setgid bit
  capability    no file capability
  applets       exactly the 15 applet links, nothing else in a PATH directory
  env           Env is PATH, XDG_CONFIG_HOME, XDG_DATA_HOME and SSL_CERT_FILE with its exact value
  config        platform, user, entrypoint, volumes, CMD, workdir, labels
  grype         strict D-2: grype output goes to evaluate(doc, name, [], today) with no ref and no tag state
  sbom          the SBOM names Caddy, the Go stdlib and BusyBox at the pinned version
  bindings      version bindings: labels, tag and commit, go and toolchain lines against the builder
  stale         an override that upstream has caught up with fails; an unexplained difference fails
  inputs        `caddy_image.py inputs` refuses what test_caddy_image_inputs.py refuses
  dependabot    docker and gomod entries for deploy/caddy/image; github-actions, nuget and npm stay
"""
from __future__ import annotations

import copy
import importlib.util
import io
import json
import re
import shutil
import subprocess
import tarfile
import unittest
from datetime import date
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from deploy_common import ROOT, quiet, scratch_dir

SCRIPTS = ROOT / ".github" / "scripts"
IMAGE_DIR = ROOT / "deploy" / "caddy" / "image"
DEPENDABOT = ROOT / ".github" / "dependabot.yml"


def load_script():
    spec = importlib.util.spec_from_file_location("caddy_image_under_test", SCRIPTS / "caddy_image.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ci = load_script()
ri = ci.ri

SHA = "a" * 40
CTX = {"sha": SHA, "caddy_version": "2.11.7"}
PINS = {
    "base_name": "gcr.io/distroless/static-debian13:latest",
    "base_digest": "sha256:" + "b" * 64,
    "busybox_version": "1.38.0",
}
CONTRACT_APPLETS = ("[", "cat", "chmod", "chown", "echo", "find", "grep", "nc", "printf", "rm", "sh", "test", "tr", "wc", "wget")
BASE_FILES = (
    "etc/ssl/certs/ca-certificates.crt",
    "etc/update-motd.d/10-uname",
    "usr/lib/os-release",
    "usr/share/doc/ca-certificates/copyright",
)


# --------------------------------------------------------------------------- the constants

class ContractConstantTests(unittest.TestCase):
    def test_the_executable_allow_list_is_the_four_distroless_files(self):
        self.assertEqual(tuple(ci.BASE_EXEC_FILES), BASE_FILES)

    def test_the_applet_set_is_the_image_contract(self):
        self.assertEqual(tuple(ci.APPLETS), CONTRACT_APPLETS)
        self.assertEqual(len(ci.APPLETS), 15)

    def test_the_env_allow_list_is_exact(self):
        self.assertEqual(tuple(ci.ENV_ALLOW), ("PATH", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "SSL_CERT_FILE"))
        self.assertEqual(ci.BASE_ENV, {
            "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            "SSL_CERT_FILE": "/etc/ssl/certs/ca-certificates.crt",
        })
        self.assertEqual(ci.ROOT_LINKS, {"bin": "usr/bin", "sbin": "usr/sbin", "lib": "usr/lib", "lib64": "usr/lib64"})
        self.assertEqual(ci.RUNTIME_ENV, {"XDG_CONFIG_HOME": "/config", "XDG_DATA_HOME": "/data"})

    def test_the_default_command_is_the_stacks(self):
        self.assertEqual(ci.RUNTIME_CMD, ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"])

    def test_the_identity_is_exact_and_is_not_the_release_identity(self):
        self.assertEqual(ci.IDENTITY, "https://github.com/mpcs2013/decisya/.github/workflows/caddy-image.yml@refs/heads/main")
        self.assertNotEqual(ci.IDENTITY, ri.IDENTITY)
        self.assertEqual(ci.REMOTE, "ghcr.io/mpcs2013/decisya-caddy")


# --------------------------------------------------------------------------- executables, setuid, capabilities, applets

def entry(kind: str = "file", mode: int = 0o644, link: str = "", pax: dict | None = None) -> dict:
    return {"kind": kind, "mode": mode, "link": link, "pax": pax or {}}


def good_view() -> dict:
    view = {"usr": entry("dir", 0o755), "usr/bin": entry("dir", 0o755), "etc": entry("dir", 0o755)}
    view["usr/bin/caddy"] = entry("file", 0o755)
    view["usr/bin/busybox"] = entry("file", 0o755)
    for path in BASE_FILES:
        view[path] = entry("file", 0o755)
    view["etc/passwd"] = entry("file", 0o644)
    for applet in CONTRACT_APPLETS:
        view["usr/bin/" + applet] = entry("sym", 0o777, "busybox")
    return view


class FileCheckTests(unittest.TestCase):
    def problems(self, mutator=None) -> list:
        view = good_view()
        if mutator:
            mutator(view)
        return ci.check_files(view)

    def assert_problem(self, mutator, containing: str):
        problems = self.problems(mutator)
        self.assertTrue(any(containing in p for p in problems), "no problem mentions %r: %s" % (containing, problems))

    def test_the_good_view_has_no_problem(self):
        self.assertEqual(self.problems(), [])

    def test_red_an_extra_executable_is_refused(self):
        self.assert_problem(lambda v: v.update({"srv/run.sh": entry("file", 0o755)}), "executable regular files")

    def test_red_an_extra_executable_in_a_path_directory_is_refused(self):
        view_change = lambda v: v.update({"usr/local/bin/curl": entry("file", 0o755)})
        self.assert_problem(view_change, "executable regular files")
        self.assert_problem(view_change, "PATH directory")

    def test_red_a_missing_base_file_is_refused(self):
        for path in BASE_FILES:
            with self.subTest(path=path):
                self.assert_problem(lambda v: v.pop(path), "executable regular files")

    def test_red_a_base_file_that_lost_its_exec_bit_is_refused(self):
        self.assert_problem(lambda v: v.update({BASE_FILES[0]: entry("file", 0o644)}), "executable regular files")

    def test_red_a_missing_binary_is_refused(self):
        self.assert_problem(lambda v: v.pop("usr/bin/caddy"), "executable regular files")
        self.assert_problem(lambda v: v.pop("usr/bin/busybox"), "executable regular files")

    def test_red_setuid_and_setgid_are_refused(self):
        self.assert_problem(lambda v: v.update({"usr/bin/caddy": entry("file", 0o4755)}), "setuid or setgid")
        self.assert_problem(lambda v: v.update({"usr/bin/busybox": entry("file", 0o2755)}), "setuid or setgid")

    def test_red_a_file_capability_is_refused(self):
        pax = {"SCHILY.xattr.security.capability": "AQAAAgAgAAAAAA=="}
        self.assert_problem(lambda v: v.update({"usr/bin/caddy": entry("file", 0o755, pax=pax)}), "file capability")

    def test_red_a_missing_applet_link_is_refused(self):
        self.assert_problem(lambda v: v.pop("usr/bin/nc"), "applet links are")

    def test_red_an_extra_applet_link_is_refused(self):
        self.assert_problem(lambda v: v.update({"usr/bin/tar": entry("sym", 0o777, "busybox")}), "applet links are")

    def test_red_a_link_to_something_else_is_refused(self):
        self.assert_problem(lambda v: v.update({"usr/bin/nc": entry("sym", 0o777, "/tmp/evil")}), "neither an applet link")

    def test_red_a_regular_file_in_place_of_an_applet_is_refused(self):
        self.assert_problem(lambda v: v.update({"usr/bin/nc": entry("file", 0o755)}), "neither an applet link")


# --------------------------------------------------------------------------- the same checks on real tar layers

def tar_layer(entries: list) -> bytes:
    """entries: (name, kind, mode, link, pax). Written as a PAX tar, as image layers are."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for name, kind, mode, link, pax in entries:
            info = tarfile.TarInfo(name)
            info.mode = mode
            data = None
            if kind == "dir":
                info.type = tarfile.DIRTYPE
            elif kind == "sym":
                info.type = tarfile.SYMTYPE
                info.linkname = link
            else:
                info.type = tarfile.REGTYPE
                info.size = 1
                data = io.BytesIO(b"x")
            if pax:
                info.pax_headers = dict(pax)
            tar.addfile(info, data)
    return buf.getvalue()


def F(name: str, mode: int = 0o644, pax: dict | None = None) -> tuple:
    return (name, "file", mode, "", pax)


def D(name: str) -> tuple:
    return (name, "dir", 0o755, "", None)


def L(name: str, link: str) -> tuple:
    return (name, "sym", 0o777, link, None)


def image_layers(*extra: list) -> list:
    base = [D("usr"), D("usr/bin"), D("etc"), L("bin", "usr/bin"), F("etc/passwd")] + [F(p, 0o755) for p in BASE_FILES]
    caddy = [F("usr/bin/caddy", 0o755)]
    busybox = [F("bin/busybox", 0o755)] + [L("bin/" + a, "busybox") for a in CONTRACT_APPLETS]
    layers = [base, caddy, busybox, *extra]
    return [("layer%d" % i, tar_layer(entries)) for i, entries in enumerate(layers)]


class LayerViewTests(unittest.TestCase):
    def problems(self, *extra: list) -> list:
        return ci.check_files(ci.merged_view(image_layers(*extra)))

    def test_the_good_layers_have_no_problem(self):
        self.assertEqual(self.problems(), [])

    def test_the_merged_usr_names_resolve_to_one_path(self):
        view = ci.merged_view(image_layers())
        self.assertIn("usr/bin/busybox", view)
        self.assertIn("usr/bin/[", view)
        self.assertEqual(view["usr/bin/nc"]["kind"], "sym")

    def test_red_a_whiteout_of_a_base_file_is_seen(self):
        problems = self.problems([F("etc/update-motd.d/.wh.10-uname")])
        self.assertTrue(any("executable regular files" in p for p in problems), problems)

    def test_red_an_executable_added_by_a_later_layer_is_seen(self):
        problems = self.problems([F("usr/local/bin/evil", 0o755)])
        self.assertTrue(any("executable regular files" in p for p in problems), problems)
        self.assertTrue(any("PATH directory" in p for p in problems), problems)

    def test_red_a_setuid_binary_in_a_layer_is_seen(self):
        problems = self.problems([F("usr/bin/caddy", 0o4755)])
        self.assertTrue(any("setuid or setgid" in p for p in problems), problems)

    def test_red_a_file_capability_in_a_layer_is_seen(self):
        problems = self.problems([F("usr/bin/caddy", 0o755, {"SCHILY.xattr.security.capability": "AQAAAgAgAAAAAA=="})])
        self.assertTrue(any("file capability" in p for p in problems), problems)

    def test_red_a_later_layer_replacing_an_applet_with_a_file_is_seen(self):
        problems = self.problems([F("bin/nc", 0o755)])
        self.assertTrue(any("neither an applet link" in p for p in problems), problems)

    def test_red_a_removed_applet_link_is_seen(self):
        problems = self.problems([F("bin/.wh.wget")])
        self.assertTrue(any("applet links are" in p for p in problems), problems)

    # G6 F-1: PATH is tied to the image. The real-shaped base has bin -> usr/bin, sbin -> usr/sbin, lib -> usr/lib.
    def real_shaped_layers(self, *extra: list) -> list:
        root = [L("sbin", "usr/sbin"), L("lib", "usr/lib"), L("lib64", "usr/lib64"), D("usr/sbin")]
        layers = image_layers(root, *extra)
        return layers

    def test_the_real_shaped_view_passes(self):
        view = ci.merged_view(self.real_shaped_layers())
        self.assertEqual(ci.check_files(view), [])
        self.assertEqual(view["@root/bin"]["link"], "usr/bin")

    def test_red_bin_re_pointed_is_refused(self):
        problems = self.problems([L("bin", "usr/local/bin")])
        self.assertTrue(any("/bin must be a symlink to usr/bin" in p for p in problems), problems)

    def test_red_sbin_re_pointed_is_refused(self):
        problems = ci.check_files(ci.merged_view(self.real_shaped_layers([L("sbin", "tmp")])))
        self.assertTrue(any("/sbin must be a symlink to usr/sbin" in p for p in problems), problems)

    def test_red_bin_as_a_real_directory_is_refused(self):
        problems = self.problems([D("bin")])
        self.assertTrue(any("/bin must be a symlink to usr/bin" in p for p in problems), problems)

    def test_red_a_path_directory_that_is_a_symlink_is_refused(self):
        problems = self.problems([L("usr/local/bin", "/tmp")])
        self.assertTrue(any("PATH directory must be a real directory" in p for p in problems), problems)
        problems = self.problems([L("usr/bin", "/tmp")])
        self.assertTrue(any("usr/bin: a PATH directory must be a real directory" in p for p in problems), problems)


class PathEnvTests(unittest.TestCase):
    def problems(self, path: str) -> list:
        cfg = copy.deepcopy(good_config())
        cfg["config"]["Env"] = [e for e in cfg["config"]["Env"] if not e.startswith("PATH=")] + ["PATH=" + path]
        return ci.check_config(cfg, CTX, PINS)

    def test_the_exact_path_passes(self):
        self.assertEqual(self.problems(ci.BASE_ENV["PATH"]), [])

    def test_red_a_changed_or_reordered_path_is_refused(self):
        for path in (
            "/usr/local/bin:/usr/local/sbin:/usr/sbin:/usr/bin:/sbin:/bin",
            "/tmp:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin",
            "/usr/bin",
        ):
            with self.subTest(path=path):
                self.assertTrue(any("Env PATH" in p for p in self.problems(path)))


# --------------------------------------------------------------------------- the image config

def good_config() -> dict:
    return {
        "os": "linux",
        "architecture": "amd64",
        "config": {
            "User": "",
            "Env": [
                "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "XDG_CONFIG_HOME=/config",
                "XDG_DATA_HOME=/data",
                "SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt",
            ],
            "Entrypoint": None,
            "Cmd": list(ci.RUNTIME_CMD),
            "WorkingDir": "/srv",
            "Volumes": None,
            "Labels": {
                "org.opencontainers.image.source": ci.SOURCE_URL,
                "org.opencontainers.image.revision": SHA,
                "org.opencontainers.image.version": "2.11.7",
                "org.opencontainers.image.base.name": PINS["base_name"],
                "org.opencontainers.image.base.digest": PINS["base_digest"],
            },
        },
    }


class ConfigCheckTests(unittest.TestCase):
    def problems(self, mutator=None) -> list:
        cfg = copy.deepcopy(good_config())
        if mutator:
            mutator(cfg)
        return ci.check_config(cfg, CTX, PINS)

    def assert_problem(self, mutator, containing: str):
        problems = self.problems(mutator)
        self.assertTrue(any(containing in p for p in problems), "no problem mentions %r: %s" % (containing, problems))

    def replace_env(self, cfg: dict, key: str, item: str | None):
        cfg["config"]["Env"] = [e for e in cfg["config"]["Env"] if not e.startswith(key + "=")]
        if item is not None:
            cfg["config"]["Env"].append(item)

    def test_the_good_config_has_no_problem(self):
        self.assertEqual(self.problems(), [])

    def test_red_another_ssl_cert_file_value_is_refused(self):
        self.assert_problem(lambda c: self.replace_env(c, "SSL_CERT_FILE", "SSL_CERT_FILE=/tmp/evil.pem"), "Env SSL_CERT_FILE")

    def test_red_a_missing_ssl_cert_file_is_refused(self):
        self.assert_problem(lambda c: self.replace_env(c, "SSL_CERT_FILE", None), "Env SSL_CERT_FILE")

    def test_red_an_extra_env_key_is_refused(self):
        for item in ("GOFLAGS=-mod=mod", "SSL_CERT_DIR=/etc/ssl/certs", "CADDY_ADMIN=:2019"):
            with self.subTest(item=item):
                self.assert_problem(lambda c: c["config"]["Env"].append(item), "outside the allow-list")

    def test_red_a_changed_xdg_value_is_refused(self):
        self.assert_problem(lambda c: self.replace_env(c, "XDG_DATA_HOME", "XDG_DATA_HOME=/var/lib"), "Env XDG_DATA_HOME")
        self.assert_problem(lambda c: self.replace_env(c, "XDG_CONFIG_HOME", None), "Env XDG_CONFIG_HOME")

    def test_red_a_non_root_user_is_refused(self):
        self.assert_problem(lambda c: c["config"].update({"User": "65532"}), "User is")

    def test_red_an_entrypoint_is_refused(self):
        self.assert_problem(lambda c: c["config"].update({"Entrypoint": ["caddy"]}), "Entrypoint")

    def test_red_a_declared_volume_is_refused(self):
        self.assert_problem(lambda c: c["config"].update({"Volumes": {"/data": {}}}), "VOLUME")

    def test_red_another_command_or_workdir_is_refused(self):
        self.assert_problem(lambda c: c["config"].update({"Cmd": ["caddy", "run"]}), "Cmd is")
        self.assert_problem(lambda c: c["config"].update({"WorkingDir": "/"}), "WorkingDir")

    def test_red_another_platform_is_refused(self):
        self.assert_problem(lambda c: c.update({"architecture": "arm64"}), "platform is")

    def test_red_a_version_binding_label_mismatch_is_refused(self):
        key = "org.opencontainers.image.version"
        self.assert_problem(lambda c: c["config"]["Labels"].update({key: "2.11.6"}), "label " + key)
        key = "org.opencontainers.image.revision"
        self.assert_problem(lambda c: c["config"]["Labels"].update({key: "c" * 40}), "label " + key)
        key = "org.opencontainers.image.base.digest"
        self.assert_problem(lambda c: c["config"]["Labels"].update({key: "sha256:" + "c" * 64}), "label " + key)
        key = "org.opencontainers.image.source"
        self.assert_problem(lambda c: c["config"]["Labels"].pop(key), "label " + key)


# --------------------------------------------------------------------------- strict D-2: the grype call shape

GRYPE_DOC = {
    "matches": [],
    "source": {"type": "image", "target": {"layers": ["sha256:" + "d" * 64]}},
    "distro": {"name": "debian"},
}


class RecordingEvaluate:
    def __init__(self, findings=()):
        self.calls = []
        self.findings = list(findings)

    def __call__(self, *args, **kwargs):
        self.calls.append((args, kwargs))
        return {"findings": self.findings, "excepted": []}


class StrictGrypeTests(unittest.TestCase):
    TODAY = date(2026, 10, 23)

    def run_grype(self, doc, findings=(), db_built=True, returncode=0):
        evaluate = RecordingEvaluate(findings)
        ims = SimpleNamespace(DB_MOUNT="/db", SCAN_ENV={"GRYPE_DB_AUTO_UPDATE": "false"}, _db_built=lambda d: db_built, evaluate=evaluate)
        seen = []

        def fake_run(argv, timeout=1800):
            seen.append(list(argv))
            return subprocess.CompletedProcess(argv, returncode, json.dumps(doc), "")

        with mock.patch.object(ri, "run", fake_run):
            problems = ri._grype("scanner@sha256:" + "e" * 64, Path("db"), Path("out"), "caddy", ims, [], self.TODAY)
        del ri.LOG[:]
        return problems, evaluate, seen

    def test_evaluate_gets_the_document_the_alias_no_exceptions_and_today_only(self):
        problems, evaluate, seen = self.run_grype(GRYPE_DOC)
        self.assertEqual(problems, [])
        self.assertEqual(len(evaluate.calls), 1)
        args, kwargs = evaluate.calls[0]
        self.assertEqual(args, (GRYPE_DOC, "caddy", [], self.TODAY))
        self.assertEqual(kwargs, {}, "strict D-2 passes no ref and no tag_state")
        self.assertEqual(seen[0][-3:], ["docker-archive:/in/caddy.tar", "-o", "json"])

    def test_the_inspect_step_passes_no_exception_list(self):
        source = (SCRIPTS / "caddy_image.py").read_text(encoding="utf-8")
        self.assertIn('problems += ri._grype(scanner, db_dir, out, "caddy", ims, [], today)', source)
        for needle in ("tag_state", "EXCEPTIONS_FILE", "exceptions.json", "resolve_tag"):
            self.assertNotIn(needle, source)

    def test_the_real_evaluate_takes_no_ref_and_no_tag_state_by_default(self):
        spec = importlib.util.spec_from_file_location("image_scan_under_test", SCRIPTS / "image_scan.py")
        scan = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(scan)
        import inspect
        params = inspect.signature(scan.evaluate).parameters
        self.assertIsNone(params["tag_state"].default)
        self.assertIsNone(params["ref"].default)

    def test_red_a_high_finding_fails_the_check(self):
        finding = {"package": "stdlib", "version": "1.26.9", "id": "CVE-2026-00001", "severity": "High", "fix": "1.26.10"}
        problems, _, _ = self.run_grype(GRYPE_DOC, findings=[finding])
        self.assertEqual(len(problems), 1)
        self.assertIn("High or Critical", problems[0])

    def test_red_a_scan_that_catalogued_no_layers_fails(self):
        doc = copy.deepcopy(GRYPE_DOC)
        doc["source"]["target"]["layers"] = []
        problems, evaluate, _ = self.run_grype(doc)
        self.assertTrue(problems)
        self.assertEqual(evaluate.calls, [])

    def test_red_a_scan_with_no_recognised_os_fails(self):
        doc = copy.deepcopy(GRYPE_DOC)
        doc["distro"] = {}
        problems, evaluate, _ = self.run_grype(doc)
        self.assertTrue(problems)
        self.assertEqual(evaluate.calls, [])

    def test_red_an_unidentified_database_fails(self):
        problems, evaluate, _ = self.run_grype(GRYPE_DOC, db_built=False)
        self.assertTrue(problems)
        self.assertEqual(evaluate.calls, [])

    def test_red_a_failed_grype_run_fails(self):
        problems, evaluate, _ = self.run_grype(GRYPE_DOC, returncode=2)
        self.assertTrue(problems)
        self.assertEqual(evaluate.calls, [])


# --------------------------------------------------------------------------- SBOM evidence

GOOD_COMPONENTS = [
    {"purl": "pkg:golang/github.com/caddyserver/caddy/v2@v2.11.7", "name": "github.com/caddyserver/caddy/v2"},
    {"purl": "pkg:golang/stdlib@1.26.10", "name": "stdlib", "version": "1.26.10"},
    {"name": "busybox", "version": "1.38.0", "purl": "pkg:generic/busybox@1.38.0"},
]


class SbomEvidenceTests(unittest.TestCase):
    def problems(self, components=None, raw: str | None = None) -> list:
        with scratch_dir() as folder:
            path = folder / "sbom.cdx.json"
            if raw is None:
                raw = json.dumps({"bomFormat": "CycloneDX", "components": GOOD_COMPONENTS if components is None else components})
            path.write_text(raw, encoding="utf-8")
            return ci.check_sbom(path, CTX, PINS)

    def test_the_good_sbom_has_no_problem(self):
        self.assertEqual(self.problems(), [])

    def test_red_an_sbom_without_the_caddy_component_is_refused(self):
        problems = self.problems(GOOD_COMPONENTS[1:])
        self.assertTrue(any("Caddy module component" in p for p in problems), problems)

    def test_red_an_sbom_with_another_caddy_version_is_refused(self):
        other = [dict(GOOD_COMPONENTS[0], purl="pkg:golang/github.com/caddyserver/caddy/v2@v2.11.6")] + GOOD_COMPONENTS[1:]
        problems = self.problems(other)
        self.assertTrue(any("Caddy module component" in p for p in problems), problems)

    def test_red_an_sbom_without_the_stdlib_component_is_refused(self):
        problems = self.problems([GOOD_COMPONENTS[0], GOOD_COMPONENTS[2]])
        self.assertTrue(any("stdlib" in p for p in problems), problems)

    def test_red_an_sbom_without_busybox_is_refused(self):
        problems = self.problems(GOOD_COMPONENTS[:2])
        self.assertTrue(any("BusyBox" in p for p in problems), problems)

    def test_red_an_sbom_with_another_busybox_version_is_refused(self):
        other = GOOD_COMPONENTS[:2] + [dict(GOOD_COMPONENTS[2], version="1.37.0")]
        problems = self.problems(other)
        self.assertTrue(any("BusyBox" in p for p in problems), problems)

    def test_red_an_empty_sbom_names_all_three(self):
        self.assertEqual(len(self.problems([])), 3)

    def test_red_an_unreadable_sbom_is_refused(self):
        problems = self.problems(raw="not json")
        self.assertEqual(len(problems), 1)
        self.assertIn("not valid JSON", problems[0])


# --------------------------------------------------------------------------- version bindings

class VersionBindingTests(unittest.TestCase):
    def flag_value(self, flags: list, name: str) -> str:
        return flags[flags.index(name) + 1]

    def test_a_push_tag_binds_the_commit_and_the_identity(self):
        flags = ci.derive_flags("2.11.7-" + SHA, "push", SHA)
        self.assertEqual(self.flag_value(flags, "--certificate-identity"), ci.IDENTITY)
        self.assertEqual(self.flag_value(flags, "--certificate-github-workflow-sha"), SHA)
        self.assertEqual(self.flag_value(flags, "--certificate-github-workflow-trigger"), "push")
        self.assertEqual(self.flag_value(flags, "--certificate-github-workflow-ref"), "refs/heads/main")
        self.assertFalse([f for f in flags if "regexp" in f])

    def test_the_commit_comes_from_the_tag_when_none_is_given(self):
        flags = ci.derive_flags("2.11.7-" + SHA, "push")
        self.assertEqual(self.flag_value(flags, "--certificate-github-workflow-sha"), SHA)

    def test_red_a_commit_that_differs_from_the_tag_is_refused(self):
        with self.assertRaises(ci.ReleaseError):
            ci.derive_flags("2.11.7-" + SHA, "push", "c" * 40)

    def test_red_a_dry_run_or_moving_tag_never_verifies_as_a_push_image(self):
        for tag in ("dryrun-" + SHA[:12], "latest", "2.11.7", "2.11.7-" + SHA[:12], "v2.11.7-" + SHA):
            with self.subTest(tag=tag):
                with self.assertRaises(ci.ReleaseError):
                    ci.derive_flags(tag, "push")

    def test_a_dry_run_tag_binds_the_run_commit(self):
        flags = ci.derive_flags("dryrun-" + SHA[:12], "workflow_dispatch", SHA)
        self.assertEqual(self.flag_value(flags, "--certificate-github-workflow-trigger"), "workflow_dispatch")
        self.assertEqual(self.flag_value(flags, "--certificate-github-workflow-sha"), SHA)

    def test_red_a_dry_run_tag_for_another_commit_is_refused(self):
        with self.assertRaises(ci.ReleaseError):
            ci.derive_flags("dryrun-" + "c" * 12, "workflow_dispatch", SHA)
        with self.assertRaises(ci.ReleaseError):
            ci.derive_flags("dryrun-" + SHA[:12], "workflow_dispatch")

    def test_red_any_other_trigger_is_refused(self):
        for trigger in ("pull_request", "schedule", "workflow_run", ""):
            with self.subTest(trigger=trigger):
                with self.assertRaises(ci.ReleaseError):
                    ci.derive_flags("2.11.7-" + SHA, trigger, SHA)

    def verify_command(self, reference: str, runner: str = "cosign") -> str:
        _, output = quiet(ci.cmd_verify_command, SimpleNamespace(reference=reference, runner=runner))
        return output

    def test_verify_command_prints_the_exact_identity_and_the_tag_commit(self):
        digest = "sha256:" + "f" * 64
        output = self.verify_command("%s:2.11.7-%s@%s" % (ci.REMOTE, SHA, digest))
        self.assertIn(ci.IDENTITY, output)
        self.assertIn("--certificate-identity ", output)
        self.assertNotIn("regexp", output)
        self.assertIn("--certificate-github-workflow-sha " + SHA, output)
        self.assertIn("--certificate-github-workflow-trigger push", output)
        self.assertIn("%s@%s" % (ci.REMOTE, digest), output)

    def test_red_verify_command_refuses_other_references(self):
        digest = "@sha256:" + "f" * 64
        for reference in (
            "%s:dryrun-%s%s" % (ci.REMOTE, SHA[:12], digest),
            "%s-evil:2.11.7-%s%s" % (ci.REMOTE, SHA, digest),
            "ghcr.io/other/decisya-caddy:2.11.7-%s%s" % (SHA, digest),
            "%s:2.11.7-%s" % (ci.REMOTE, SHA),
        ):
            with self.subTest(reference=reference):
                with self.assertRaises(ci.ReleaseError):
                    self.verify_command(reference)

    def test_the_version_comparison_puts_a_release_above_its_prerelease(self):
        self.assertGreater(ci.vtuple("v1.2.3"), ci.vtuple("v1.2.3-rc1"))
        self.assertGreater(ci.vtuple("1.26.10"), ci.vtuple("1.26.9"))
        self.assertGreater(ci.vtuple("v0.60.0"), ci.vtuple("v0.59.0"))


# --------------------------------------------------------------------------- stale and unexplained overrides

X_NET = "golang.org/x/net"


def build_lists(ours: dict, upstream: dict, edges=()):
    return ({"modules": ours, "edges": list(edges), "go": "1.26.0"}, {"modules": upstream, "edges": [], "go": "1.26.0"})


class BuildListTests(unittest.TestCase):
    OVERRIDES = {X_NET: "v0.60.0"}

    def compare(self, ours: dict, upstream: dict, overrides=None, edges=()):
        mine, theirs = build_lists(ours, upstream, edges)
        return ci.compare_buildlists(mine, theirs, self.OVERRIDES if overrides is None else overrides)

    def assert_problem(self, result, containing: str):
        problems, _ = result
        self.assertTrue(any(containing in p for p in problems), "no problem mentions %r: %s" % (containing, problems))

    def test_an_annotated_override_is_the_only_difference(self):
        problems, rows = self.compare({X_NET: "v0.60.0", "golang.org/x/sys": "v0.48.0"}, {X_NET: "v0.59.0", "golang.org/x/sys": "v0.48.0"})
        self.assertEqual(problems, [])
        self.assertTrue(any("override" in row and X_NET in row for row in rows), rows)

    def test_a_module_raised_by_an_override_is_explained(self):
        edges = [(X_NET + "@v0.60.0", "golang.org/x/text@v0.43.0")]
        problems, _ = self.compare({X_NET: "v0.60.0", "golang.org/x/text": "v0.43.0"}, {X_NET: "v0.59.0", "golang.org/x/text": "v0.42.0"}, edges=edges)
        self.assertEqual(problems, [])

    def test_red_a_stale_override_fails_once_upstream_equals_it(self):
        self.assert_problem(self.compare({X_NET: "v0.60.0"}, {X_NET: "v0.60.0"}), "stale")

    def test_red_a_stale_override_fails_once_upstream_is_above_it(self):
        self.assert_problem(self.compare({X_NET: "v0.60.0"}, {X_NET: "v0.61.0"}), "stale")

    def test_red_an_unexplained_difference_fails(self):
        result = self.compare({X_NET: "v0.60.0", "golang.org/x/sys": "v0.49.0"}, {X_NET: "v0.59.0", "golang.org/x/sys": "v0.48.0"})
        self.assert_problem(result, "neither an annotated override nor raised by one")

    def test_red_a_module_only_we_carry_fails(self):
        self.assert_problem(self.compare({X_NET: "v0.60.0", "example.invalid/extra": "v1.0.0"}, {X_NET: "v0.59.0"}), "differs from upstream")

    def test_red_a_module_we_dropped_fails(self):
        self.assert_problem(self.compare({X_NET: "v0.60.0"}, {X_NET: "v0.59.0", "example.invalid/gone": "v1.0.0"}), "not in ours")

    def test_red_an_override_that_is_not_the_selected_version_fails(self):
        self.assert_problem(self.compare({X_NET: "v0.61.0"}, {X_NET: "v0.59.0"}), "is not the selected version")

    def test_red_a_difference_with_no_override_at_all_fails(self):
        self.assert_problem(self.compare({X_NET: "v0.60.0"}, {X_NET: "v0.59.0"}, overrides={}), "neither an annotated override nor raised by one")


# --------------------------------------------------------------------------- `inputs` refuses what the input tests refuse

class InputsSubcommandTests(unittest.TestCase):
    def run_inputs(self, edits: dict | None = None, extra=None):
        """Run `caddy_image.inputs` on a scratch copy of the folder. None when it passes, else the failure text."""
        with scratch_dir() as parent:
            folder = parent / "image"
            shutil.copytree(IMAGE_DIR, folder)
            for name, edit in (edits or {}).items():
                path = folder / name
                path.write_text(edit(path.read_text(encoding="utf-8")), encoding="utf-8")
            if extra:
                extra(folder)
            patches = dict(INPUT_DIR=folder, DOCKERFILE=folder / "Dockerfile", GO_MOD=folder / "go.mod",
                           GO_SUM=folder / "go.sum", MAIN_GO=folder / "main.go", MODULES_TXT=folder / "modules.txt")
            del ri.LOG[:]
            try:
                with mock.patch.multiple(ci, **patches):
                    try:
                        quiet(ci.cmd_inputs, None)
                        return None
                    except ci.ReleaseError as exc:
                        return str(exc) + "\n" + "\n".join(ri.LOG)
            finally:
                del ri.LOG[:]

    def assert_refused(self, containing: str, edits: dict | None = None, extra=None):
        result = self.run_inputs(edits, extra)
        self.assertIsNotNone(result, "inputs passed; expected a failure mentioning %r" % containing)
        self.assertIn(containing, result)

    @staticmethod
    def sub(old: str, new: str):
        def edit(text: str) -> str:
            assert old in text, "fixture text not found: %r" % old
            return text.replace(old, new, 1)
        return edit

    def test_the_real_inputs_pass(self):
        self.assertIsNone(self.run_inputs())

    # Dockerfile
    def test_red_a_parser_directive_is_refused(self):
        self.assert_refused("parser directive", {"Dockerfile": lambda t: "# syntax=docker/dockerfile:1\n" + t})

    def test_red_an_unresolved_digest_is_refused(self):
        edit = lambda t: re.sub(r"(golang:\S+)@sha256:[0-9a-f]{64}", r"\1@sha256:" + "0" * 64, t, count=1)
        self.assert_refused("all-zero", {"Dockerfile": edit})

    def test_red_add_and_a_directory_copy_are_refused(self):
        self.assert_refused("ADD is not allowed", {"Dockerfile": self.sub("COPY go.mod go.sum main.go ./", "ADD go.mod go.sum main.go ./")})
        self.assert_refused("must copy exactly", {"Dockerfile": self.sub("COPY go.mod go.sum main.go ./", "COPY . ./")})

    def test_red_a_changed_go_environment_is_refused(self):
        self.assert_refused("builder ENV differs", {"Dockerfile": self.sub("    GOWORK=off \\\n", "")})
        self.assert_refused("builder ENV differs", {"Dockerfile": self.sub("GOFLAGS=-mod=readonly", "GOFLAGS=-mod=mod")})

    def test_red_a_final_stage_entrypoint_volume_or_run_is_refused(self):
        self.assert_refused("must not contain ENTRYPOINT", {"Dockerfile": lambda t: t + '\nENTRYPOINT ["caddy"]\n'})
        self.assert_refused("must not contain VOLUME", {"Dockerfile": lambda t: t + "\nVOLUME /data\n"})
        self.assert_refused("must not contain RUN", {"Dockerfile": lambda t: t + "\nRUN true\n"})

    def test_red_an_applet_outside_the_contract_is_refused(self):
        self.assert_refused("BusyBox link set", {"Dockerfile": self.sub(" wget; do", " wget tar; do")})

    def test_red_a_debug_runtime_is_refused(self):
        self.assert_refused("plain root", {"Dockerfile": self.sub("static-debian13:latest@", "static-debian13:debug@")})

    # go.mod
    def test_red_a_replace_or_godebug_is_refused(self):
        self.assert_refused("'replace' is not allowed", {"go.mod": lambda t: t + "\nreplace golang.org/x/net => ../net\n"})
        self.assert_refused("'godebug' is not allowed", {"go.mod": lambda t: t + "\ngodebug x509sha1=1\n"})

    def test_red_a_second_direct_requirement_is_refused(self):
        self.assert_refused("exactly one direct requirement", {"go.mod": lambda t: t + "\nrequire github.com/example/other v1.0.0\n"})

    def test_red_a_badly_annotated_override_is_refused(self):
        self.assert_refused("must read '// indirect; override", {"go.mod": self.sub(" #188", "")})

    def test_red_a_go_line_or_toolchain_above_the_builder_is_refused(self):
        self.assert_refused("is above the builder's Go", {"go.mod": self.sub("\ngo 1.26.0\n", "\ngo 1.27.0\n")})
        self.assert_refused("exceeds the builder's Go", {"go.mod": self.sub("\ngo 1.26.0\n", "\ngo 1.26.0\n\ntoolchain go1.26.99\n")})

    # main.go and the folder
    def test_red_a_go_directive_or_extra_import_in_main_go_is_refused(self):
        self.assert_refused("no //go: directive", {"main.go": lambda t: "//go:debug x509sha1=1\n" + t})
        old = '_ "github.com/caddyserver/caddy/v2/modules/standard"'
        self.assert_refused("must import exactly", {"main.go": self.sub(old, old + '\n\t_ "example.invalid/plugin"')})

    def test_red_a_vendor_folder_go_work_or_other_go_file_is_refused(self):
        self.assert_refused("vendor", extra=lambda f: (f / "vendor").mkdir())
        self.assert_refused("go.work", extra=lambda f: (f / "go.work").write_text("go 1.26.0\n", encoding="utf-8"))
        self.assert_refused("extra.go", extra=lambda f: (f / "extra.go").write_text("package main\n", encoding="utf-8"))

    def test_red_an_empty_modules_txt_is_refused(self):
        self.assert_refused("modules.txt is missing or empty", extra=lambda f: (f / "modules.txt").write_text("", encoding="utf-8"))


# --------------------------------------------------------------------------- Dependabot

def dependabot_entries(text: str) -> list:
    entries = []
    for chunk in re.split(r"(?m)^  - package-ecosystem:", text)[1:]:
        directory = re.search(r"(?m)^    directory:\s*(\S+)", chunk)
        entries.append({"ecosystem": chunk.splitlines()[0].strip(), "directory": directory.group(1) if directory else None, "text": chunk})
    return entries


def find_entries(text: str, ecosystem: str, directory: str) -> list:
    return [e for e in dependabot_entries(text) if e["ecosystem"] == ecosystem and e["directory"] == directory]


def dependabot_problems(text: str) -> list:
    p = []
    for ecosystem, directory in (("github-actions", "/"), ("nuget", "/"), ("npm", "/src/Decisya.Web")):
        if len(find_entries(text, ecosystem, directory)) != 1:
            p.append("ecosystems: no single %s entry for %s" % (ecosystem, directory))
    weekly = r"(?m)^    schedule: \{ interval: weekly \}$"
    cooldown = r"(?m)^    cooldown: \{ default-days: 7 \}$"

    docker = find_entries(text, "docker", "/deploy/caddy/image")
    if len(docker) != 1:
        p.append("caddy-docker: no single docker entry for /deploy/caddy/image")
    else:
        body = docker[0]["text"]
        if not re.search(weekly, body) or not re.search(cooldown, body):
            p.append("caddy-docker: the docker entry must be weekly with a 7-day cooldown")
        m = re.search(r'dependency-name:\s*"golang"\s*\n\s*update-types:\s*\[([^\]]*)\]', body)
        ignored = set(re.findall(r'"([^"]+)"', m.group(1))) if m else set()
        if ignored != {"version-update:semver-major", "version-update:semver-minor"}:
            p.append("caddy-docker: the golang line must ignore semver-major and semver-minor, got %s" % sorted(ignored))

    gomod = find_entries(text, "gomod", "/deploy/caddy/image")
    if len(gomod) != 1:
        p.append("caddy-gomod: no single gomod entry for /deploy/caddy/image")
    else:
        body = gomod[0]["text"]
        if not re.search(weekly, body) or not re.search(cooldown, body):
            p.append("caddy-gomod: the gomod entry must be weekly with a 7-day cooldown")
        allow = re.search(r"(?m)^    allow:\n((?:      .*\n)+)", body)
        names = re.findall(r'dependency-name:\s*"([^"]+)"', allow.group(1)) if allow else []
        if names != ["github.com/caddyserver/caddy/v2"]:
            p.append("caddy-gomod: allow must list only github.com/caddyserver/caddy/v2, got %s" % names)

    engine = find_entries(text, "docker", "/.devcontainer/engine")
    if len(engine) != 1 or not re.search(r'dependency-name:\s*"mpcs2013/decisya-caddy"', engine[0]["text"]):
        p.append("engine-ignore: the /.devcontainer/engine entry must ignore mpcs2013/decisya-caddy")
    return p


def mutate_entry(text: str, ecosystem: str, directory: str, old: str, new: str) -> str:
    header = "  - package-ecosystem: %s\n    directory: %s\n" % (ecosystem, directory)
    start = text.index(header)
    end = text.find("\n  - package-ecosystem:", start + 1)
    end = len(text) if end < 0 else end
    chunk = text[start:end]
    assert old in chunk, "fixture text not found in the entry: %r" % old
    return text[:start] + chunk.replace(old, new, 1) + text[end:]


class DependabotTests(unittest.TestCase):
    def setUp(self):
        self.text = DEPENDABOT.read_text(encoding="utf-8")

    def assert_red(self, tag: str, text: str):
        problems = dependabot_problems(text)
        self.assertIn(tag, {p.split(":")[0] for p in problems}, "no %s problem; got %s" % (tag, problems))

    def test_the_real_file_breaks_no_rule(self):
        self.assertEqual(dependabot_problems(self.text), [])

    def test_red_a_missing_docker_entry_is_refused(self):
        self.assert_red("caddy-docker", mutate_entry(self.text, "docker", "/deploy/caddy/image", "directory: /deploy/caddy/image", "directory: /deploy/caddy/other"))

    def test_red_a_docker_entry_without_a_cooldown_is_refused(self):
        self.assert_red("caddy-docker", mutate_entry(self.text, "docker", "/deploy/caddy/image", "    cooldown: { default-days: 7 }\n", ""))

    def test_red_the_golang_minor_bump_is_not_lifted(self):
        both = 'update-types: ["version-update:semver-major", "version-update:semver-minor"]'
        self.assert_red("caddy-docker", mutate_entry(self.text, "docker", "/deploy/caddy/image", both, 'update-types: ["version-update:semver-major"]'))

    def test_red_a_missing_gomod_entry_is_refused(self):
        self.assert_red("caddy-gomod", mutate_entry(self.text, "gomod", "/deploy/caddy/image", "directory: /deploy/caddy/image", "directory: /deploy/caddy/other"))

    def test_red_a_gomod_entry_without_a_cooldown_is_refused(self):
        self.assert_red("caddy-gomod", mutate_entry(self.text, "gomod", "/deploy/caddy/image", "    cooldown: { default-days: 7 }\n", ""))

    def test_red_a_wider_gomod_allow_list_is_refused(self):
        old = '      - dependency-name: "github.com/caddyserver/caddy/v2"\n'
        self.assert_red("caddy-gomod", mutate_entry(self.text, "gomod", "/deploy/caddy/image", old, old + '      - dependency-name: "golang.org/x/net"\n'))

    def test_red_a_gomod_entry_without_an_allow_list_is_refused(self):
        old = '    allow:\n      - dependency-name: "github.com/caddyserver/caddy/v2"\n'
        self.assert_red("caddy-gomod", mutate_entry(self.text, "gomod", "/deploy/caddy/image", old, ""))

    def test_red_the_engine_entry_must_ignore_our_own_image(self):
        old = '      - dependency-name: "mpcs2013/decisya-caddy"\n'
        self.assertIn(old, self.text)
        self.assert_red("engine-ignore", self.text.replace(old, "", 1))

    def test_red_the_actions_nuget_and_npm_entries_stay(self):
        for ecosystem in ("github-actions", "nuget", "npm"):
            with self.subTest(ecosystem=ecosystem):
                text = mutate_header(self.text, ecosystem)
                self.assert_red("ecosystems", text)


def mutate_header(text: str, ecosystem: str) -> str:
    old = "  - package-ecosystem: %s\n" % ecosystem
    assert old in text, "fixture text not found: %r" % old
    return text.replace(old, "  - package-ecosystem: pip\n", 1)


if __name__ == "__main__":
    unittest.main()
