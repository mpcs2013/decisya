"""The full-stack CI jobs and their harness (#123, ADR-0019; G3 G4-123-01 to 05).

The three jobs `apphost-tests`, `e2e` and `zap` are required checks, so they follow the `image-scan`
always-report pattern. .github/scripts/fullstack.py runs only inside GitHub Actions; these tests call its
pure helpers and never start Docker, the AppHost or a browser.
"""
import base64
import contextlib
import importlib.util
import io
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github" / "workflows" / "ci.yml"
HARNESS = ROOT / ".github" / "scripts" / "fullstack.py"
ZAP_DIR = ROOT / ".github" / "zap"
BASH = shutil.which("bash")  # full path: on Windows a bare "bash" resolves to System32's WSL launcher first

JOBS = {"apphost-tests": 25, "e2e": 30, "zap": 25}
GATE = "needs.changes.outputs.fullstack == 'true'"
UPLOAD = "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a  # v7.0.1"
FORK_GUARD = ("(github.event_name != 'pull_request' || "
              "github.event.pull_request.head.repo.full_name == github.repository)")
ZAP_REF = ("docker.io/zaproxy/zap-stable:20260807@sha256:"
           "781a2bdaea47324e7bab583e2263f21d257b0aee61ed51521a5be45f5f5081ef")


def load():
    spec = importlib.util.spec_from_file_location("_ci_fullstack", HARNESS)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def job_lines(job: str) -> list[str]:
    lines = CI.read_text(encoding="utf-8").splitlines()
    start = lines.index(f"  {job}:")
    end = next((i for i in range(start + 1, len(lines)) if re.match(r"^  [A-Za-z0-9_-]+:\s*$", lines[i])), len(lines))
    return [ln for ln in lines[start:end] if not ln.lstrip().startswith("#")]


def steps(job: list[str]) -> list[list[str]]:
    out: list[list[str]] = []
    for line in job:
        if line.startswith("      - "):
            out.append([line])
        elif out and (line.startswith("        ") or not line.strip()):
            out[-1].append(line)
    return out


def step_if(step: list[str]) -> str | None:
    for line in step:
        m = re.match(r"^(?:      - |        )if:\s*(.+?)\s*$", line)
        if m:
            return m.group(1)
    return None


class JobShapeTests(unittest.TestCase):
    def test_each_job_always_reports_with_read_only_permissions(self):
        for job, minutes in JOBS.items():
            with self.subTest(job=job):
                text = job_lines(job)
                self.assertIn("    needs: changes", text)
                self.assertIn(f"    timeout-minutes: {minutes}", text)
                self.assertFalse(any(re.match(r"^    if:", ln) for ln in text), "a job-level if skips a required check")
                perms = text.index("    permissions:")
                self.assertEqual(text[perms + 1], "      contents: read")
                self.assertFalse(text[perms + 2].startswith("      "), "contents: read is the only permission")
                body = "\n".join(text)
                for banned in ("cache", "secrets.", "environment:", "continue-on-error", "services:"):
                    self.assertNotIn(banned, body)

    def test_every_step_after_the_checkout_is_gated_on_the_lane(self):
        for job in JOBS:
            with self.subTest(job=job):
                all_steps = steps(job_lines(job))
                self.assertTrue(all_steps[0][0].startswith("      - uses: actions/checkout@"))
                self.assertIsNone(step_if(all_steps[0]), "the checkout runs so the job always reports")
                self.assertIn("          persist-credentials: false", all_steps[0])
                for step in all_steps[1:]:
                    cond = step_if(step)
                    self.assertIsNotNone(cond, step[0])
                    self.assertTrue(cond == GATE or cond.startswith(GATE + " && "), cond)

    def test_exactly_two_uploads_from_the_staging_directories_and_never_for_forks(self):
        uploads = {}
        for job in JOBS:
            for step in steps(job_lines(job)):
                if any("actions/upload-artifact" in ln for ln in step):
                    uploads[job] = step
        self.assertEqual(set(uploads), {"e2e", "zap"})
        for job, step in uploads.items():
            with self.subTest(job=job):
                self.assertIn(f"        uses: {UPLOAD}", step)
                self.assertIn(f"          path: ${{{{ runner.temp }}}}/upload/{job}/", step)
                self.assertIn("          retention-days: 7", step)
                self.assertTrue(step_if(step).endswith(FORK_GUARD), step_if(step))
        self.assertIn("failure()", step_if(uploads["e2e"]))

    def test_the_stack_is_started_by_the_harness_never_by_dotnet_run_or_a_bare_npx(self):
        texts = {"harness": HARNESS.read_text(encoding="utf-8")}
        texts.update({job: "\n".join(job_lines(job)) for job in JOBS})
        for where, text in texts.items():
            with self.subTest(where=where):
                self.assertNotIn("dotnet run", text)
                for m in re.finditer(r"npx\b[\"',\s]+([^\"',\s]+)", text):
                    self.assertEqual(m.group(1), "--no-install", m.group(0))
                self.assertNotRegex(text, r"npm (install|i)\b", "npm ci only")

    def test_no_switch_weakens_tls_the_dashboard_or_package_signatures(self):
        banned = ("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "UNSECURED_ALLOW_ANONYMOUS", "AuthMode=Unsecured",
                  "verify=False", "_create_unverified_context", "NODE_TLS_REJECT_UNAUTHORIZED",
                  "DOTNET_NUGET_SIGNATURE_VERIFICATION", "CERT_NONE", "check_hostname = False")
        files = [CI, HARNESS, ROOT / ".github" / "scripts" / "zap_policy.py",
                 *sorted(p for p in ZAP_DIR.iterdir() if p.is_file())]
        for path in files:
            text = path.read_text(encoding="utf-8")
            for word in banned:
                with self.subTest(file=path.name, word=word):
                    self.assertNotIn(word, text)


