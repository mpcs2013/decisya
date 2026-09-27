"""CI's Integration step runs exactly the test projects that contain Category=Integration tests (#17).

The step runs listed projects only, without --ignore-exit-code 8, so a project with zero Integration
tests never makes it fail, and a mistyped trait in a listed project still does (G4-17-19). This test
fails when a project gains Integration tests without being added to the step, or when a listed
project no longer has any.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRAIT = re.compile(r'Trait\(\s*"Category"\s*,\s*"Integration"\s*\)')


def projects_with_integration_tests() -> set[str]:
    found = set()
    for csproj in (ROOT / "tests").rglob("*.csproj"):
        if any(TRAIT.search(cs.read_text(encoding="utf-8")) for cs in csproj.parent.rglob("*.cs")
               if "obj" not in cs.parts and "bin" not in cs.parts):
            found.add(csproj.relative_to(ROOT).as_posix())
    return found


def projects_in_ci_step() -> set[str]:
    text = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    step = text.split("- name: Integration tests (Testcontainers)", 1)[1].split("\n      - name:", 1)[0]
    m = re.search(r"for project in ([^;]+); do", step)
    assert m, "the Integration step's project loop was not found in ci.yml"
    return set(m.group(1).split())


class CiIntegrationStepTests(unittest.TestCase):
    def test_every_project_with_integration_tests_runs_in_ci_and_no_other(self):
        self.assertEqual(projects_in_ci_step(), projects_with_integration_tests())

    def test_zero_tests_is_not_ignored(self):
        text = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        step = text.split("- name: Integration tests (Testcontainers)", 1)[1].split("\n      - name:", 1)[0]
        self.assertNotIn("--ignore-exit-code", step)


if __name__ == "__main__":
    unittest.main()
