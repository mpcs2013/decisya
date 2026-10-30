"""#222 (ADR-0022 items 1 and 4; G3 of the ADR: M-2, M-5, R-1, Ruling C; G3 of #222: M222-1, M222-2, S-1).

The pull-request modes of .github/scripts/image_scan.py: the mode selection, the delta key, the base
module loaded from the second checkout, the gate-change comparison and the R-1 raw-key check. Nothing
here runs git, Docker or the network: the `_git` and `_run` seams, the tag resolution and the base
module are replaced, and every scanner document is built in the test.
"""
import importlib.util
import io
import json
import os
import re
import shutil
import sys
import tempfile
import types
import unittest
from contextlib import redirect_stdout
from datetime import date
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github" / "workflows" / "ci.yml"
TODAY = date(2026, 10, 30)
DB_BUILT = "2026-10-30T00:00:00Z"


def load_scan():
    path = ROOT / ".github" / "scripts" / "image_scan.py"
    spec = importlib.util.spec_from_file_location("_ci_image_scan_222", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def match(name="libx", vid="CVE-2026-1", severity="High", ptype="apk", version="1.0", fix_state="fixed"):
    return {"vulnerability": {"id": vid, "severity": severity,
                              "fix": {"state": fix_state, "versions": ["1.1"] if fix_state == "fixed" else []}},
            "artifact": {"name": name, "type": ptype, "version": version}, "relatedVulnerabilities": []}


def grype_doc(matches, db_built=DB_BUILT):
    return {"matches": matches, "descriptor": {"db": {"built": db_built}}}


def git_result(returncode=0, stdout=b""):
    return types.SimpleNamespace(returncode=returncode, stdout=stdout, stderr=b"")


class ModeSelectionTests(unittest.TestCase):
    """G3 M-2 mode table and G3 M222-2: every doubt is the full scan."""

    def setUp(self):
        self.scan = load_scan()

    def test_table(self):
        s = self.scan
        rows = [
            ("push", [s.PIN_FILE], True, s.MODE_FULL),
            (None, [s.PIN_FILE], True, s.MODE_FULL),
            ("pull_request", [s.PIN_FILE], False, s.MODE_FULL),          # M222-2: not a merge commit
            ("pull_request", None, True, s.MODE_FULL),                   # unknown base
            ("pull_request", ["README.md"], True, s.MODE_FULL),          # step ran with an empty lane
            ("pull_request", [".github/image-scan/new.txt"], True, s.MODE_FULL),  # unclassified lane path
            ("pull_request", [s.PIN_FILE, ".github/image-scan/x"], True, s.MODE_FULL),
            ("pull_request", [".github/scripts/image_scan.py"], True, s.MODE_GATE),
            ("pull_request", [".github/image-scan/Dockerfile", s.PIN_FILE], True, s.MODE_GATE),
            ("pull_request", [".github/workflows/ci.yml", s.EXCEPTIONS_PATH], True, s.MODE_GATE),
            ("pull_request", [s.EXCEPTIONS_PATH], True, s.MODE_VALIDATOR),
            ("pull_request", [s.EXCEPTIONS_PATH, "docs/x.md"], True, s.MODE_VALIDATOR),
            ("pull_request", [s.EXCEPTIONS_PATH, s.PIN_FILE], True, s.MODE_DELTA),
            ("pull_request", [s.PIN_FILE], True, s.MODE_DELTA),
            ("pull_request", [s.PARITY_FILE], True, s.MODE_DELTA),
            ("pull_request", [".github/release/Dockerfile"], True, s.MODE_DELTA),
        ]
        for event, changed, merge, want in rows:
            with self.subTest(event=event, changed=changed, merge=merge):
                self.assertEqual(s.select_mode(event, changed, merge)[0], want)

    def test_lane_regex_is_ci_ymls_without_all(self):
        text = CI.read_text(encoding="utf-8")
        m = re.search(r"images_changed=\$\(printf '%s\\n' \"\$changed\" \| grep -E '([^']+)'", text)
        self.assertIsNotNone(m, "images_changed regex not found in ci.yml")
        self.assertEqual(self.scan.IMAGES_LANE_RE.pattern, m.group(1).removesuffix("|^ALL$"))

    def test_base_checkout_path_is_ci_ymls(self):
        text = CI.read_text(encoding="utf-8")
        self.assertIn(f"path: {self.scan.BASE_CHECKOUT.name}", text)
        self.assertEqual(self.scan.BASE_CHECKOUT.name, ".image-scan-base")

    def test_changed_files_argv_and_failure(self):
        calls = []
        with mock.patch.object(self.scan, "_git", side_effect=lambda a: calls.append(a) or git_result(0, b"a\0b c\0")):
            self.assertEqual(self.scan.changed_files(), ["a", "b c"])
        self.assertEqual(calls, [["diff", "--name-only", "--no-renames", "-z", "HEAD^1", "HEAD"]])
        for result in (None, git_result(128)):
            with self.subTest(result=result), mock.patch.object(self.scan, "_git", return_value=result):
                self.assertIsNone(self.scan.changed_files())

    def test_is_merge_commit_needs_exactly_two_parents(self):
        def fake(two, three):
            return lambda a: git_result(0 if (two if a[-1] == "HEAD^2^{commit}" else three) else 128)
        cases = [((True, False), True), ((False, False), False), ((True, True), False)]
        for (two, three), want in cases:
            with self.subTest(two=two, three=three), mock.patch.object(self.scan, "_git", side_effect=fake(two, three)):
                self.assertIs(self.scan.is_merge_commit(), want)
        with mock.patch.object(self.scan, "_git", return_value=None):
            self.assertFalse(self.scan.is_merge_commit())

    def test_first_parent_sha_accepts_only_a_full_hash(self):
        for out, want in ((b"a" * 40 + b"\n", "a" * 40), (b"zz\n", None), (b"", None)):
            with self.subTest(out=out), mock.patch.object(self.scan, "_git", return_value=git_result(0, out)):
                self.assertEqual(self.scan.first_parent_sha(), want)


class ChangedAliasTests(unittest.TestCase):
    def setUp(self):
        self.scan = load_scan()
        self.refs = self.scan.parse_targets(self.scan.CONTAINER_IMAGES.read_text(encoding="utf-8"))

    def test_only_aliases_whose_reference_differs(self):
        base = dict(self.refs, caddy=self.refs["caddy"][:-1] + ("0" if self.refs["caddy"][-1] != "0" else "1"))
        self.assertEqual(self.scan.changed_aliases(base, self.refs), ["caddy"])
        self.assertEqual(self.scan.changed_aliases(self.refs, self.refs), [])
        self.assertEqual(sorted(self.scan.changed_aliases({}, self.refs)), sorted(self.refs))

    def test_base_pin_refs_reads_head_parent_through_git(self):
        calls = []
        text = self.scan.CONTAINER_IMAGES.read_bytes()
        with mock.patch.object(self.scan, "_git", side_effect=lambda a: calls.append(a) or git_result(0, text)):
            self.assertEqual(self.scan.base_pin_refs(), self.refs)
        self.assertEqual(calls, [["show", f"HEAD^1:{self.scan.PIN_FILE}"]])
        for result in (None, git_result(128), git_result(0, b"not a pin file")):
            with self.subTest(result=result), mock.patch.object(self.scan, "_git", return_value=result):
                self.assertIsNone(self.scan.base_pin_refs())


class DeltaKeyTests(unittest.TestCase):
    """G3 M-2: the exact key (alias, name, type, id), no version or fix state; a rise counts as new."""

    def setUp(self):
        self.scan = load_scan()

    def head(self, *matches):
        return self.scan.evaluate(grype_doc(list(matches)), "postgres", [], TODAY)["findings"]

    def ranks(self, *matches):
        return {self.scan.finding_key("postgres", m): self.scan.rank_of(m["vulnerability"]["severity"]) for m in matches}

    def test_key_ignores_version_and_fix_state(self):
        a = match(version="1.0", fix_state="fixed")
        b = match(version="2.0", fix_state="not-fixed")
        self.assertEqual(self.scan.finding_key("postgres", a), self.scan.finding_key("postgres", b))
        self.assertEqual(self.scan.new_findings(self.head(b), self.ranks(a)), [])

    def test_rename_type_change_new_id_and_severity_rise_are_new(self):
        base = self.ranks(match())
        for m in (match(name="liby"), match(ptype="deb"), match(vid="CVE-2026-2"), match(severity="Critical")):
            with self.subTest(m=m):
                self.assertEqual(len(self.scan.new_findings(self.head(m), base)), 1)

    def test_a_lower_severity_at_the_head_is_not_new(self):
        self.assertEqual(self.scan.new_findings(self.head(match(severity="High")), self.ranks(match(severity="Critical"))), [])

    def test_no_ranks_means_every_finding_is_new(self):
        self.assertEqual(len(self.scan.new_findings(self.head(match(), match(vid="CVE-2026-2")), None)), 2)

    def test_related_ids_never_make_a_key_match(self):
        m = match(vid="GHSA-2222-3333-4444")
        m["relatedVulnerabilities"] = [{"id": "CVE-2026-1"}]
        self.assertEqual(len(self.scan.new_findings(self.head(m), self.ranks(match(vid="CVE-2026-1")))), 1)

    def test_rank_of(self):
        self.assertEqual([self.scan.rank_of(x) for x in ("Critical", "High", "Elevated(unknown)", "Medium", None)],
                         [2, 1, 1, None, None])

    def test_same_repository(self):
        ref = "docker.io/library/postgres:18-alpine@sha256:" + "a" * 64
        self.assertTrue(self.scan.same_repository(ref.replace("a" * 64, "b" * 64), ref))
        self.assertFalse(self.scan.same_repository("ghcr.io/library/postgres:18-alpine@sha256:" + "a" * 64, ref))
        self.assertFalse(self.scan.same_repository(None, ref))

    def test_base_ranks_use_the_base_modules_evaluate_raw_without_exceptions(self):
        seen = []

        def evaluate(doc, alias, exceptions, today):
            seen.append(exceptions)
            return self.scan.evaluate(doc, alias, exceptions, today)
        base = types.SimpleNamespace(evaluate=evaluate)
        ranks = self.scan.base_ranks(base, grype_doc([match(), match(vid="CVE-2026-9", severity="Low")]), "postgres", TODAY)
        self.assertEqual(ranks, {("postgres", "libx", "apk", "CVE-2026-1"): 1})
        self.assertTrue(seen and all(e == [] for e in seen))


class BaseLoadingTests(unittest.TestCase):
    """G3 M-5 and M222-1: main's module by file path from the second checkout, never registered, and
    anything it raises (SystemExit included) means "no usable base", never "no findings"."""

    def setUp(self):
        self.scan = load_scan()
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.base = self.tmp / ".image-scan-base"
        (self.base / ".github" / "scripts").mkdir(parents=True)
        self.module_path = self.base / ".github" / "scripts" / "image_scan.py"
        self.module_path.write_text((ROOT / ".github" / "scripts" / "image_scan.py").read_text(encoding="utf-8"), encoding="utf-8")
        mock.patch.object(self.scan, "BASE_CHECKOUT", self.base).start()
        self.addCleanup(mock.patch.stopall)
        self.parent = "c" * 40

    def git(self, checkout_head):
        def fake(args):
            if args[:1] == ["-C"]:
                return git_result(0, checkout_head.encode() + b"\n")
            return git_result(0, self.parent.encode() + b"\n")
        return fake

    def load(self, checkout_head=None):
        log = []
        with mock.patch.object(self.scan, "_git", side_effect=self.git(checkout_head or self.parent)):
            return self.scan.load_base_module(log), log

    def test_loads_and_never_registers_under_a_module_name(self):
        before = set(sys.modules)
        module, log = self.load()
        self.assertIsNotNone(module, log)
        self.assertEqual(module.__name__, self.scan.BASE_MODULE_NAME)
        self.assertNotIn(self.scan.BASE_MODULE_NAME, set(sys.modules) - before)
        self.assertNotIn("image_scan", set(sys.modules) - before)

    def test_refusals(self):
        cases = {
            "missing": lambda: self.module_path.unlink(),
            "syntax": lambda: self.module_path.write_text("def (:\n", encoding="utf-8"),
            "exit at import": lambda: self.module_path.write_text("import sys\nsys.exit(0)\n", encoding="utf-8"),
            "raise at import": lambda: self.module_path.write_text("raise RuntimeError('x')\n", encoding="utf-8"),
            "missing api": lambda: self.module_path.write_text("CONTAINER_IMAGES = None\n", encoding="utf-8"),
        }
        original = self.module_path.read_text(encoding="utf-8")
        for name, damage in cases.items():
            with self.subTest(name):
                self.module_path.write_text(original, encoding="utf-8")
                damage()
                module, log = self.load()
                self.assertIsNone(module)
                self.assertTrue(log)

    def test_a_checkout_that_is_not_the_first_parent_is_refused(self):
        module, log = self.load(checkout_head="d" * 40)
        self.assertIsNone(module)
        self.assertIn("first parent", " ".join(log))

    @unittest.skipUnless(hasattr(os, "symlink"), "needs symlinks")
    def test_a_link_on_the_path_is_refused(self):
        real = self.tmp / "real.py"
        real.write_text(self.module_path.read_text(encoding="utf-8"), encoding="utf-8")
        self.module_path.unlink()
        try:
            os.symlink(real, self.module_path)
        except OSError:
            self.skipTest("symlinks not permitted here")
        module, log = self.load()
        self.assertIsNone(module)
        self.assertIn("link", " ".join(log))

    def test_scan_with_contains_systemexit_from_the_module(self):
        def boom(*a, **k):
            raise SystemExit(0)
        mod = types.SimpleNamespace(scan_argv=lambda *a: ["x"], _run=boom, check_provenance=None, read_sbom=None)
        doc, why = self.scan.scan_with(mod, "s", "r", "postgres", str(self.tmp), str(self.tmp), [])
        self.assertIsNone(doc)
        self.assertIn("SystemExit", why[0])


class RunHarness:
    """run() end to end with every seam replaced. head_docs/base_docs map a reference to the scanner
    document each side returns; db_update counts the database updates (Ruling C: exactly one)."""

    def __init__(self, test, scan, *, mode_changed, base_refs=None, base_scanner_same=True, head_docs, base_docs,
                 base=True, base_eval=None, base_run=None):
        self.test, self.scan = test, scan
        self.tmp = Path(tempfile.mkdtemp())
        test.addCleanup(shutil.rmtree, self.tmp, True)
        self.head_refs = scan.parse_targets(scan.CONTAINER_IMAGES.read_text(encoding="utf-8"))
        self.base_refs = base_refs or dict(self.head_refs)
        self.scanner = scan.parse_scanner_ref(scan.SCANNER_DOCKERFILE.read_text(encoding="utf-8"))
        self.base_scanner = self.scanner if base_scanner_same else self.scanner.replace("@sha256:", "@sha256:0")[:-1]
        self.head_docs, self.base_docs = head_docs, base_docs
        self.db_updates = 0
        self.base_scans = []
        exceptions = self.tmp / "exceptions.json"
        exceptions.write_text(json.dumps({"exceptions": []}), encoding="utf-8")
        base_pins = self.tmp / "ContainerImages.cs"
        text = scan.CONTAINER_IMAGES.read_text(encoding="utf-8")
        for alias, ref in self.head_refs.items():
            if self.base_refs.get(alias) != ref:
                text = text.replace(ref.rsplit(":", 1)[1], self.base_refs[alias].rsplit(":", 1)[1])
        base_pins.write_text(text, encoding="utf-8")
        self.base_pin_text = text
        base_docker = self.tmp / "Dockerfile"
        base_docker.write_text(f"FROM {self.base_scanner} AS grype\n", encoding="utf-8")
        self.base = None
        if base:
            self.base = types.SimpleNamespace(
                CONTAINER_IMAGES=base_pins, SCANNER_DOCKERFILE=base_docker,
                parse_targets=scan.parse_targets, parse_scanner_ref=scan.parse_scanner_ref, scan_argv=scan.scan_argv,
                read_sbom=lambda d: None, check_provenance=lambda *a, **k: [],
                evaluate=base_eval or scan.evaluate, _run=base_run or self.run_base)
        self.patches = [
            mock.patch.object(scan, "EXCEPTIONS_FILE", exceptions),
            mock.patch.object(scan, "_utc_today", return_value=TODAY),
            mock.patch.object(scan, "_git", side_effect=self.git),
            mock.patch.object(scan, "changed_files", return_value=mode_changed),
            mock.patch.object(scan, "is_merge_commit", return_value=True),
            mock.patch.object(scan, "_run", side_effect=self.run_head),
            mock.patch.object(scan, "check_provenance", return_value=[]),
            mock.patch.object(scan, "read_sbom", return_value=None),
            mock.patch.object(scan, "resolve_tag_state", return_value=scan.TAG_UNCHANGED),
            mock.patch.object(scan, "load_base_module", return_value=self.base),
            mock.patch.dict(os.environ, {"GITHUB_EVENT_NAME": "pull_request", "RUNNER_TEMP": str(self.tmp)}),
        ]

    def git(self, args):
        if args[:1] == ["show"]:
            return git_result(0, self.base_pin_text.encode())
        return git_result(0, b"")

    def _doc_for(self, argv, docs):
        for ref, doc in docs.items():
            if f"registry:{ref}" in argv:
                return types.SimpleNamespace(returncode=0, stdout=json.dumps(doc), stderr="")
        return types.SimpleNamespace(returncode=0, stdout=json.dumps(grype_doc([])), stderr="")

    def run_head(self, argv, log):
        if argv[-2:] == ["db", "update"]:
            self.db_updates += 1
            return types.SimpleNamespace(returncode=0, stdout="", stderr="")
        return self._doc_for(argv, self.head_docs)

    def run_base(self, argv, log):
        self.base_scans.append(argv)
        return self._doc_for(argv, self.base_docs)

    def run(self):
        for p in self.patches:
            p.start()
        try:
            out = io.StringIO()
            with redirect_stdout(out):
                code = self.scan.run()
            return code, out.getvalue()
        finally:
            for p in reversed(self.patches):
                p.stop()


class GateChangeTests(unittest.TestCase):
    """ADR-0022 item 1 (gate-change comparison) with G3 S-1 and Ruling C."""

    def setUp(self):
        self.scan = load_scan()
        self.refs = self.scan.parse_targets(self.scan.CONTAINER_IMAGES.read_text(encoding="utf-8"))
        self.kc = self.refs["keycloak"]

    def harness(self, head_matches, base_matches, **kw):
        return RunHarness(self, self.scan, mode_changed=[".github/workflows/ci.yml"],
                          head_docs={self.kc: grype_doc(head_matches)}, base_docs={self.kc: grype_doc(base_matches)}, **kw)

    def test_a_finding_present_at_base_and_head_passes(self):
        h = self.harness([match(name="guava", ptype="java-archive")], [match(name="guava", ptype="java-archive")])
        code, out = h.run()
        self.assertEqual(code, 0, out)
        self.assertIn("mode: gate-change", out)
        self.assertEqual(h.db_updates, 1)
        self.assertEqual(len(h.base_scans), len(self.refs))  # every alias at the base

    def test_a_finding_new_at_the_head_fails(self):
        code, out = self.harness([match(), match(vid="CVE-2026-7")], [match()]).run()
        self.assertEqual(code, 1)
        self.assertIn("CVE-2026-7", out)

    def test_no_usable_base_fails_the_gate_change(self):
        code, out = self.harness([match()], [match()], base=False).run()
        self.assertEqual(code, 1)
        self.assertIn("a gate change needs the base comparison", out)

    def test_m222_1_a_base_systemexit_with_a_high_at_the_head_is_exit_1(self):
        def boom(*a, **k):
            raise SystemExit(0)
        for kw in ({"base_eval": boom}, {"base_run": boom}):
            with self.subTest(kw=list(kw)):
                code, out = self.harness([match()], [match()], **kw).run()
                self.assertEqual(code, 1, out)

    def test_s1_an_untrusted_base_with_the_same_scanner_and_reference_fails(self):
        def bad_run(argv, log):
            return types.SimpleNamespace(returncode=3, stdout="", stderr="")
        code, out = self.harness([], [], base_run=bad_run).run()
        self.assertEqual(code, 1)
        self.assertIn("R-1 cannot be evaluated", out)

    def test_ruling_c_another_database_build_at_the_base_is_unavailable(self):
        h = RunHarness(self, self.scan, mode_changed=[".github/workflows/ci.yml"],
                       head_docs={self.kc: grype_doc([match()])}, base_docs={self.kc: grype_doc([match()], db_built="2026-10-29T00:00:00Z")})
        code, out = h.run()
        self.assertEqual(code, 1)
        self.assertIn("another database build", out)


class RawKeyCheckTests(unittest.TestCase):
    """R-1 (ADR-0022 item 1, dated line 2026-10-30): same scanner and same reference, so a raw base key
    missing at the head fails; with another scanner it is only logged."""

    def setUp(self):
        self.scan = load_scan()
        self.refs = self.scan.parse_targets(self.scan.CONTAINER_IMAGES.read_text(encoding="utf-8"))
        self.kc = self.refs["keycloak"]

    def test_a_low_match_vanishing_at_the_head_fails(self):
        h = RunHarness(self, self.scan, mode_changed=[".github/scripts/image_scan.py"],
                       head_docs={self.kc: grype_doc([])}, base_docs={self.kc: grype_doc([match(severity="Low")])})
        code, out = h.run()
        self.assertEqual(code, 1)
        self.assertIn("R-1", out)

    def test_with_another_scanner_a_vanished_match_is_logged_only(self):
        h = RunHarness(self, self.scan, mode_changed=[".github/image-scan/Dockerfile"], base_scanner_same=False,
                       head_docs={self.kc: grype_doc([])}, base_docs={self.kc: grype_doc([match(severity="Low")])})
        code, out = h.run()
        self.assertEqual(code, 0, out)
        self.assertIn("logged only", out)


class DeltaRunTests(unittest.TestCase):
    """ADR-0022 item 1: only the changed aliases are scanned; findings already at the base pass."""

    def setUp(self):
        self.scan = load_scan()
        self.refs = self.scan.parse_targets(self.scan.CONTAINER_IMAGES.read_text(encoding="utf-8"))
        old = self.refs["postgres"]
        self.base_refs = dict(self.refs, postgres=old[:-4] + ("0000" if not old.endswith("0000") else "1111"))

    def harness(self, head, base):
        return RunHarness(self, self.scan, mode_changed=[self.scan.PIN_FILE], base_refs=self.base_refs,
                          head_docs={self.refs["postgres"]: grype_doc(head)},
                          base_docs={self.base_refs["postgres"]: grype_doc(base)})

    def test_only_the_changed_alias_is_scanned_and_a_known_finding_passes(self):
        h = self.harness([match()], [match()])
        code, out = h.run()
        self.assertEqual(code, 0, out)
        self.assertIn("mode: delta", out)
        self.assertEqual(len(h.base_scans), 1)
        self.assertIn("scanning postgres", out)
        self.assertNotIn("scanning keycloak", out)

    def test_a_new_finding_in_the_changed_alias_fails(self):
        code, out = self.harness([match(), match(vid="CVE-2026-8")], [match()]).run()
        self.assertEqual(code, 1)

    def test_validator_only_scans_nothing(self):
        h = RunHarness(self, self.scan, mode_changed=[self.scan.EXCEPTIONS_PATH], head_docs={}, base_docs={})
        code, out = h.run()
        self.assertEqual(code, 0, out)
        self.assertIn("mode: validator", out)
        self.assertEqual(h.db_updates, 0)


class EvaluateTypeTests(unittest.TestCase):
    def test_evaluate_reports_the_package_type(self):
        scan = load_scan()
        result = scan.evaluate(grype_doc([match(ptype="java-archive")]), "keycloak", [], TODAY)
        self.assertEqual(result["findings"][0]["type"], "java-archive")


if __name__ == "__main__":
    unittest.main()