class ReviewPathTests(unittest.TestCase):
    def test_the_zap_files_need_review_and_warn_on_push(self):
        # G2 D7 and S-123-06: a change under .github/zap/ cannot skip G3/G6 and is flagged by the pre-push check.
        for name in ("gates", "prepush"):
            spec = importlib.util.spec_from_file_location(f"_ci_{name}", ROOT / ".claude" / "scripts" / f"{name}.py")
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            setattr(self, name, module)
        for path in (".github/zap/Dockerfile", ".github/zap/context.context", ".github/zap/hook.py",
                     ".github/zap/exceptions.json"):
            with self.subTest(path=path):
                self.assertTrue(self.gates.REVIEW_REQUIRED_PATHS.match(path))
                self.assertTrue(path.startswith(self.prepush.WARN_PREFIXES))


def lanes_block() -> str:
    m = re.search(r"# lanes: begin\n(.*?)\n\s*# lanes: end", CI.read_text(encoding="utf-8"), re.DOTALL)
    assert m, "lanes markers not found in ci.yml"
    return "\n".join(line.strip() for line in m.group(1).splitlines())


@unittest.skipUnless(BASH, "needs bash")
class LaneTests(unittest.TestCase):
    """The fullstack lane works by exclusion: anything not proven irrelevant runs the three jobs."""

    def lane(self, changed: str) -> str:
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "out"
            script = 'changed="$FAKE_CHANGED"\n' + lanes_block()
            env = {**os.environ, "GITHUB_OUTPUT": out.as_posix(), "FAKE_CHANGED": changed}
            subprocess.run([BASH, "-c", script], cwd=ROOT, env=env, check=True, capture_output=True)
            values = dict(ln.split("=", 1) for ln in out.read_text(encoding="utf-8").splitlines())
        return values["fullstack"]

    def test_lane_table(self):
        table = {
            "src/Modules/Ledger/Ledger.cs": "true",
            "src/Decisya.Web/src/App.tsx": "true",
            "tests/e2e/login.spec.ts": "true",
            "deploy/keycloak/realm-dev.json": "true",
            "tools/new-kind-of-file.sh": "true",
            ".github/workflows/ci.yml": "true",
            ".github/scripts/fullstack.py": "true",
            ".github/scripts/zap_policy.py": "true",
            ".github/scripts/spa_package_guard.py": "true",
            ".github/zap/context.context": "true",
            "ALL": "true",
            "docs/adr/0019-x.md": "false",
            "README.md": "false",
            ".claude/hooks/x.py": "false",
            ".devcontainer/sandbox.py": "false",
            ".pre-commit-config.yaml": "false",
            ".github/dependabot.yml": "false",
            ".github/workflows/release.yml": "false",
            ".github/scripts/image_scan.py": "false",
            "deploy/caddy/Caddyfile": "false",
            "deploy/otel-collector/config.yaml": "false",
            "deploy/tests/test_x.py": "false",
        }
        for changed, expected in table.items():
            with self.subTest(changed=changed):
                self.assertEqual(self.lane(changed), expected)
        self.assertEqual(self.lane("docs/a.md\nsrc/Decisya.Bff/Program.cs"), "true", "one code file is enough")


