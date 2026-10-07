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

    def test_the_job_asks_for_read_only_contents_and_nothing_else(self):
        self.assertRegex(self.job, r"(?m)^    permissions:\n      contents: read\n")
        self.assertNotRegex(self.job, r"(?m)^\s+(id-token|packages|pull-requests|checks|statuses|security-events):")

    def test_the_checkout_does_not_persist_credentials(self):
        self.assertIn("persist-credentials: false", self.job)


if __name__ == "__main__":
    unittest.main()
