"""Image-scan rules added in #120 (G3 deltas 2026-10-06 and 2026-10-07; ADR-0015 amendment 2026-10-06).

- Binary-only images (G4-120-08, G4-120-10, S-120-15 to 17): only the aliases mapped in
  BINARY_ONLY_ALIASES may lack an OS distro, and only with Go-module evidence from the same Grype run's
  CycloneDX SBOM. Grype's SBOM carries no digest, so the binding is structural (G3 ruling 2026-10-07).
- The narrow D-2 rule (G4-120-09): an exception keeps a fix-available finding only for an OS package,
  an exception of at most 30 days, and a tag that still resolves to exactly the pinned digest. Every
  failure to resolve the tag fails closed.
- Item 4 (#142, ADR-0015 amendment 2026-10-17, G3 M-6, M-7, S-4): a fixed go-module finding in a
  vendor binary (caddy only) is excepted for at most 14 days, with Marco's dated approval, until the
  2026-11-30 sunset, and only for the scanned caddy reference. Test 4 above keeps its caddy case with no
  reference (no reference, no item 4); the moved negatives live in VendorBinaryItem4Tests.
No test touches the network: the HTTP seams are replaced.
"""
import importlib.util
import inspect
import json
import os
import tempfile
import unittest
import urllib.error
from datetime import date, datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TODAY = date(2026, 10, 7)
PIN = "a" * 64