class InputTests(unittest.TestCase):
    def setUp(self):
        self.h = load()

    def test_the_aspire_cli_version_is_the_approved_sdk_version(self):
        self.assertEqual(self.h.read_aspire_version('<Project Sdk="Aspire.AppHost.Sdk/13.5.4">'), "13.5.4")
        self.assertEqual(self.h.read_aspire_version((ROOT / "src" / "Decisya.AppHost" / "Decisya.AppHost.csproj")
                                                    .read_text(encoding="utf-8")), "13.5.4")
        for bad in ('<Project Sdk="Aspire.AppHost.Sdk/13.6.0">', '<Project Sdk="Aspire.AppHost.Sdk/13.5.4-preview">',
                    '<Project Sdk="Aspire.AppHost.Sdk/13.5.4;x">', "<Project>",
                    '<Project Sdk="Aspire.AppHost.Sdk/13.5.4"><Project Sdk="Aspire.AppHost.Sdk/13.5.4">'):
            with self.subTest(text=bad), self.assertRaises(self.h.HarnessError):
                self.h.read_aspire_version(bad)

    def test_the_zap_reference_is_the_dated_digest_pin(self):
        self.assertEqual(self.h.parse_zap_ref((ZAP_DIR / "Dockerfile").read_text(encoding="utf-8")), ZAP_REF)
        digest = ZAP_REF.split("@", 1)[1]
        for bad in ("FROM docker.io/zaproxy/zap-stable:latest AS zap",
                    f"FROM docker.io/zaproxy/zap-stable:2.17.0@{digest} AS zap",
                    "FROM docker.io/zaproxy/zap-stable:20260807 AS zap",
                    f"FROM docker.io/zaproxy/zap-weekly:20260807@{digest} AS zap",
                    f"FROM {ZAP_REF}",
                    f"FROM {ZAP_REF} AS zap\nFROM {ZAP_REF} AS zap"):
            with self.subTest(text=bad), self.assertRaises(self.h.HarnessError):
                self.h.parse_zap_ref(bad)

    def test_the_zap_container_argv_is_exact(self):
        self.assertEqual(self.h.zap_run_argv(ZAP_REF, "/w"), [
            "docker", "run", "--rm", "--network", "host", "--user", "1000:1000", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--pids-limit", "2048", "--memory", "8g",
            "-v", "/w:/zap/wrk:rw", ZAP_REF, "zap-baseline.py", "-t", "https://localhost:7200",
            "-n", "context.context", "--hook", "/zap/wrk/hook.py", "--autooff", "-j", "-m", "2", "-T", "10",
            "-P", "18090", "-z", "-silent -config start.checkForUpdates=false", "-J", "report.json",
            "-r", "report.html"])
        with self.assertRaises(self.h.HarnessError):
            self.h.zap_run_argv("docker.io/zaproxy/zap-stable:latest", "/w")

    def test_the_committed_context_includes_only_the_bff_and_excludes_every_launch_port(self):
        context = (ZAP_DIR / "context.context").read_text(encoding="utf-8")
        ports = self.h.launch_profile()["ports"]
        self.assertIn(8080, ports)
        self.assertEqual(self.h.check_zap_context(context, ports), [])
        self.assertTrue(self.h.check_zap_context(context, ports | {12345}), "a new port must be excluded")
        widened = context.replace("</incregexes>", "</incregexes>\n    <incregexes>^https://localhost:8080.*$</incregexes>", 1)
        self.assertTrue(self.h.check_zap_context(widened, ports))
        self.assertTrue(self.h.check_zap_context("<configuration>", ports))

    def test_the_launch_profile_passes_only_allow_listed_https_localhost_values(self):
        env = self.h.launch_profile()["env"]
        self.assertEqual(set(env), set(self.h.LAUNCH_ENV_KEYS) | {"ASPNETCORE_URLS"})
        for key in ("ASPNETCORE_URLS", "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"):
            self.assertTrue(env[key].startswith("https://localhost:"), key)


