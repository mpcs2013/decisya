"""The #28 CI gates cannot pass without noticing (G3 MUSTs G4-28-03 to 05, S-02).

- Every `run:` uses bash with pipefail (the old `Vulnerable packages` step failed open through `tee`).
- The `images` lane is true for each trigger path and false for docs-only, and its output is wired
  to the scan step exactly (a misspelt output would skip the required scan and stay green).
- The exceptions file is bounded in time and scope; the scan proves it scanned the pinned digest.
- The dependency canary's local-only weakening option never appears in CI.
"""
import contextlib
import importlib.util
import io
import re
import shutil
import subprocess
import tempfile
import unittest
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github" / "workflows" / "ci.yml"
BASH = shutil.which("bash")


def load(name: str):
    path = ROOT / ".github" / "scripts" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(f"_ci_{name}", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def ci_text() -> str:
    return CI.read_text(encoding="utf-8")


class ShellTests(unittest.TestCase):
    def test_workflow_default_shell_is_bash(self):
        self.assertRegex(ci_text(), r"(?m)^defaults:\n  run:\n    shell: bash\s*$")

    def test_no_job_or_step_overrides_the_shell(self):
        lines = [l for l in ci_text().splitlines() if re.match(r"^\s+shell:", l)]
        self.assertEqual(lines, ["    shell: bash"], "only the workflow-level default may set shell:")


def lanes_block() -> str:
    m = re.search(r"# lanes: begin\n(.*?)\n\s*# lanes: end", ci_text(), re.DOTALL)
    assert m, "lanes markers not found in ci.yml"
    return "\n".join(line.strip() for line in m.group(1).splitlines())


@unittest.skipUnless(BASH, "needs bash")
class ImagesLaneTests(unittest.TestCase):
    def images(self, changed: str) -> str:
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            script = f'changed=$(printf "%s" "{changed}")\nGITHUB_OUTPUT="{out.as_posix()}"\n' + lanes_block()
            subprocess.run([BASH, "-c", script], cwd=d, check=True, capture_output=True, text=True)
            values = dict(l.split("=", 1) for l in out.read_text().splitlines())
        return values["images"]

    def test_true_for_every_trigger_path(self):
        for path in ("ALL", "src/Decisya.AppHost/ContainerImages.cs", ".devcontainer/engine/images.Dockerfile",
                     ".github/image-scan/exceptions.json", ".github/scripts/image_scan.py", ".github/workflows/ci.yml"):
            with self.subTest(path=path):
                self.assertEqual(self.images(path), "true")

    def test_false_for_docs_and_unrelated_code(self):
        for path in ("docs/x.md", "src/Decisya.Bff/Program.cs", ".github/scripts/dependency_canary.py"):
            with self.subTest(path=path):
                self.assertEqual(self.images(path), "false")

    def test_canary_script_change_runs_the_npm_canary(self):
        # G6 F-03: the npm canary runs in the SPA lane; a canary-only PR must reach it
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "src" / "Decisya.Web").mkdir(parents=True)
            (Path(d) / "src" / "Decisya.Web" / "package.json").write_text("{}")
            out = Path(d) / "out"
            script = f'changed=$(printf "%s" ".github/scripts/dependency_canary.py")\nGITHUB_OUTPUT="{out.as_posix()}"\n' + lanes_block()
            subprocess.run([BASH, "-c", script], cwd=d, check=True, capture_output=True, text=True)
            values = dict(l.split("=", 1) for l in out.read_text().splitlines())
        self.assertEqual(values["spa"], "true")


class ImagesWiringTests(unittest.TestCase):
    def test_output_detect_and_scan_if_are_wired(self):
        text = ci_text()
        self.assertIn("images: ${{ steps.detect.outputs.images }}", text)
        self.assertIn('echo "images=$images" >> "$GITHUB_OUTPUT"', text)
        job = text.split("\n  image-scan:\n", 1)
        self.assertEqual(len(job), 2, "image-scan job not found")
        body = re.split(r"\n  [a-z][a-z0-9-]*:\n", "\n" + job[1])[0]
        self.assertIn("needs: changes", body)
        self.assertRegex(body, r"if: needs\.changes\.outputs\.images == 'true'\n\s+run: python3 \.github/scripts/image_scan\.py")
        self.assertNotRegex(body.split("steps:", 1)[0], r"(?m)^    if:", "the required job must not have a job-level if")

    def test_canary_weakening_never_runs_in_ci(self):
        self.assertNotIn("--weaken-for-evidence", ci_text())
        self.assertIn("dependency_canary.py nuget", ci_text())
        self.assertIn("dependency_canary.py npm", ci_text())


