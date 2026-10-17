"""Every CI and hook dependency is pinned by commit SHA or digest (#28, ADR-0014; G4-28-02).

A tag can be moved (the March 2026 trivy-action compromise moved 76 of 77 tags), so a pin is a full
40-character commit SHA with a version comment. This covers step-level and job-level (reusable
workflow) `uses:`, `docker://` references, the `action.yml` of every local `./` action, and the
pre-commit `rev:` values. Anything the scan cannot read fails, rather than passing unchecked.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"
PRECOMMIT = ROOT / ".pre-commit-config.yaml"

USES_RE = re.compile(r"^\s*(?:-\s+)?uses:\s*(.*?)\s*$")
PINNED_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}\s+#\s*v\d+(\.\d+)*\S*$")
DOCKER_RE = re.compile(r"^docker://\S+@sha256:[0-9a-f]{64}$")
LOCAL_RE = re.compile(r"^\./\S+$")
IMAGE_RE = re.compile(r"^\s*image:\s*(.*?)\s*$")
REV_RE = re.compile(r"^\s*rev:\s*(.*?)\s*$")
FROZEN_RE = re.compile(r"^[0-9a-f]{40}\s+#\s*frozen:\s*v?\d+(\.\d+)*\S*$")


def uses_problems(text: str, where: str) -> list[str]:
    """Unpinned or unreadable `uses:` lines in one workflow or action file."""
    problems = []
    for n, line in enumerate(text.splitlines(), 1):
        if line.lstrip().startswith("#"):
            continue
        m = USES_RE.match(line)
        if not m:
            if re.search(r"\buses\s*:", line):
                problems.append(f"{where}:{n}: cannot read this uses: line; write it on one line")
            continue
        value = m.group(1).strip("'\"") if not m.group(1).startswith(("'", '"')) else m.group(1)
        raw = m.group(1)
        if raw.startswith(("'", '"')) or raw.startswith(("{", "[", "$", "&", "*")) or raw == "":
            problems.append(f"{where}:{n}: cannot read uses: {raw!r}; use a plain value")
        elif value.startswith("docker://"):
            if not DOCKER_RE.match(value.split()[0]):
                problems.append(f"{where}:{n}: docker:// reference without an @sha256 digest: {value}")
        elif LOCAL_RE.match(value):
            continue  # its action.yml is checked separately
        elif not PINNED_RE.match(value):
            problems.append(f"{where}:{n}: not pinned to a 40-character SHA with a '# vX' comment: {value}")
    return problems


def image_problems(text: str, where: str) -> list[str]:
    problems = []
    for n, line in enumerate(text.splitlines(), 1):
        m = IMAGE_RE.match(line)
        if m and m.group(1).startswith("docker://") and not DOCKER_RE.match(m.group(1).strip("'\"")):
            problems.append(f"{where}:{n}: action image without an @sha256 digest: {m.group(1)}")
    return problems


def precommit_problems(text: str) -> list[str]:
    problems = []
    for n, line in enumerate(text.splitlines(), 1):
        m = REV_RE.match(line)
        if m and not FROZEN_RE.match(m.group(1)):
            problems.append(f".pre-commit-config.yaml:{n}: rev is not a 40-character SHA with '# frozen: vX': {m.group(1)}")
        elif not m and re.search(r"\brev\s*:", line) and not line.lstrip().startswith("#"):
            problems.append(f".pre-commit-config.yaml:{n}: cannot read this rev: line")
    return problems


def local_actions() -> list[Path]:
    return sorted((ROOT / ".github").rglob("action.y*ml"))


def local_refs(text: str) -> list[str]:
    """The `./path` values of every `uses:` line (G6 F-01: resolved and checked, never skipped)."""
    refs = []
    for line in text.splitlines():
        m = USES_RE.match(line)
        if m and LOCAL_RE.match(m.group(1)):
            refs.append(m.group(1))
    return refs


def local_ref_problems(ref: str, root: Path) -> list[str]:
    folder = (root / ref).resolve()
    try:
        folder.relative_to(root.resolve())
    except ValueError:
        return [f"{ref}: points outside the repository"]
    files = [p for p in (folder / "action.yml", folder / "action.yaml") if p.is_file()]
    if not files:
        return [f"{ref}: no action.yml found; a local action must exist so its pins can be checked"]
    text = files[0].read_text(encoding="utf-8")
    return uses_problems(text, ref) + image_problems(text, ref)


class CiPinTests(unittest.TestCase):
    def test_every_workflow_uses_is_pinned(self):
        files = sorted(WORKFLOWS.glob("*.y*ml"))
        self.assertTrue(files, "no workflows found")
        problems = [p for f in files for p in uses_problems(f.read_text(encoding="utf-8"), f.name)]
        self.assertEqual(problems, [])

    def test_every_local_action_is_pinned(self):
        problems = []
        for f in local_actions():
            text = f.read_text(encoding="utf-8")
            problems += uses_problems(text, str(f.relative_to(ROOT)))
            problems += image_problems(text, str(f.relative_to(ROOT)))
        self.assertEqual(problems, [])

    def test_every_local_action_reference_resolves_and_is_pinned(self):
        problems = []
        for f in sorted(WORKFLOWS.glob("*.y*ml")):
            for ref in local_refs(f.read_text(encoding="utf-8")):
                problems += local_ref_problems(ref, ROOT)
        self.assertEqual(problems, [])

    def test_the_zap_scanner_is_one_dated_digest_pin_with_its_version_noted(self):
        # #123 (M3, G4-123-04 c): the moving version tag fails the cooldown, so the pin is the dated build
        # tag plus digest, and the comment names the ZAP version Marco approved.
        text = (ROOT / ".github" / "zap" / "Dockerfile").read_text(encoding="utf-8")
        froms = [ln for ln in text.splitlines() if re.match(r"^\s*FROM\b", ln, re.IGNORECASE)]
        self.assertEqual(len(froms), 1, froms)
        self.assertRegex(froms[0], r"^FROM docker\.io/zaproxy/zap-stable:\d{8}@sha256:[0-9a-f]{64} AS zap$")
        self.assertIn("# ZAP 2.17.0, dated build tag", text)
        self.assertEqual([ln for ln in text.splitlines() if ln.strip() and not ln.startswith("#")], froms,
                         "the file is never built: one FROM line and comments only")

    def test_every_precommit_rev_is_frozen(self):
        text = PRECOMMIT.read_text(encoding="utf-8")
        self.assertIn("rev:", text, "no rev found in .pre-commit-config.yaml")
        self.assertEqual(precommit_problems(text), [])


class CiPinDetectorTests(unittest.TestCase):
    """The detector itself fails closed (red cases)."""

    SHA = "11d5960a326750d5838078e36cf38b85af677262"

    def flags(self, text):
        return uses_problems(text, "x.yml")

    def test_pinned_step_and_reusable_workflow_pass(self):
        self.assertEqual(self.flags(f"      - uses: actions/checkout@{self.SHA}  # v4.4.0\n"), [])
        self.assertEqual(self.flags(f"    uses: org/repo/.github/workflows/w.yml@{self.SHA} # v1.2.3\n"), [])

    def test_tag_branch_short_sha_and_missing_comment_fail(self):
        for value in ("actions/checkout@v4", "actions/checkout@main", "actions/checkout@11d5960",
                      f"actions/checkout@{self.SHA}", f"actions/checkout@{self.SHA} # latest"):
            with self.subTest(value=value):
                self.assertTrue(self.flags(f"      - uses: {value}\n"))

    def test_job_level_reusable_workflow_by_tag_fails(self):
        self.assertTrue(self.flags("  call:\n    uses: org/repo/.github/workflows/w.yml@v1\n"))

    def test_docker_reference_needs_a_digest(self):
        self.assertTrue(self.flags("      - uses: docker://alpine:3.20\n"))
        self.assertEqual(self.flags("      - uses: docker://alpine@sha256:" + "a" * 64 + "\n"), [])
        self.assertTrue(image_problems("  image: docker://wagoid/x:6.2.1\n", "action.yml"))

    def test_unreadable_forms_fail(self):
        for line in ("      - uses: \"actions/checkout@v4\"\n", "      - uses: ${{ env.X }}\n",
                     "      - {uses: actions/checkout@v4}\n"):
            with self.subTest(line=line):
                self.assertTrue(self.flags(line))

    def test_local_action_is_resolved_not_skipped(self):
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.assertTrue(local_ref_problems("./.github/actions/missing", root), "a missing local action must fail")
            act = root / ".github" / "actions" / "x"
            act.mkdir(parents=True)
            (act / "action.yml").write_text("runs:\n  using: composite\n  steps:\n    - uses: actions/checkout@v4\n", encoding="utf-8")
            self.assertTrue(local_ref_problems("./.github/actions/x", root), "an unpinned inner uses: must fail")
            (act / "action.yml").write_text(f"runs:\n  using: composite\n  steps:\n    - uses: actions/checkout@{self.SHA}  # v4.4.0\n", encoding="utf-8")
            self.assertEqual(local_ref_problems("./.github/actions/x", root), [])
            self.assertTrue(local_ref_problems("./../outside", root))
        self.assertEqual(local_refs("      - uses: ./.github/actions/x\n"), ["./.github/actions/x"])

    def test_precommit_tag_rev_fails(self):
        self.assertTrue(precommit_problems("    rev: v8.30.1\n"))
        self.assertTrue(precommit_problems(f"    rev: {self.SHA}\n"))
        self.assertEqual(precommit_problems(f"    rev: {self.SHA}  # frozen: v8.30.1\n"), [])


if __name__ == "__main__":
    unittest.main()