class SecretTests(unittest.TestCase):
    def setUp(self):
        self.h = load()

    def test_every_run_value_is_masked_before_anything_else_is_printed(self):
        out = io.StringIO()
        with mock.patch.object(self.h.sys, "stdout", out):
            run = self.h.RunSecrets()
        values = run.all_values()
        self.assertEqual(set(run.parameters), set(self.h.STACK_PARAMETERS))
        self.assertEqual(run.parameters["dev-user-password"], run.password)
        self.assertEqual(len(set(values)), len(values))
        for value in values:
            self.assertRegex(value, r"^[0-9a-f]{48}$")
        self.assertEqual(out.getvalue().splitlines(), [f"::add-mask::{v}" for v in values])

    def test_child_environments_carry_no_runner_credential(self):
        leaky = {"GITHUB_TOKEN": "t", "GITHUB_ENV": "/e", "GITHUB_OUTPUT": "/o", "ACTIONS_RUNTIME_TOKEN": "a",
                 "ACTIONS_ID_TOKEN_REQUEST_URL": "u", "Parameters__dev-user-password": "p", "E2E_DEV_PASSWORD": "p",
                 "MY_SECRET": "s", "db_password": "p", "NPM_TOKEN": "n",
                 "DOTNET_NUGET_SIGNATURE_" + "VERIFICATION": "false"}
        with mock.patch.dict(os.environ, {**leaky, "GITHUB_ACTIONS": "true", "HOME": "/h"}, clear=True):
            env = self.h.base_env()
        for key in leaky:
            self.assertNotIn(key, env)
        self.assertEqual(env["GITHUB_ACTIONS"], "true")
        self.assertEqual(env["HOME"], "/h")

    def test_needles_find_the_password_in_four_forms_and_the_token_shapes(self):
        password = 'a b&c"d/é'
        needles = self.h.Needles([password])
        self.assertEqual(len(self.h.secret_forms(password)), 4)
        for form in self.h.secret_forms(password):
            self.assertTrue(needles.line_matches(f"x {form} y"), form)
        for hit in ("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.sig", "Set-Cookie: __Host-decisya-sessionC1=abc",
                    "http://localhost:15062/login?t=abc", "KEYCLOAK_IDENTITY_LEGACY=x", "AUTH_SESSION_ID=x"):
            self.assertTrue(needles.line_matches(hit), hit)
        self.assertFalse(needles.line_matches("ready: bff /bff/me"))

    def test_needles_catch_private_keys_even_base64_encoded(self):
        # #123 spike 2: the Aspire tunnel proxy prints its TLS key as base64 JSON, which reached a CI log.
        # The PEM headers are assembled at run time: a literal one (or its base64) is a gitleaks finding.
        needles = self.h.Needles([])
        header = "-----BEGIN " + "PRIVATE" + " KEY-----"
        ec_header = header.replace("BEGIN ", "BEGIN EC ")
        encoded = base64.b64encode(header.encode("ascii")).decode("ascii")
        for hit in (header, ec_header, encoded, '{"server_key_base64":"QUJD"}', '"private_key": "x"'):
            self.assertTrue(needles.line_matches(hit), hit)

    def test_diagnose_never_reads_the_tunnel_proxy_logs(self):
        stack = self.h.Stack.__new__(self.h.Stack)
        stack.needles = self.h.Needles([])
        calls = []
        listing = ["keycloak-abc\tquay.io/keycloak/keycloak\tUp\t8080/tcp",
                   "aspire-container-network-tunnelproxy-xyz\tdcptun\tUp\t15049/tcp"]

        def fake_run(argv):
            calls.append(argv)
            return listing if argv[:2] == ["docker", "ps"] else ["line"]

        with mock.patch.object(stack, "_diag_run", side_effect=fake_run), \
                mock.patch.object(stack, "_diag_probe", return_value="status 200"), \
                contextlib.redirect_stdout(io.StringIO()):
            stack.diagnose()
        logs = [argv[-1] for argv in calls if argv[:2] == ["docker", "logs"]]
        self.assertEqual(logs, ["keycloak-abc"])
        self.assertIn(["docker", "port", "aspire-container-network-tunnelproxy-xyz"], calls)

    def test_the_harness_refuses_to_run_outside_github_actions(self):
        with mock.patch.dict(os.environ, {"GITHUB_ACTIONS": ""}), contextlib.redirect_stdout(io.StringIO()) as out:
            for command in ("install-aspire", "apphost-tests", "e2e", "zap"):
                with self.subTest(command=command):
                    self.assertEqual(self.h.main([command]), 1)
        self.assertNotIn("::add-mask::", out.getvalue(), "nothing is generated before the CI check")