class ExceptionsTests(unittest.TestCase):
    TODAY = date(2026, 10, 3)

    def setUp(self):
        self.scan = load("image_scan")

    def entry(self, **kw):
        e = {"id": "CVE-2026-12345", "image": "redis", "package": "libssl3",
             "justification": "not reachable: TLS is terminated at the edge", "issue": "#83",
             "added": "2026-10-01", "expires": "2026-12-01"}
        e.update(kw)
        return e

    def rejects(self, data):
        # G6 F-02: the rejection prints a real ::error:: annotation; keep it out of the CI job log
        with self.assertRaises(SystemExit) as cm, contextlib.redirect_stdout(io.StringIO()):
            self.scan.validate_exceptions(data, today=self.TODAY)
        self.assertEqual(cm.exception.code, 2)

    def test_valid_entry_is_accepted(self):
        self.assertEqual(len(self.scan.validate_exceptions({"exceptions": [self.entry()]}, today=self.TODAY)), 1)

    def test_bad_entries_are_rejected(self):
        cases = {
            "future added": self.entry(added="2026-10-04", expires="2026-11-01"),
            "expires before added": self.entry(expires="2026-09-30"),
            "over 90 days": self.entry(expires="2027-01-01"),
            "non-strict date": self.entry(added="2026-10-1"),
            "glob package": self.entry(package="libssl*"),
            "empty package": self.entry(package=""),
            "bad id": self.entry(id="CVE-26-1"),
            "bad issue": self.entry(issue="83"),
            "short justification": self.entry(justification="n/a"),
        }
        for name, e in cases.items():
            with self.subTest(case=name):
                self.rejects({"exceptions": [e]})
        extra = self.entry()
        extra["note"] = "x"
        self.rejects({"exceptions": [extra]})
        self.rejects({"exceptions": [self.entry(), self.entry()]})
        self.rejects({"exceptions": [], "other": 1})

    def test_committed_file_is_valid(self):
        import json
        data = json.loads((ROOT / ".github" / "image-scan" / "exceptions.json").read_text(encoding="utf-8"))
        self.scan.validate_exceptions(data)


class ProvenanceTests(unittest.TestCase):
    REF = "docker.io/library/redis:8.2.1-alpine@sha256:" + "a" * 64

    def setUp(self):
        self.scan = load("image_scan")

    def doc(self, **kw):
        d = {"matches": [], "source": {"type": "image", "target": {"userInput": self.REF, "layers": [{"digest": "x"}]}},
             "distro": {"name": "alpine"}, "descriptor": {"db": {"built": "2026-10-02T00:00:00Z"}}}
        for k, v in kw.items():
            d[k] = v
        return d

    def test_good_scan_is_trusted(self):
        self.assertEqual(self.scan.check_provenance(self.doc(), self.REF), [])

    def test_untrusted_scans_are_flagged(self):
        other = {"type": "image", "target": {"userInput": self.REF.split("@")[0], "layers": [{"digest": "x"}]}}
        empty = {"type": "image", "target": {"userInput": self.REF, "layers": []}}
        for name, doc in {"tag not digest": self.doc(source=other), "no layers": self.doc(source=empty),
                          "no distro": self.doc(distro={}), "no db": self.doc(descriptor={}),
                          "no matches": {"source": {}}}.items():
            with self.subTest(case=name):
                self.assertTrue(self.scan.check_provenance(doc, self.REF))

    def test_scanner_environment_is_exact_and_keeps_the_db_age_check(self):
        self.assertEqual(set(self.scan.SCAN_ENV), {"GRYPE_DB_CACHE_DIR", "GRYPE_DB_AUTO_UPDATE", "GRYPE_CHECK_FOR_APP_UPDATE"})
        self.assertEqual(set(self.scan.DB_UPDATE_ENV), {"GRYPE_DB_CACHE_DIR"})
        source = (ROOT / ".github" / "scripts" / "image_scan.py").read_text(encoding="utf-8")
        for name in ("GRYPE_DB_VALIDATE_AGE", "GRYPE_DB_MAX_ALLOWED_BUILT_AGE"):
            self.assertNotRegex(source, rf"[\"']{name}[\"']\s*:", f"{name} must never be set")


