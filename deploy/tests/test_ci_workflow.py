"""The `deploy-guards` job cannot be skipped, and does what the design says (issue #120, G3 T120-05, G4-120-03).

GitHub counts a skipped required job as passed, and a required check that never starts blocks every
pull request. So the job has no `paths:` filter on the workflow, no `needs`, no job-level or step-level
`if:`, no `continue-on-error`, and it fails (not skips) when Docker is missing. These tests read
.github/workflows/ci.yml as text (stdlib only, no YAML parser) and fail closed when they cannot find
what they look for.
"""
from __future__ import annotations

import re
import unittest
from pathlib import Path

CI = Path(__file__).resolve().parents[2] / ".github" / "workflows" / "ci.yml"
PINNED = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}\s+#\s*v\d+(\.\d+)*\S*$")


def workflow_text() -> str:
    return CI.read_text(encoding="utf-8")


def job_block(text: str, name: str) -> str:
    """From the job's header line to the next job header (or the end of the file)."""
    header = "\n  %s:\n" % name
    start = text.find(header)
    assert start >= 0, "job %s not found in ci.yml" % name
    rest = text[start + 1:]
    following = re.search(r"\n  [A-Za-z_][\w-]*:\n", rest[len(header) - 1:])
    return rest if following is None else rest[: len(header) - 1 + following.start()]


def code_lines(block: str) -> list:
    return [l for l in block.splitlines() if l.strip() and not l.lstrip().startswith("#")]


class DeployGuardsJobTests(unittest.TestCase):
    def setUp(self):
        self.text = workflow_text()
        self.job = job_block(self.text, "deploy-guards")
        self.code = code_lines(self.job)

    def test_the_workflow_has_no_paths_filter(self):
        triggers = re.search(r"(?ms)^on:\n(.*?)^\S", self.text + "x").group(1)
        self.assertNotRegex("\n".join(code_lines(triggers)), r"paths(-ignore)?\s*:")

    def test_the_job_is_never_skipped(self):
        for line in self.code:
            self.assertFalse(re.match(r"^\s*(-\s+)?if\s*:", line), "an if: key: %s" % line.strip())
            self.assertFalse(re.match(r"^\s{4}needs\s*:", line), "a needs: key: %s" % line.strip())
            self.assertNotIn("continue-on-error", line)
            self.assertNotIn("|| true", line)
            self.assertNotIn("always()", line)

    def test_docker_is_required_not_optional(self):
        self.assertRegex("\n".join(self.code), r'DECISYA_DEPLOY_REQUIRE_DOCKER:\s*"1"')

    def test_the_address_scan_runs_first_and_alone(self):
        steps = re.split(r"(?m)^      - ", self.job)[1:]
        self.assertGreaterEqual(len(steps), 5)
        self.assertTrue(steps[0].startswith("uses: actions/checkout@"))
        self.assertIn("test_no_home_addresses.py", steps[1])
        self.assertNotIn("if:", steps[1])

    def test_the_drift_check_runs_the_built_apphost_and_compares_with_the_committed_file(self):
        script = "\n".join(self.code)
        self.assertIn("--operation publish --step publish --output-path", script)
        self.assertIn("dotnet build src/Decisya.AppHost/Decisya.AppHost.csproj", script)
        self.assertIn("-warnaserror", script)
        self.assertIn("-getProperty:TargetPath", script)
        self.assertNotIn("find artifacts/bin", script)
        self.assertIn('test -f "$dll"', script)
        self.assertIn("diff -u deploy/compose/docker-compose.yaml", script)
        self.assertNotIn("dotnet run", script, "dotnet run is taken over by the Aspire CLI where one is installed")
        self.assertNotIn("aspire publish", script)

    def test_the_guard_suite_runs_every_test_file(self):
        self.assertRegex("\n".join(self.code), r'unittest discover -s deploy/tests -p "test_\*\.py"')

    def test_every_action_is_pinned_to_a_commit_sha(self):
        uses = [re.match(r"^\s*(?:-\s+)?uses:\s*(.*?)\s*$", l).group(1) for l in self.code if re.match(r"^\s*(?:-\s+)?uses:", l)]
        self.assertGreaterEqual(len(uses), 2)
        for value in uses:
            self.assertRegex(value, PINNED)

    def test_the_job_asks_for_read_only_contents_and_packages_and_nothing_else(self):
        # #164 (G2 D2, G3 M-5): packages: read is the Ostium feed read, in exactly this one block.
        self.assertRegex(self.job, r"(?m)^    permissions:\n      contents: read\n      packages: read\n(?!      )")
        self.assertNotRegex(self.job, r"(?m)^\s+(id-token|pull-requests|checks|statuses|security-events):")
        self.assertNotRegex(self.job, r"(?m)^\s+\w[\w-]*:\s*write\s*$")

    def test_the_checkout_does_not_persist_credentials(self):
        self.assertIn("persist-credentials: false", self.job)

    def test_a_locked_restore_runs_before_the_first_build_and_the_job_stays_unconditional(self):
        steps = re.split(r"(?m)^      - ", self.job)[1:]
        restore = [i for i, s in enumerate(steps) if "dotnet restore" in s]
        build = [i for i, s in enumerate(steps) if "dotnet build" in s]
        self.assertEqual(len(restore), 1, "exactly one explicit restore step")
        self.assertTrue(build and restore[0] < build[0], "the restore comes before the first build")
        self.assertIn("dotnet restore src/Decisya.AppHost/Decisya.AppHost.csproj --locked-mode", steps[restore[0]])
        self.assertIn("dotnet build src/Decisya.AppHost/Decisya.AppHost.csproj --configuration Release -warnaserror --no-restore", steps[build[0]])


