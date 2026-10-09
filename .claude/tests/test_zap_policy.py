"""The ZAP baseline verdict (#123, G3 G4-123-05; ADR-0019): every verdict fails closed.

zap_policy.py decides pass or fail from ZAP's JSON report, the URL list the hook writes, ZAP's exit code
and .github/zap/exceptions.json. No test here starts Docker or ZAP.
"""
import contextlib
import importlib.util
import io
import json
import os
import tempfile
import unittest
from datetime import date
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
TODAY = date(2026, 10, 9)
BFF = "https://localhost:7200"


def load():
    spec = importlib.util.spec_from_file_location("_ci_zap_policy", ROOT / ".github" / "scripts" / "zap_policy.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def report(*alerts, site=BFF):
    return {"site": [{"@name": site, "alerts": list(alerts)}]}


def alert(plugin="10020", risk="2", confidence="2", uris=(BFF + "/",)):
    return {"pluginid": plugin, "name": f"alert {plugin}", "riskcode": risk, "confidence": confidence,
            "instances": [{"uri": u} for u in uris]}


GOOD_URLS = [BFF, BFF + "/bff/me", BFF + "/assets/app.js"]


def entry(**kw):
    e = {"pluginId": "10020", "path": "/bff/me", "justification": "accepted for a reason long enough",
         "issue": "#123", "added": "2026-10-01", "expires": "2026-12-01"}
    e.update(kw)
    return e


class ExitCodeAndUrlTests(unittest.TestCase):
    def setUp(self):
        self.p = load()

    def problems(self, zap_exit=0, urls=GOOD_URLS, rep=None):
        return self.p.evaluate(rep if rep is not None else report(), urls, zap_exit, [], TODAY)["problems"]

    def test_only_exit_codes_0_1_2_are_trusted(self):
        for code in (0, 1, 2):
            with self.subTest(code=code):
                self.assertEqual(self.problems(zap_exit=code), [])
        for code in (3, 125, 126, 127, 137, None, -1):
            with self.subTest(code=code):
                self.assertTrue(self.problems(zap_exit=code))

    def test_the_url_list_must_exist_and_cover_root_and_bff_me(self):
        for name, urls in {"none": None, "empty": [], "no root": [BFF + "/bff/me"], "no me": [BFF + "/"],
                           "foreign": GOOD_URLS + ["https://localhost:8080/admin/"]}.items():
            with self.subTest(case=name):
                self.assertTrue(self.problems(urls=urls))
        self.assertEqual(self.problems(urls=[BFF, BFF + "/bff/me"]), [], "a bare origin counts as /")

    def test_origins_are_parsed_not_prefix_matched(self):
        bad = ["https://localhost:72000/", "https://localhost:7200.evil/", "https://localhost:7200@evil/",
               "http://localhost:7200/", "https://localhost/", "https://evil/", "", None, 7200]
        for url in bad:
            with self.subTest(url=url):
                self.assertFalse(self.p.is_bff_origin(url))
        self.assertTrue(self.p.is_bff_origin(BFF + "/x?y=1"))

    def test_a_foreign_site_or_instance_is_a_problem(self):
        self.assertTrue(self.problems(rep=report(site="https://localhost:8080")))
        self.assertTrue(self.problems(rep=report(alert(uris=("https://localhost:8080/realms/x",)))))

    def test_a_report_without_sites_or_an_alert_without_instances_fails_closed(self):
        self.assertTrue(self.problems(rep={}))
        no_instances = {"pluginid": "1", "riskcode": "3", "confidence": "2", "instances": []}
        self.assertTrue(self.problems(rep=report(no_instances)))


class RiskTests(unittest.TestCase):
    def setUp(self):
        self.p = load()

    def test_unknown_or_missing_risk_counts_as_high(self):
        self.assertEqual(self.p.parse_risk("2"), 2)
        for value in (None, "x", "9", "-1", True):
            with self.subTest(value=value):
                self.assertEqual(self.p.parse_risk(value), 3)

    def test_medium_and_high_fail_low_and_info_are_only_reported(self):
        result = self.p.evaluate(report(alert(risk="2"), alert(plugin="2", risk="3"), alert(plugin="3", risk="1"),
                                        alert(plugin="4", risk="0")), GOOD_URLS, 0, [], TODAY)
        self.assertEqual({f["pluginId"] for f in result["findings"]}, {"10020", "2"})
        self.assertEqual({r["pluginId"] for r in result["reported"]}, {"3", "4"})

    def test_only_confidence_zero_is_dropped(self):
        dropped = self.p.evaluate(report(alert(confidence="0")), GOOD_URLS, 0, [], TODAY)
        kept = self.p.evaluate(report(alert(confidence=None)), GOOD_URLS, 0, [], TODAY)
        self.assertEqual(dropped["findings"], [])
        self.assertTrue(kept["findings"])


class ExceptionTests(unittest.TestCase):
    def setUp(self):
        self.p = load()

    def valid(self, *entries):
        errors, ok = self.p.exception_errors({"exceptions": list(entries)}, TODAY)
        self.assertEqual(errors, [])
        return ok

    def test_bad_entries_are_rejected(self):
        cases = {
            "unknown key": {**entry(), "note": "x"},
            "missing key": {k: v for k, v in entry().items() if k != "issue"},
            "non-string": entry(pluginId=10020),
            "plugin not digits": entry(pluginId="abc"),
            "root path": entry(path="/"),
            "query in path": entry(path="/bff/me?x=1"),
            "glob in path": entry(path="/bff/*"),
            "short justification": entry(justification="too short"),
            "bad issue": entry(issue="123"),
            "future added": entry(added="2026-10-10"),
            "expires before added": entry(expires="2026-09-30"),
            "over 90 days": entry(expires="2027-01-01"),
            "bad date": entry(added="2026-02-30"),
        }
        for name, e in cases.items():
            with self.subTest(case=name):
                errors, _ = self.p.exception_errors({"exceptions": [e]}, TODAY)
                self.assertTrue(errors)
        errors, _ = self.p.exception_errors({"exceptions": [entry(), entry()]}, TODAY)
        self.assertTrue(errors, "a duplicate (pluginId, path) is rejected")
        errors, _ = self.p.exception_errors([], TODAY)
        self.assertTrue(errors, "the top level must be an object")

    def test_an_exact_path_covers_only_that_path_and_a_prefix_covers_below_it(self):
        exact = self.valid(entry(path="/bff/me"))
        prefix = self.valid(entry(path="/assets/"))
        on_me = report(alert(uris=(BFF + "/bff/me?x=1",)))
        elsewhere = report(alert(uris=(BFF + "/bff/login",)))
        under_assets = report(alert(uris=(BFF + "/assets/app.js",)))
        self.assertEqual(self.p.evaluate(on_me, GOOD_URLS, 0, exact, TODAY)["findings"], [])
        self.assertTrue(self.p.evaluate(elsewhere, GOOD_URLS, 0, exact, TODAY)["findings"])
        self.assertEqual(self.p.evaluate(under_assets, GOOD_URLS, 0, prefix, TODAY)["findings"], [])

    def test_another_plugin_or_an_expired_entry_does_not_cover(self):
        other = self.valid(entry(pluginId="99999"))
        rep = report(alert(uris=(BFF + "/bff/me",)))
        self.assertTrue(self.p.evaluate(rep, GOOD_URLS, 0, other, TODAY)["findings"])
        expired = self.valid(entry(added="2026-07-01", expires="2026-09-01"))
        self.assertTrue(self.p.evaluate(rep, GOOD_URLS, 0, expired, TODAY)["findings"])

    def test_the_committed_exceptions_file_is_valid(self):
        data = json.loads((ROOT / ".github" / "zap" / "exceptions.json").read_text(encoding="utf-8"))
        errors, _ = self.p.exception_errors(data)
        self.assertEqual(errors, [])

    def test_an_invalid_exceptions_file_exits_2_before_the_report_is_read(self):
        with tempfile.TemporaryDirectory() as d:
            bad = Path(d) / "exceptions.json"
            bad.write_text(json.dumps({"exceptions": [entry(path="/")]}), encoding="utf-8")
            with self.assertRaises(SystemExit) as cm, contextlib.redirect_stdout(io.StringIO()):
                self.p.run(Path(d) / "missing-report.json", Path(d) / "missing-urls.txt", 0, bad)
            self.assertEqual(cm.exception.code, 2)


class OutputTests(unittest.TestCase):
    def setUp(self):
        self.p = load()

    def run_with(self, rep, urls, zap_exit=0):
        with tempfile.TemporaryDirectory() as d:
            rp, up, ep = Path(d) / "report.json", Path(d) / "urls.txt", Path(d) / "exceptions.json"
            if rep is not None:
                rp.write_text(json.dumps(rep), encoding="utf-8")
            up.write_text("\n".join(urls), encoding="utf-8")
            ep.write_text(json.dumps({"exceptions": []}), encoding="utf-8")
            out = io.StringIO()
            with contextlib.redirect_stdout(out), mock.patch.dict(os.environ, {}, clear=False):
                os.environ.pop("GITHUB_STEP_SUMMARY", None)
                code = self.p.run(rp, up, zap_exit, ep)
            return code, out.getvalue()

    def test_a_missing_report_fails(self):
        code, _ = self.run_with(None, GOOD_URLS)
        self.assertNotEqual(code, 0)

    def test_a_clean_report_passes_and_a_medium_fails(self):
        self.assertEqual(self.run_with(report(), GOOD_URLS)[0], 0)
        self.assertNotEqual(self.run_with(report(alert()), GOOD_URLS)[0], 0)

    def test_scanner_text_cannot_issue_workflow_commands(self):
        hostile = alert(risk="1")
        hostile["name"] = "::error::x ::add-mask::y"
        _, out = self.run_with(report(hostile), GOOD_URLS)
        lines = out.splitlines()
        starts = [i for i, l in enumerate(lines) if l.startswith("::stop-commands::")]
        self.assertTrue(starts, "output is wrapped in a stop-commands window")
        token = lines[starts[0]].split("::stop-commands::", 1)[1]
        end = lines.index(f"::{token}::")
        for i, line in enumerate(lines):
            if "::error::x" in line or "::add-mask::y" in line:
                self.assertTrue(starts[0] < i < end, "hostile text appears only inside the stop window")
                self.assertFalse(line.startswith("::"), "and never as a command at the start of a line")

    def test_the_policy_never_writes_github_command_files(self):
        source = (ROOT / ".github" / "scripts" / "zap_policy.py").read_text(encoding="utf-8")
        self.assertNotIn("GITHUB_ENV", source)
        self.assertNotIn("GITHUB_OUTPUT", source)


if __name__ == "__main__":
    unittest.main()
