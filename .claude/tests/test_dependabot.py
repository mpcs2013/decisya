"""Dependabot PRs skip commitlint, and only Dependabot's own (#113; G4-113-01 to 03).

ci.yml: both commitlint steps in build-test carry the same exemption, compared as a whole expression
(a `||` or a misplaced `!` would skip the verdict for human PRs, and commitlint's continue-on-error
would then fail open). dependabot.yml: the three ignore rules sit in their own entries only.
No test can prove Dependabot honours an ignore; docs/runbooks/main-ruleset.md has the manual check.
Stdlib only, like the other tests here.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github" / "workflows" / "ci.yml"
DEPENDABOT = ROOT / ".github" / "dependabot.yml"

STEPS = ("Commit messages (Conventional Commits)", "Commit messages verdict and mirror drift check")
EXEMPTION = ("github.event_name == 'pull_request' && !( "
             "github.event.pull_request.user.login == 'dependabot[bot]' && "
             "github.actor == 'dependabot[bot]' && "
             "startsWith(github.head_ref, 'dependabot/'))")


def job_block(text: str, job: str) -> list[str]:
    """The lines of one top-level job under `jobs:` (two-space indent)."""
    lines = text.splitlines()
    start = next((i for i, ln in enumerate(lines) if ln == f"  {job}:"), None)
    assert start is not None, f"job {job} not found in ci.yml"
    end = next((i for i in range(start + 1, len(lines)) if re.match(r"^  \S", lines[i])), len(lines))
    return lines[start:end]


def step_block(job: list[str], name: str) -> list[str]:
    start = next((i for i, ln in enumerate(job) if re.match(rf"^      - name: {re.escape(name)}\s*$", ln)), None)
    assert start is not None, f"step {name!r} not found in build-test"
    end = next((i for i in range(start + 1, len(job)) if re.match(r"^      - ", job[i])), len(job))
    return job[start:end]


def step_if(step: list[str]) -> str:
    """The step's `if:` value, plain or folded (`>-`), whitespace-normalised; fails if absent or repeated."""
    idx = [i for i, ln in enumerate(step) if re.match(r"^        if:", ln)]
    assert len(idx) == 1, f"expected one if: in step, found {len(idx)}"
    first = step[idx[0]].split("if:", 1)[1].strip()
    if first not in (">-", ">", "|", "|-"):
        return " ".join(first.split())
    parts = []
    for ln in step[idx[0] + 1:]:
        if not ln.startswith("          "):  # deeper than the step keys
            break
        parts.append(ln.strip())
    return " ".join(" ".join(parts).split())


def entries(text: str) -> dict[tuple[str, str], str]:
    """(ecosystem, directory) -> the entry's text. Fails closed if the layout cannot be read."""
    chunks = re.split(r"(?m)^  - package-ecosystem:", text)[1:]
    assert chunks, "no '  - package-ecosystem:' entries found in dependabot.yml"
    found = {}
    for chunk in chunks:
        eco = chunk.splitlines()[0].strip()
        m = re.search(r"(?m)^    directory:\s*(\S+)\s*$", chunk)
        assert m, f"entry {eco} has no directory"
        # A trailing comment belongs to the next entry; keep only the entry's own indented lines.
        own = [ln for ln in chunk.splitlines()[1:] if ln.startswith("    ") or not ln.strip()]
        found[(eco, m.group(1))] = "\n".join(own)
    return found


def ignore_rules(entry: str) -> list[tuple[str, tuple[str, ...]]]:
    """(dependency-name, update-types) for each rule under `ignore:`."""
    rules = []
    for m in re.finditer(r'(?m)^      - dependency-name:\s*"?([^"\s]+)"?\s*$((?:\n        \S.*)*)', entry):
        types = re.search(r"update-types:\s*\[([^\]]*)\]", m.group(2))
        rules.append((m.group(1), tuple(t.strip().strip("'\"") for t in types.group(1).split(",")) if types else ()))
    return rules


class CommitlintExemptionTests(unittest.TestCase):
    def setUp(self):
        self.job = job_block(CI.read_text(encoding="utf-8"), "build-test")

    def test_both_steps_carry_exactly_the_exemption(self):
        for name in STEPS:
            with self.subTest(step=name):
                self.assertEqual(step_if(step_block(self.job, name)), EXEMPTION)

    def test_only_commitlint_continues_on_error(self):
        lint, verdict = (step_block(self.job, n) for n in STEPS)
        self.assertTrue(any(re.match(r"^        continue-on-error:\s*true\b", ln) for ln in lint))
        self.assertFalse(any("continue-on-error" in ln for ln in verdict), "the verdict step must fail the job")

    def test_build_test_has_no_job_level_if(self):
        self.assertFalse(any(re.match(r"^    if:", ln) for ln in self.job), "the required check must always report")


class DependabotIgnoreTests(unittest.TestCase):
    EXPECTED = {
        ("npm", "/src/Decisya.Web"): [("typescript", ("version-update:semver-major",)),
                                      ("eslint", ("version-update:semver-major",))],
        # G4-113-01: Dependabot names it without the quay.io registry; no update-types (all bumps).
        ("docker", "/.devcontainer/engine"): [("keycloak/keycloak", ())],
        # #119: a .NET major is an ADR-level change (ADR-0009); digest and minor bumps still come.
        ("docker", "/.github/release"): [("dotnet/aspnet", ("version-update:semver-major",)),
                                         ("dotnet/runtime", ("version-update:semver-major",))],
    }

    def test_ignore_rules_are_exact_and_scoped(self):
        found = entries(DEPENDABOT.read_text(encoding="utf-8"))
        for key, rules in self.EXPECTED.items():
            self.assertIn(key, found, f"dependabot.yml entry {key} not found")
        for key, text in found.items():
            with self.subTest(entry=key):
                self.assertEqual(sorted(ignore_rules(text)), sorted(self.EXPECTED.get(key, [])))


class DetectorTests(unittest.TestCase):
    """The parsers fail closed (red cases)."""

    def step(self, cond_lines):
        return ["      - name: x", *cond_lines, "        run: echo"]

    def test_weakened_conditions_do_not_match(self):
        bad = ["github.event_name == 'pull_request' || !(", "github.actor == 'dependabot[bot]'"]
        self.assertNotEqual(step_if(self.step(["        if: >-", *("          " + b for b in bad)])), EXEMPTION)
        self.assertEqual(step_if(self.step(["        if: github.event_name == 'pull_request'"])),
                         "github.event_name == 'pull_request'")

    def test_missing_if_fails(self):
        with self.assertRaises(AssertionError):
            step_if(self.step([]))

    def test_rules_parse_and_registry_name_differs(self):
        entry = ('    ignore:\n      - dependency-name: "quay.io/keycloak/keycloak"\n'
                 '      - dependency-name: "eslint"\n        update-types: ["version-update:semver-major"]\n')
        self.assertEqual(ignore_rules(entry), [("quay.io/keycloak/keycloak", ()),
                                               ("eslint", ("version-update:semver-major",))])
        self.assertNotIn(("keycloak/keycloak", ()), ignore_rules(entry))

    def test_unreadable_dependabot_layout_fails(self):
        with self.assertRaises(AssertionError):
            entries("version: 2\nupdates: []\n")


if __name__ == "__main__":
    unittest.main()