CREDENTIAL = "NuGetPackageSourceCredentials_ostium"
RELEASE = Path(__file__).resolve().parents[2] / ".github" / "workflows" / "release.yml"
WORKFLOWS = {"ci.yml": CI, "release.yml": RELEASE}


class RestoreHardeningTests(unittest.TestCase):
    """Static checks of the restore rules of issue #164 (G2 D2, G3 M-1 and M-5) over both workflows.

    The feed credential is the job's own token. It may appear only as a step-level `env:` entry of a `run:` step
    whose command is a `dotnet restore ... --locked-mode`: never on a `uses:` step, at job or workflow level, or
    in a file written for later steps.
    """

    def texts(self):
        return {name: path.read_text(encoding="utf-8") for name, path in WORKFLOWS.items()}

    def test_locked_restore_is_declared_once_at_workflow_level_and_never_redefined(self):
        for name, text in self.texts().items():
            with self.subTest(workflow=name):
                self.assertRegex(text, r'(?m)^env:\n  DECISYA_LOCKED_RESTORE: "true"\n')
                self.assertEqual(text.count("DECISYA_LOCKED_RESTORE:"), 1, "no job or step redefines it")

    def test_every_dotnet_restore_is_locked_and_no_source_is_overridden(self):
        for name, text in self.texts().items():
            for line in code_lines(text):
                with self.subTest(workflow=name, line=line.strip()):
                    if re.search(r"\bdotnet restore\b", line):
                        self.assertIn("--locked-mode", line)
                    if re.search(r"\bdotnet\b", line):
                        self.assertNotRegex(line, r"(--source\b|\s-s\s|--configfile|RestoreSources)")
                    self.assertNotRegex(line, r"(RestoreLockedMode|DECISYA_LOCKED_RESTORE=)")

    def test_no_job_that_may_restore_has_a_package_cache(self):
        for name, text in self.texts().items():
            with self.subTest(workflow=name):
                body = "\n".join(code_lines(text))
                self.assertNotIn("actions/cache", body)
                self.assertNotRegex(body, r"(?m)^\s+cache(-dependency-path)?:")
                self.assertNotIn(".nuget/packages", body)

    def test_the_credential_is_only_a_step_env_of_a_run_step_that_restores(self):
        seen = 0
        for name, text in self.texts().items():
            lines = text.splitlines()
            for number, line in enumerate(lines):
                if CREDENTIAL not in line or line.lstrip().startswith("#"):
                    continue
                seen += 1
                with self.subTest(workflow=name, line=number + 1):
                    self.assertRegex(line, r'^          %s: "Username=\$\{\{ github\.actor \}\};Password=\$\{\{ github\.token \}\}"$' % CREDENTIAL)
                    self.assertEqual(lines[number - 1], "        env:", "a step-level env: block")
                    start = max(i for i in range(number) if lines[i].startswith("      - "))
                    end = next((i for i in range(number + 1, len(lines)) if lines[i].startswith("      - ") or re.match(r"^  \S", lines[i])), len(lines))
                    step = "\n".join(lines[start:end])
                    self.assertNotRegex(step, r"(?m)^(      - |        )uses:")
                    self.assertRegex(step, r"(?m)^        run: dotnet restore( .*)? --locked-mode$")
        self.assertEqual(seen, 7, "ci.yml has six restore steps carrying it (build-test, codeql, apphost-tests, e2e, zap, deploy-guards), release.yml one")

    def test_the_credential_is_never_written_for_later_steps(self):
        for name, text in self.texts().items():
            with self.subTest(workflow=name):
                for line in code_lines(text):
                    if "GITHUB_ENV" in line or "GITHUB_OUTPUT" in line or "GITHUB_PATH" in line:
                        self.assertNotIn("github.token", line)
                        self.assertNotIn("GITHUB_TOKEN", line)
                        self.assertNotIn(CREDENTIAL, line)

    def test_packages_read_is_granted_only_to_the_jobs_that_restore(self):
        expected = {"ci.yml": {"build-test", "codeql", "apphost-tests", "e2e", "zap", "deploy-guards"},
                    "release.yml": {"build", "verify"}}   # verify reads GHCR; build reads the Ostium feed
        for name, text in self.texts().items():
            with self.subTest(workflow=name):
                body = text.split("\njobs:\n", 1)[1]
                parts = re.split(r"(?m)^  ([A-Za-z0-9_-]+):\s*$", body)
                with_packages = {parts[i] for i in range(1, len(parts), 2) if re.search(r"(?m)^      packages: read$", parts[i + 1])}
                self.assertEqual(with_packages, expected[name])
                self.assertNotRegex(text.split("\njobs:\n", 1)[0], r"(?m)^\s+packages:")
                for job in ("realm-guard", "changes", "claude-config", "image-scan"):
                    self.assertNotIn(job, with_packages)


if __name__ == "__main__":
    unittest.main()