def load_scan():
    path = ROOT / ".github" / "scripts" / "image_scan.py"
    spec = importlib.util.spec_from_file_location("_ci_image_scan_120", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def go_component(purl="pkg:golang/golang.org/x/net@v0.1.0", layer="sha256:" + "1" * 64, kind="go-module"):
    return {"type": "library", "name": purl, "purl": purl,
            "properties": [{"name": "syft:package:type", "value": kind},
                           {"name": "syft:location:0:layerID", "value": layer}]}


class BinaryOnlyTests(unittest.TestCase):
    REF = "docker.io/otel/opentelemetry-collector:0.161.0@sha256:" + "b" * 64
    LAYER = "sha256:" + "1" * 64

    def setUp(self):
        self.scan = load_scan()

    def doc(self, matches=None, distro=None, layers=None, user_input=None):
        return {"matches": [] if matches is None else matches,
                "source": {"type": "image", "target": {
                    "userInput": user_input or self.REF,
                    "layers": [{"digest": self.LAYER}, {"digest": "sha256:" + "2" * 64}] if layers is None else layers}},
                "distro": {} if distro is None else distro,
                "descriptor": {"db": {"built": "2026-10-07T00:00:00Z"}}}

    def sbom(self, name="docker.io/otel/opentelemetry-collector", version="0.161.0", components=None, ctype="container"):
        return {"metadata": {"component": {"type": ctype, "name": name, "version": version}},
                "components": [go_component(layer=self.LAYER)] if components is None else components}

    def trusted(self, doc, sbom, alias="otelcollector", ref=None):
        return self.scan.check_provenance(doc, ref or self.REF, alias, sbom)

    @staticmethod
    def go_match(purl="pkg:golang/golang.org/x/net@v0.1.0", severity="Low"):
        return {"vulnerability": {"id": "GHSA-hhhh-jjjj-mmmm", "severity": severity, "fix": {"state": "not-fixed"}},
                "artifact": {"name": "golang.org/x/net", "version": "0.1.0", "type": "go-module", "purl": purl},
                "relatedVulnerabilities": []}

    # S-120-15: the exemption is tied to one image, and the list grows only with a code and test change.
    def test_the_allow_list_maps_exactly_the_collector_to_its_image(self):
        self.assertEqual(self.scan.BINARY_ONLY_ALIASES, {"otelcollector": "docker.io/otel/opentelemetry-collector"})
        self.assertLessEqual(set(self.scan.BINARY_ONLY_ALIASES), set(self.scan.ALIASES))

    # G4-120-10 test 1: a clean collector (no vulnerable package) is trusted on SBOM evidence.
    def test_zero_matches_with_a_valid_sbom_is_trusted(self):
        self.assertEqual(self.trusted(self.doc(), self.sbom()), [])

    def test_a_missing_or_unusable_sbom_is_not_trusted(self):
        for name, sbom in {"missing": None, "not an object": [], "no metadata": {"components": []},
                           "no components list": {"metadata": {"component": {"type": "container",
                               "name": "docker.io/otel/opentelemetry-collector", "version": "0.161.0"}}},
                           "empty components": self.sbom(components=[])}.items():
            with self.subTest(case=name):
                self.assertTrue(self.trusted(self.doc(), sbom))

    def test_an_sbom_without_go_module_components_is_not_trusted(self):
        for name, comps in {"file only": [{"type": "file", "name": "/otelcol"}],
                            "binary type": [go_component(kind="binary", layer=self.LAYER)],
                            "go type without golang purl": [{**go_component(layer=self.LAYER), "purl": "pkg:generic/x@1"}]}.items():
            with self.subTest(case=name):
                self.assertTrue(any("no Go-module component" in p for p in self.trusted(self.doc(), self.sbom(components=comps))))

    # G4-120-10 revised tests 4 and 5 (G3 ruling 2026-10-07): name and tag bind the SBOM to the alias.
    def test_a_wrong_image_name_tag_or_type_is_not_trusted(self):
        for name, sbom in {"other image": self.sbom(name="docker.io/otel/opentelemetry-collector-contrib"),
                           "other tag": self.sbom(version="0.160.0"),
                           "not a container": self.sbom(ctype="application")}.items():
            with self.subTest(case=name):
                self.assertTrue(self.trusted(self.doc(), sbom))

    def test_the_sbom_is_never_read_when_the_json_fails_provenance(self):
        calls = []

        def loader():
            calls.append(1)
            return self.sbom()

        for name, doc in {"empty layers": self.doc(layers=[]), "tag not digest": self.doc(user_input=self.REF.split("@")[0]),
                          "no db": {**self.doc(), "descriptor": {}}}.items():
            with self.subTest(case=name):
                self.assertTrue(self.trusted(doc, loader))
        self.assertEqual(calls, [], "the SBOM loader must not run once the JSON has failed")
        self.assertEqual(self.trusted(self.doc(), loader), [])
        self.assertEqual(calls, [1])

    # G4-120-10 test 6 and S-120-15: no other image, and no re-pointed collector, may lack a distro.
    def test_an_unlisted_or_repointed_image_with_no_distro_still_fails(self):
        for alias in ("caddy", "postgres", "redis", "keycloak", "api", "bff", "migrator", None):
            with self.subTest(alias=alias):
                problems = self.trusted(self.doc(), self.sbom(), alias=alias)
                self.assertTrue(any("distro.name is empty" in p for p in problems), problems)
        other = "docker.io/evil/collector:0.161.0@sha256:" + "b" * 64
        problems = self.trusted(self.doc(user_input=other), self.sbom(name="docker.io/evil/collector"), ref=other)
        self.assertTrue(any("distro.name is empty" in p for p in problems), problems)

    # S-120-16: the two outputs of one run must agree.
    def test_a_json_go_match_absent_from_the_sbom_is_not_trusted(self):
        doc = self.doc(matches=[self.go_match(purl="pkg:golang/example.com/other@v1.0.0")])
        self.assertTrue(any("same purl" in p for p in self.trusted(doc, self.sbom())))
        doc = self.doc(matches=[self.go_match(purl=None)])
        self.assertTrue(self.trusted(doc, self.sbom()), "a match without a purl fails closed")

    # S-120-17: the SBOM's Go components come from a layer the JSON scanned.
    def test_go_components_outside_the_scanned_layers_are_not_trusted(self):
        sbom = self.sbom(components=[go_component(layer="sha256:" + "9" * 64)])
        self.assertTrue(any("layer listed in the JSON" in p for p in self.trusted(self.doc(), sbom)))

    # G4-120-10 test 8: trust is not an exception; a High still blocks.
    def test_a_trusted_binary_only_scan_still_blocks_on_a_high_finding(self):
        doc = self.doc(matches=[self.go_match(severity="High")])
        self.assertEqual(self.trusted(doc, self.sbom()), [])
        self.assertTrue(self.scan.evaluate(doc, "otelcollector", [], TODAY)["findings"])

    # G4-120-10 test 9: one docker run writes both outputs into a separate, per-alias mount.
    def test_one_docker_run_writes_json_and_the_sbom_into_its_own_mount(self):
        argv = self.scan.scan_argv("scanner@sha256:" + "c" * 64, self.REF, "/tmp/db", "/tmp/out-1")
        self.assertEqual(argv.count("docker"), 1)
        self.assertIn("/tmp/out-1:" + self.scan.OUT_MOUNT, argv)
        self.assertIn("/tmp/db:" + self.scan.DB_MOUNT, argv)
        self.assertNotEqual(self.scan.OUT_MOUNT, self.scan.DB_MOUNT)
        outputs = [argv[i + 1] for i, a in enumerate(argv) if a == "-o"]
        self.assertEqual(outputs, ["json", f"cyclonedx-json={self.scan.OUT_MOUNT}/{self.scan.SBOM_NAME}"])
        self.assertNotIn(self.scan.OUT_MOUNT, " ".join(self.scan.scan_argv("s", self.REF, "/tmp/db")))

    def test_read_sbom_refuses_missing_empty_linked_and_invalid_files(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(self.scan.read_sbom(d), "missing")
            path = os.path.join(d, self.scan.SBOM_NAME)
            Path(path).write_text("", encoding="utf-8")
            self.assertIsNone(self.scan.read_sbom(d), "empty")
            Path(path).write_text("{not json", encoding="utf-8")
            self.assertIsNone(self.scan.read_sbom(d), "invalid")
            Path(path).write_text('{"metadata": {}}', encoding="utf-8")
            self.assertEqual(self.scan.read_sbom(d), {"metadata": {}})
        with tempfile.TemporaryDirectory() as d:
            target = os.path.join(d, "real.json")
            Path(target).write_text('{"metadata": {}}', encoding="utf-8")
            try:
                os.symlink(target, os.path.join(d, self.scan.SBOM_NAME))
            except (OSError, NotImplementedError):
                self.skipTest("symlinks need privileges on this host")
            self.assertIsNone(self.scan.read_sbom(d), "a symlinked SBOM is refused")


class NarrowD2Tests(unittest.TestCase):
    """G4-120-09: the ten red tests of the 2026-10-07 delta."""

    def setUp(self):
        self.scan = load_scan()

    @staticmethod
    def fixed(kind="apk", pkg="zlib", vid="CVE-2026-85091"):
        return {"vulnerability": {"id": vid, "severity": "High", "fix": {"state": "fixed", "versions": ["1.3.2-r1"]}},
                "artifact": {"name": pkg, "version": "1.3.2-r0", "type": kind}, "relatedVulnerabilities": []}

    @staticmethod
    def exc(image="redis", pkg="zlib", vid="CVE-2026-85091", added=date(2026, 10, 3), expires=date(2026, 11, 2)):
        return {"id": vid, "image": image, "package": pkg, "added_date": added, "expires_date": expires}

    def run_eval(self, alias, match, exceptions, **kw):
        return self.scan.evaluate({"matches": [match]}, alias, exceptions, TODAY, **kw)

    def test_1_an_os_package_is_excepted_while_the_tag_is_unchanged(self):
        r = self.run_eval("redis", self.fixed(), [self.exc()], tag_state="unchanged", tag_label="library/redis:8.10.2-alpine")
        self.assertEqual(r["findings"], [])
        self.assertIn("no rebuilt image", r["excepted"][0]["note"])

    def test_2_a_moved_tag_ends_the_exception(self):
        r = self.run_eval("redis", self.fixed(), [self.exc()], tag_state="moved", tag_label="library/redis:8.10.2-alpine")
        self.assertEqual(r["excepted"], [])
        self.assertIn("tag moved: rescan and bump", r["findings"][0]["note"])

    def test_3_every_unresolvable_tag_is_unknown_and_ends_the_exception(self):
        ok_token = lambda url, headers: (200, b'{"token": "t"}')  # noqa: E731

        def head_with(status=200, digest="sha256:" + PIN, exc=None):
            def head(url, headers):
                if exc:
                    raise exc
                return status, ({"docker-content-digest": digest} if digest is not None else {})
            return head

        cases = {
            "connection error": (ok_token, head_with(exc=OSError("down")), "docker.io"),
            "timeout": (ok_token, head_with(exc=TimeoutError()), "docker.io"),
            "redirect": (ok_token, head_with(exc=urllib.error.HTTPError("u", 302, "Found", {}, None)), "docker.io"),
            "404": (ok_token, head_with(status=404), "docker.io"),
            "500": (ok_token, head_with(status=500), "docker.io"),
            "missing header": (ok_token, head_with(digest=None), "docker.io"),
            "malformed header": (ok_token, head_with(digest="not-a-digest"), "docker.io"),
            "sha512": (ok_token, head_with(digest="sha512:" + "a" * 128), "docker.io"),
            "prefix only": (ok_token, head_with(digest="sha256:" + PIN + "ff"), "docker.io"),
            "token refused": (lambda u, h: (401, b"{}"), head_with(), "docker.io"),
            "unknown registry": (ok_token, head_with(), "example.com"),
        }
        for name, (get, head, registry) in cases.items():
            with self.subTest(case=name):
                self.scan._http_get, self.scan._http_head = get, head
                self.assertEqual(self.scan.resolve_tag_state(registry, "library/redis", "8.10.2-alpine", PIN), "unknown")
        r = self.run_eval("redis", self.fixed(), [self.exc()], tag_state="unknown")
        self.assertEqual(r["excepted"], [])
        self.assertIn("tag resolution failed", r["findings"][0]["note"])

    def test_3b_only_an_exact_digest_is_unchanged_and_the_request_is_well_formed(self):
        seen = {}
        self.scan._http_get = lambda url, headers: (200, b'{"token": "t"}')

        def head(url, headers):
            seen["url"], seen["headers"] = url, headers
            return 200, {"docker-content-digest": "sha256:" + PIN}

        self.scan._http_head = head
        self.assertEqual(self.scan.resolve_tag_state("docker.io", "library/redis", "8.10.2-alpine", PIN), "unchanged")
        self.assertEqual(seen["url"], "https://registry-1.docker.io/v2/library/redis/manifests/8.10.2-alpine")
        for media in ("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json"):
            self.assertIn(media, seen["headers"]["Accept"])
        self.scan._http_head = lambda url, headers: (200, {"docker-content-digest": "sha256:" + "b" * 64})
        self.assertEqual(self.scan.resolve_tag_state("docker.io", "library/redis", "8.10.2-alpine", PIN), "moved")

    def test_4_a_fixed_go_module_finding_still_blocks_with_an_unchanged_tag(self):
        r = self.run_eval("caddy", self.fixed(kind="go-module", pkg="stdlib", vid="CVE-2026-11111"),
                          [self.exc(image="caddy", pkg="stdlib", vid="CVE-2026-11111", added=date(2026, 10, 6), expires=date(2026, 10, 20))],
                          tag_state="unchanged")
        self.assertEqual(r["excepted"], [])
        self.assertTrue(r["findings"])

    def test_5_a_fixed_java_finding_still_blocks_with_an_unchanged_tag(self):
        r = self.run_eval("keycloak", self.fixed(kind="java-archive", pkg="netty-codec", vid="CVE-2026-22222"),
                          [self.exc(image="keycloak", pkg="netty-codec", vid="CVE-2026-22222", added=date(2026, 10, 6), expires=date(2026, 10, 20))],
                          tag_state="unchanged")
        self.assertEqual(r["excepted"], [])
        self.assertTrue(r["findings"])

    def test_6_an_exception_longer_than_30_days_keeps_strict_d2(self):
        r = self.run_eval("redis", self.fixed(), [self.exc(added=date(2026, 10, 3), expires=date(2026, 11, 3))], tag_state="unchanged")
        self.assertEqual(r["excepted"], [])
        self.assertTrue(r["findings"])

    def test_7_a_fixed_finding_without_an_exception_blocks(self):
        r = self.run_eval("redis", self.fixed(), [], tag_state="unchanged")
        self.assertTrue(r["findings"])

    def test_8_the_release_path_without_a_tag_state_keeps_strict_d2(self):
        r = self.run_eval("redis", self.fixed(), [self.exc()])
        self.assertEqual(r["excepted"], [])
        r = self.run_eval("api", self.fixed(), [self.exc(image="api")], tag_state="unchanged")
        self.assertEqual(r["excepted"], [], "release images are not scan aliases and never get the narrow rule")

    def test_9_the_rule_is_code_not_configuration(self):
        self.assertEqual(self.scan.ENTRY_KEYS, {"id", "image", "package", "justification", "issue", "added", "expires"})
        self.assertEqual(self.scan.OS_PACKAGE_TYPES, frozenset({"apk", "deb", "rpm"}))
        self.assertEqual(self.scan.MAX_FIXED_EXCEPTION_DAYS, 30)
        self.assertLessEqual(self.scan.HTTP_TIMEOUT, 30)
        source = inspect.getsource(self.scan.resolve_tag_state) + inspect.getsource(self.scan._http_head) \
            + inspect.getsource(self.scan._http_get) + inspect.getsource(self.scan._anonymous_token)
        self.assertNotIn("os.environ", source)
        self.assertNotIn("getenv", source)
        self.assertNotIn("print(", source, "the token is never printed")


class VendorBinaryItem4Tests(unittest.TestCase):
    """#142: ADR-0015 item 4 (G3 2026-10-17 M-6, M-7, S-4 and ruling 3)."""

    CADDY = "docker.io/library/caddy:2.11.7-alpine@sha256:" + PIN
    DAY = date(2026, 10, 20)
    APPROVED = "Fixed in Go, no Caddy release yet; item 4 under #142, approved by Marco 2026-10-17."

    def setUp(self):
        self.scan = load_scan()

    @staticmethod
    def fixed(kind="go-module", pkg="stdlib", vid="CVE-2026-78663"):
        return {"vulnerability": {"id": vid, "severity": "Critical", "fix": {"state": "fixed", "versions": ["1.26.9"]}},
                "artifact": {"name": pkg, "version": "go1.26.8", "type": kind}, "relatedVulnerabilities": []}

    def exc(self, image="caddy", pkg="stdlib", vid="CVE-2026-78663", added=date(2026, 10, 17),
            expires=date(2026, 10, 31), justification=None):
        return {"id": vid, "image": image, "package": pkg, "issue": "#142",
                "justification": self.APPROVED if justification is None else justification,
                "added": added.isoformat(), "expires": expires.isoformat(),
                "added_date": added, "expires_date": expires}

    def run_eval(self, alias, match, exceptions, today=None, **kw):
        return self.scan.evaluate({"matches": [match]}, alias, exceptions, today or self.DAY, **kw)

    def errors_for(self, entry, today=date(2026, 10, 17)):
        raw = {k: v for k, v in entry.items() if not k.endswith("_date")}
        errors, _ = self.scan.exception_errors({"exceptions": [raw]}, today)
        return errors

    def test_the_rule_is_code_with_exact_values(self):
        self.assertEqual(self.scan.VENDOR_BINARY_ALIASES, {"caddy": "docker.io/library/caddy"})
        self.assertEqual(self.scan.MAX_VENDOR_BINARY_EXCEPTION_DAYS, 14)
        self.assertEqual(self.scan.VENDOR_BINARY_SUNSET, date(2026, 11, 30))
        self.assertEqual(self.scan.ENTRY_KEYS, {"id", "image", "package", "justification", "issue", "added", "expires"})
        source = inspect.getsource(self.scan._keeps_vendor_binary_exception) + inspect.getsource(self.scan.is_vendor_binary_entry) \
            + inspect.getsource(self.scan._vendor_binary_errors) + inspect.getsource(self.scan.vendor_binary_approval)
        for banned in ("os.environ", "getenv", "_http_", "resolve_tag_state", "tag_state"):
            self.assertNotIn(banned, source, f"item 4 never uses {banned}")

    def test_item_4_makes_no_registry_call(self):
        """G6 N-1: with every registry seam raising, item 4 still decides from the dates alone."""
        def boom(*args, **kwargs):
            raise AssertionError("item 4 must not touch the registry")
        self.scan._http_get = self.scan._http_head = self.scan._anonymous_token = self.scan.resolve_tag_state = boom
        r = self.run_eval("caddy", self.fixed(), [self.exc()], ref=self.CADDY)
        self.assertEqual(r["findings"], [])
        self.assertEqual(r["excepted"][0]["rule"], self.scan.RULE_ITEM4)

    def test_the_release_path_never_reaches_item_4(self):
        """G6 N-1: release_images.py neither names the alias map nor passes a scanned reference to evaluate()."""
        source = (ROOT / ".github" / "scripts" / "release_images.py").read_text(encoding="utf-8")
        self.assertIn("ims.evaluate(", source, "the release path still uses the shared policy")
        self.assertNotIn("VENDOR_BINARY", source)
        self.assertNotIn("ref=", source)

    def test_a_fixed_caddy_go_module_finding_is_excepted_under_item_4(self):
        for pkg in ("stdlib", "golang.org/x/net"):
            with self.subTest(package=pkg):
                r = self.run_eval("caddy", self.fixed(pkg=pkg), [self.exc(pkg=pkg)], ref=self.CADDY, tag_state="unchanged")
                self.assertEqual(r["findings"], [])
                self.assertEqual(r["excepted"][0]["rule"], self.scan.RULE_ITEM4)
                self.assertIn("ADR-0015 item 4", r["excepted"][0]["note"])

    def test_item_4_ignores_the_tag_state(self):
        for state in ("moved", "unknown", None):
            with self.subTest(tag_state=state):
                r = self.run_eval("caddy", self.fixed(), [self.exc()], ref=self.CADDY, tag_state=state)
                self.assertEqual(r["findings"], [])

    def test_without_the_scanned_reference_item_4_never_applies(self):
        r = self.run_eval("caddy", self.fixed(), [self.exc()], tag_state="unchanged")
        self.assertEqual(r["excepted"], [])
        self.assertTrue(r["findings"])

    def test_a_repointed_caddy_reference_loses_item_4(self):
        for ref in ("docker.io/someone/caddy:2.11.7-alpine@sha256:" + PIN, "ghcr.io/library/caddy:2.11.7-alpine@sha256:" + PIN, "not a ref"):
            with self.subTest(ref=ref):
                r = self.run_eval("caddy", self.fixed(), [self.exc()], ref=ref, tag_state="unchanged")
                self.assertEqual(r["excepted"], [])

    def test_other_aliases_keep_strict_d2_for_go_modules(self):
        """Ruling 3: test 4's negative case moved here, with an unchanged tag and a 14-day entry."""
        refs = {"keycloak": "quay.io/keycloak/keycloak:26.4@sha256:" + PIN,
                "otelcollector": "docker.io/otel/opentelemetry-collector:0.163.1@sha256:" + PIN}
        for alias, ref in refs.items():
            with self.subTest(alias=alias):
                r = self.run_eval(alias, self.fixed(), [self.exc(image=alias)], ref=ref, tag_state="unchanged")
                self.assertEqual(r["excepted"], [])
                self.assertTrue(r["findings"])

    def test_other_package_types_in_caddy_keep_strict_d2(self):
        # apk/deb/rpm stay under item 3 (NarrowD2Tests); every other non-go-module type keeps strict D-2.
        for kind, pkg in (("java-archive", "netty/codec"), ("binary", "stdlib"), ("python", "golang.org/x/net")):
            with self.subTest(kind=kind):
                r = self.run_eval("caddy", self.fixed(kind=kind, pkg=pkg), [self.exc(pkg=pkg)], ref=self.CADDY, tag_state="unchanged")
                self.assertEqual(r["excepted"], [])

    def test_a_go_module_name_without_a_slash_is_not_covered(self):
        r = self.run_eval("caddy", self.fixed(pkg="xnet"), [self.exc(pkg="xnet")], ref=self.CADDY, tag_state="unchanged")
        self.assertEqual(r["excepted"], [])

    def test_a_caddy_entry_breaking_a_bound_blocks_even_with_an_unchanged_tag(self):
        """Ruling 3: item 3's 30-day cap never leaks into go-module."""
        cases = {
            "15-day span": self.exc(added=date(2026, 10, 17), expires=date(2026, 11, 1)),
            "30-day span": self.exc(added=date(2026, 10, 6), expires=date(2026, 11, 5)),
            "expires past approval + 14": self.exc(added=date(2026, 10, 20), expires=date(2026, 11, 2)),
            "no approval": self.exc(justification="Fixed in Go, no Caddy release yet; item 4 under #142."),
            "expires after the sunset": self.exc(added=date(2026, 11, 25), expires=date(2026, 12, 2),
                                                 justification="#142, approved by Marco 2026-11-25."),
        }
        for name, entry in cases.items():
            with self.subTest(case=name):
                r = self.run_eval("caddy", self.fixed(), [entry], today=entry["added_date"], ref=self.CADDY, tag_state="unchanged")
                self.assertEqual(r["excepted"], [])
                self.assertTrue(r["findings"])

    def test_an_expired_item_4_entry_no_longer_covers(self):
        """G5 N-1: covered on expires, not the day after."""
        on_expiry = self.run_eval("caddy", self.fixed(), [self.exc()], today=date(2026, 10, 31), ref=self.CADDY, tag_state="unchanged")
        self.assertEqual(on_expiry["findings"], [])
        after = self.run_eval("caddy", self.fixed(), [self.exc()], today=date(2026, 11, 1), ref=self.CADDY, tag_state="unchanged")
        self.assertEqual(after["excepted"], [])
        self.assertTrue(after["findings"])

    def test_a_caddy_os_package_stays_under_item_3_when_the_reference_is_passed(self):
        """G5 N-2: item 4 is not consulted for apk, so item 3's tag rule and 30-day cap still decide."""
        apk = self.fixed(kind="apk", pkg="zlib", vid="CVE-2026-85091")
        entry = self.exc(pkg="zlib", vid="CVE-2026-85091", added=date(2026, 10, 6), expires=date(2026, 11, 2),
                         justification="Alpine zlib fix 1.3.2-r1, no rebuilt image yet; #143.")
        r = self.run_eval("caddy", apk, [entry], ref=self.CADDY, tag_state="unchanged", tag_label="library/caddy:2.11.7-alpine")
        self.assertEqual(r["findings"], [])
        self.assertNotEqual(r["excepted"][0].get("rule"), self.scan.RULE_ITEM4)
        self.assertIn("no rebuilt image", r["excepted"][0]["note"])
        moved = self.run_eval("caddy", apk, [entry], ref=self.CADDY, tag_state="moved")
        self.assertEqual(moved["excepted"], [])

    def test_a_fixed_caddy_go_module_finding_without_an_entry_blocks(self):
        r = self.run_eval("caddy", self.fixed(), [self.exc(vid="CVE-2026-00001")], ref=self.CADDY, tag_state="unchanged")
        self.assertTrue(r["findings"])
        self.assertEqual(r["excepted"], [])

    def test_the_sunset_ends_item_4(self):
        entry = self.exc(added=date(2026, 11, 16), expires=date(2026, 11, 30), justification="#142, approved by Marco 2026-11-16.")
        keeps = lambda today: self.scan._keeps_vendor_binary_exception(entry, "caddy", {"type": "go-module"}, self.CADDY, today)  # noqa: E731
        self.assertTrue(keeps(date(2026, 11, 30)))
        self.assertFalse(keeps(date(2026, 12, 1)))
        self.assertFalse(self.scan._keeps_vendor_binary_exception(entry, "caddy", {"type": "go-module"}, self.CADDY, datetime(2026, 11, 20)),
                         "a datetime is not a date: the rule does not apply")

    def test_sunset_tripwire(self):
        """Red from 2026-12-01 on purpose: remove item 4 (code, tests, ADR) or decide again (ADR amendment and G3)."""
        today = datetime.now(timezone.utc).date()
        self.assertLessEqual(today, self.scan.VENDOR_BINARY_SUNSET,
                             "ADR-0015 item 4 passed its sunset: remove it or decide again")

    def test_the_validator_rejects_every_broken_item_4_entry(self):
        self.assertEqual(self.errors_for(self.exc()), [])
        cases = {
            "15-day span": (self.exc(expires=date(2026, 11, 1)), "more than 14 days after added"),
            "issue missing": (self.exc(justification="Fixed in Go, no Caddy release; approved by Marco 2026-10-17."), "issue value #142"),
            "no approval": (self.exc(justification="Fixed in Go, no Caddy release yet; #142."), "exactly one"),
            "two approvals": (self.exc(justification="#142, approved by Marco 2026-10-17, approved by Marco 2026-10-16."), "exactly one"),
            "not a real date": (self.exc(justification="#142, approved by Marco 2026-02-30."), "exactly one"),
            "approval after added": (self.exc(justification="#142, approved by Marco 2026-10-18."), "approval date is after added"),
            "past approval + 14": (self.exc(added=date(2026, 10, 20), expires=date(2026, 11, 2)), "after the approval date"),
            "after the sunset": (self.exc(added=date(2026, 11, 25), expires=date(2026, 12, 2), justification="#142, approved by Marco 2026-11-25."),
                                 "after the sunset"),
        }
        for name, (entry, message) in cases.items():
            with self.subTest(case=name):
                errors = self.errors_for(entry, today=entry["added_date"])
                self.assertTrue(any("item 4 entry" in e and message in e for e in errors), errors)

    def test_non_item_4_entries_are_not_held_to_item_4(self):
        for entry in (self.exc(image="redis", pkg="zlib", justification="Alpine zlib, no rebuilt image yet, see #143."),
                      self.exc(pkg="xnet", justification="Not Go-shaped, so not item 4; #142.")):
            with self.subTest(package=entry["package"]):
                self.assertFalse(self.scan.is_vendor_binary_entry(entry))
                self.assertFalse([e for e in self.errors_for(entry) if "item 4 entry" in e])

    def test_the_guard_entry_point_returns_expired_entries_and_never_a_partial_list(self):
        good = {k: v for k, v in self.exc().items() if not k.endswith("_date")}
        entries = self.scan.vendor_binary_entries({"exceptions": [good]}, date(2026, 11, 5))
        self.assertEqual([e["id"] for e in entries], ["CVE-2026-78663"], "an expired item-4 entry is still returned")
        bad = {**good, "id": "CVE-2026-00002", "expires": "2026-11-20"}
        with self.assertRaises(ValueError):
            self.scan.vendor_binary_entries({"exceptions": [good, bad]}, date(2026, 10, 17))

    def test_the_step_summary_marks_item_4_apart_from_item_3(self):
        row = {"image": "caddy", "package": "stdlib", "version": "go1.26.8", "id": "CVE-2026-78663", "severity": "Critical",
               "fix": "1.26.9", "status": "excepted", "note": "n4", "rule": self.scan.RULE_ITEM4}
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "summary.md"
            old = os.environ.get("GITHUB_STEP_SUMMARY")
            os.environ["GITHUB_STEP_SUMMARY"] = str(path)
            try:
                self.scan._summary([row, {**row, "image": "redis", "note": "n3", "rule": None}], [])
            finally:
                if old is None:
                    os.environ.pop("GITHUB_STEP_SUMMARY")
                else:
                    os.environ["GITHUB_STEP_SUMMARY"] = old
            text = path.read_text(encoding="utf-8")
        self.assertIn("excepted (ADR-0015 item 4): n4", text)
        self.assertIn("excepted: n3", text)

    def test_the_committed_142_entries_are_caddy_item_4_entries_only(self):
        """M-9: 16 caddy entries (GO-2026-6603 to 6613 and 6617), stdlib or x/net, #142, added 2026-10-17,
        expiring by 2026-10-31, approved, valid."""
        data = json.loads((ROOT / ".github" / "image-scan" / "exceptions.json").read_text(encoding="utf-8"))
        mine = [e for e in data["exceptions"] if e["issue"] == "#142"]
        self.assertEqual(len(mine), 16)
        self.assertEqual({e["image"] for e in mine}, {"caddy"})
        self.assertEqual({e["package"] for e in mine}, {"stdlib", "golang.org/x/net"})
        self.assertEqual(sum(e["package"] == "golang.org/x/net" for e in mine), 5)
        for e in mine:
            with self.subTest(id=e["id"], package=e["package"]):
                self.assertTrue(e["id"].startswith(("CVE-", "GHSA-")))
                self.assertEqual(e["added"], "2026-10-17")
                self.assertLessEqual(e["expires"], "2026-10-31")
                self.assertEqual(self.scan.vendor_binary_approval(e["justification"]), date(2026, 10, 17))
                self.assertTrue(self.scan.is_vendor_binary_entry(e))
        self.assertFalse([e for e in data["exceptions"] if e["image"] == "otelcollector" and self.scan.is_vendor_binary_entry({**e, "image": "caddy"})],
                         "the collector is bumped, so it carries no Go exception")
        errors, _ = self.scan.exception_errors(data, date(2026, 10, 17))
        self.assertEqual(errors, [])


if __name__ == "__main__":
    unittest.main()