class ArtifactBoundaryTests(unittest.TestCase):
    """Scan everything first; stage regular files only when the whole scan is clean (G4-123-02)."""

    def setUp(self):
        self.h = load()
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        self.source, self.staging = self.dir / "results", self.dir / "upload" / "e2e"
        (self.source / "trace").mkdir(parents=True)
        (self.source / "report.json").write_text("{}", encoding="utf-8")
        (self.source / "trace" / "page.txt").write_text("clean", encoding="utf-8")
        self.needles = self.h.Needles(["s3cret-value"])

    def stage(self, only=None):
        with contextlib.redirect_stdout(io.StringIO()):
            return self.h.scan_and_stage(self.source, self.staging, self.needles, only)

    def staged(self):
        return sorted(p.relative_to(self.staging).as_posix() for p in self.staging.rglob("*") if p.is_file())

    def test_a_clean_tree_is_staged(self):
        self.assertTrue(self.stage())
        self.assertEqual(self.staged(), ["report.json", "trace/page.txt"])

    def test_only_limits_what_is_staged(self):
        self.assertTrue(self.stage(only=("report.json",)))
        self.assertEqual(self.staged(), ["report.json"])

    def test_a_hit_anywhere_stages_nothing_and_deletes_the_source(self):
        for content in ("x s3cret-value y", "s3cret%2Dvalue".replace("%2D", "-"), "Cookie: __Host-decisya-session=1"):
            with self.subTest(content=content):
                (self.source / "trace").mkdir(parents=True, exist_ok=True)
                (self.source / "trace" / "page.txt").write_text(content, encoding="utf-8")
                (self.source / "report.json").write_text("{}", encoding="utf-8")
                self.assertFalse(self.stage(only=("report.json",)), "the scan covers files that are not staged")
                self.assertFalse(self.source.exists())
                self.assertFalse(self.staging.exists())

    def test_an_old_staging_directory_is_replaced(self):
        self.staging.mkdir(parents=True)
        (self.staging / "stale.txt").write_text("old", encoding="utf-8")
        self.assertTrue(self.stage())
        self.assertNotIn("stale.txt", self.staged())

    def test_a_missing_source_is_safe_and_stages_nothing(self):
        shutil.rmtree(self.source)
        self.assertTrue(self.stage())
        self.assertFalse(self.staging.exists())

    def test_a_link_fails_the_scan(self):
        try:
            os.symlink(self.dir / "elsewhere", self.source / "link")
        except (OSError, NotImplementedError):
            self.skipTest("cannot create a symlink here")
        self.assertFalse(self.stage())
        self.assertFalse(self.staging.exists())


if __name__ == "__main__":
    unittest.main()
