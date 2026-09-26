"""gitleaks runs the same exact version locally and in CI (#59, G4-59-17, 18; threat P-09).

The #55 finding was missed locally because pre-commit pinned an older gitleaks than CI. A one-sided
bump (Dependabot on either file, or a hand edit) fails here, in CI's claude-config job and in the
pre-push check.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def precommit_version() -> str:
    text = (ROOT / ".pre-commit-config.yaml").read_text(encoding="utf-8")
    m = re.search(r"repo:\s*https://github\.com/gitleaks/gitleaks\s*\n\s*rev:\s*v?(\S+)", text)
    assert m, "gitleaks hook not found in .pre-commit-config.yaml"
    return m.group(1)


def ci_version() -> str:
    lines = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8").splitlines()
    start = next((i for i, line in enumerate(lines) if "gitleaks/gitleaks-action@" in line), None)
    assert start is not None, "gitleaks-action step not found in ci.yml"
    for line in lines[start + 1:start + 8]:  # the step's own env block; the next "- " starts another step
        if line.lstrip().startswith("- "):
            break
        m = re.match(r"\s+GITLEAKS_VERSION:\s*\"?([^\"\s]+)\"?\s*$", line)
        if m:
            return m.group(1)
    raise AssertionError("GITLEAKS_VERSION not set on the gitleaks-action step in ci.yml")


class GitleaksParityTests(unittest.TestCase):
    def test_versions_are_exact(self):
        for version in (precommit_version(), ci_version()):
            with self.subTest(version=version):
                self.assertRegex(version, r"^\d+\.\d+\.\d+$", "pin an exact version, never 'latest'")

    def test_local_and_ci_versions_match(self):
        self.assertEqual(precommit_version(), ci_version())


if __name__ == "__main__":
    unittest.main()