class EvaluateTests(unittest.TestCase):
    """G6 F-02: the scan policy itself (severity, S-05 elevation, exact exception matching, expiry)."""

    TODAY = date(2026, 10, 3)

    def setUp(self):
        self.scan = load("image_scan")

    @staticmethod
    def match(sev, pkg="libssl3", vid="CVE-2026-1", score=None, related=None):
        v = {"id": vid, "severity": sev, "fix": {"state": "not-fixed"}}
        if score is not None:
            v["cvss"] = [{"metrics": {"baseScore": score}}]
        return {"vulnerability": v, "artifact": {"name": pkg, "version": "1"}, "relatedVulnerabilities": related or []}

    def exc(self, **kw):
        e = {"image": "redis", "package": "libssl3", "id": "CVE-2026-1", "expires_date": date(2026, 10, 3)}
        e.update(kw)
        return e

    def run_eval(self, matches, exceptions=()):
        return self.scan.evaluate({"matches": matches}, "redis", list(exceptions), self.TODAY)

    def test_severity_policy(self):
        r = self.run_eval([self.match("High"), self.match("Critical", vid="CVE-2026-2"), self.match("Medium", vid="CVE-2026-3")])
        self.assertEqual(sorted(f["id"] for f in r["findings"]), ["CVE-2026-1", "CVE-2026-2"])

    def test_unknown_with_high_cvss_is_elevated(self):
        self.assertEqual(len(self.run_eval([self.match("Unknown", score=7.5)])["findings"]), 1)
        self.assertEqual(len(self.run_eval([self.match("Negligible", score=7.0)])["findings"]), 1)
        self.assertEqual(self.run_eval([self.match("Unknown", score=6.9)])["findings"], [])
        rel = [{"id": "GHSA-xxxx", "cvss": [{"metrics": {"baseScore": 9.1}}]}]
        self.assertEqual(len(self.run_eval([self.match("Unknown", related=rel)])["findings"]), 1)

    def test_exception_matches_exactly(self):
        r = self.run_eval([self.match("High")], [self.exc()])
        self.assertEqual((r["findings"], len(r["excepted"])), ([], 1))
        for other in (self.exc(package="libssl"), self.exc(image="keycloak"), self.exc(id="CVE-2026-9")):
            with self.subTest(exception=other):
                self.assertEqual(len(self.run_eval([self.match("High")], [other])["findings"]), 1)

    def test_exception_matches_a_related_id(self):
        r = self.run_eval([self.match("High", vid="GHSA-aaaa", related=[{"id": "CVE-2026-1"}])], [self.exc()])
        self.assertEqual(r["findings"], [])

    def test_exception_ends_when_upstream_ships_a_fix(self):
        # G6 D-2: an active exception must not hide a finding that now has a fix (bump the image instead)
        fixed = self.match("High")
        fixed["vulnerability"]["fix"] = {"state": "fixed", "versions": ["1.3.3-r0"]}
        r = self.run_eval([fixed], [self.exc()])
        self.assertEqual((len(r["findings"]), r["excepted"]), (1, []))

    def test_expiry_boundary(self):
        self.assertEqual(self.run_eval([self.match("High")], [self.exc(expires_date=self.TODAY)])["findings"], [])
        expired = self.exc(expires_date=date(2026, 10, 2))
        self.assertEqual(len(self.run_eval([self.match("High")], [expired])["findings"]), 1)


class OutputHygieneTests(unittest.TestCase):
    """G6 F-02 / G3 S-04: scanner text cannot become a workflow command or table markup."""

    def setUp(self):
        self.scan = load("image_scan")

    def test_emit_protected_wraps_hostile_lines(self):
        buf = io.StringIO()
        self.scan.emit_protected(["::error::x", "pkg\n::warning::y"], out=buf)
        lines = buf.getvalue().splitlines()
        stop = re.fullmatch(r"::stop-commands::([0-9a-f]{32})", lines[0])
        self.assertTrue(stop)
        self.assertEqual(lines[-1], f"::{stop.group(1)}::")
        self.assertEqual(len(lines), 4, "a newline in scanner text must not start a new log line")
        self.assertIn("::error::x", lines[1:-1])

    def test_md_cell_escapes_markup_and_controls(self):
        cell = self.scan.md_cell("<b>|[x](y)`\\\n")
        for raw in ("<", ">", "|", "[", "]", "`", "\n"):
            self.assertNotIn(raw, cell)


class CanaryVerdictTests(unittest.TestCase):
    def setUp(self):
        self.canary = load("dependency_canary")

    def test_real_npm_gate_matches_the_canary_level(self):
        # G6 F-04: the canary proves the level the real SPA step uses; weakening one alone fails here
        self.assertIn(f"npm audit {self.canary.NPM_AUDIT_LEVEL}\n", ci_text())
        self.assertEqual(self.canary.NPM_AUDIT_LEVEL, "--audit-level=high")

    def test_nuget_verdicts(self):
        ok, _ = self.canary.judge_nuget(1, "error NU1903: Warning As Error: Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability")
        self.assertTrue(ok)
        for code, out in ((0, ""), (1, "error MSB4019: something else"),
                          (1, "error NU1903: Warning As Error: Package 'Other.Lib' 1.0.0 has a known high severity vulnerability")):
            with self.subTest(out=out):
                self.assertFalse(self.canary.judge_nuget(code, out)[0])

    def test_npm_verdicts(self):
        self.assertTrue(self.canary.judge_npm(1, '{"vulnerabilities": {"lodash": {"severity": "high"}}}')[0])
        for code, out in ((0, '{"vulnerabilities": {"lodash": {"severity": "high"}}}'), (1, "not json"),
                          (1, '{"vulnerabilities": {"lodash": {"severity": "low"}}}')):
            with self.subTest(out=out):
                self.assertFalse(self.canary.judge_npm(code, out)[0])


if __name__ == "__main__":
    unittest.main()
