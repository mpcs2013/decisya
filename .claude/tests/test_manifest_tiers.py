"""Every issue manifest states its tier in the one form Norma 0.4.1's gates.py reads (#281, G3 M-3).

0.4.1 fails an issue on a tier line it cannot read, and reads a missing or unreadable tier as Full. #281 gave every
manifest one exact `- Tier: light|full` line and kept the old wording under `Tier, as recorded`. This test keeps it
so: at most one tier-shaped line, in the exact form; a Light manifest's G3 N/A is the light tier's own; and no
issue up to #281 changes tier.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PIPELINE = ROOT / "docs" / "ai" / "pipeline"
# The same two patterns as Norma 0.4.1's gates.py (TIER_LINE, TIER_LIKE).
TIER_LINE = re.compile(r"- Tier:[ \t]*(\S+)[ \t]*")
TIER_LIKE = re.compile(r"[ \t]*[-*+][ \t]*[*_]{0,2}[ \t]*tier[ \t]*[*_]{0,2}[ \t]*:", re.IGNORECASE)
LIGHT_REASON = re.compile(r"reason:\s*light tier\b", re.IGNORECASE)
# The Light issues up to #281, by their own records (#281; #87 is Full, Marco 2026-11-06).
LIGHT_UP_TO_281 = {84, 116, 124, 145, 147, 166, 185, 192, 217, 244, 247}


def manifests():
    for path in PIPELINE.glob("*.md"):
        if path.stem.isdigit():
            yield int(path.stem), path


def head(path):
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    return lines[:next((i for i, line in enumerate(lines) if line.startswith("|")), len(lines))]


def tier(path):
    for line in head(path):
        m = TIER_LINE.fullmatch(line)
        if m and m.group(1).lower() in ("light", "full"):
            return m.group(1).lower()
    return None


class ManifestTierTests(unittest.TestCase):
    def test_one_exact_tier_line_at_most(self):
        for n, path in manifests():
            tiers = [line for line in head(path) if TIER_LIKE.match(line)]
            with self.subTest(issue=n):
                self.assertLessEqual(len(tiers), 1, "keep one tier line; put notes under 'Tier, as recorded'")
                for line in tiers:
                    m = TIER_LINE.fullmatch(line)
                    self.assertTrue(m and m.group(1) in ("light", "full"), f"not read by gates.py: {line[:60]}")

    def test_a_light_manifest_has_a_light_g3(self):
        for n, path in manifests():
            if tier(path) != "light":
                continue
            row = next((line for line in path.read_text(encoding="utf-8-sig").splitlines()
                        if line.startswith("| G3 |")), "")
            cells = [c.strip() for c in row.strip("|").split("|")]
            with self.subTest(issue=n):
                self.assertTrue(cells and len(cells) >= 4, "no G3 row")
                if cells[2] == "skipped":
                    continue
                review = (ROOT / cells[3]).read_text(encoding="utf-8")
                g3 = [line for line in review.splitlines() if re.match(r"<!-- gate: G3 \|", line)]
                self.assertEqual(len(g3), 1, f"{cells[3]} needs one G3 line")
                # A Light issue that touched a review-required path ran a real threat delta; an N/A must be the
                # light tier's own (a bug-fix N/A on an auth change, as #87's, is Full).
                if re.search(r"verdict:\s*N/A\b", g3[0]):
                    self.assertRegex(g3[0], LIGHT_REASON, "a Light issue's G3 N/A carries 'reason: light tier'")

    def test_no_issue_up_to_281_changes_tier(self):
        light = {n for n, path in manifests() if n <= 281 and tier(path) == "light"}
        self.assertEqual(light, LIGHT_UP_TO_281)

    def test_the_old_wording_is_kept_and_not_read_as_a_tier(self):
        for line in ("- Tier, as recorded: Full, all gates (Marco 2026-09-27).",
                     "- **Tier, as recorded:** light: Dependabot configuration"):
            with self.subTest(line=line):
                self.assertIsNone(TIER_LIKE.match(line))
        for line in ("- Tier: Full, all gates", "- **Tier:** full", "- Tier: **full**: it", "- tier: light"):
            with self.subTest(line=line):
                self.assertTrue(TIER_LIKE.match(line) and not
                                (TIER_LINE.fullmatch(line) and TIER_LINE.fullmatch(line).group(1) in ("light", "full")))


class InstalledGatesTierTests(unittest.TestCase):
    """#281 (G3 S-2, G6-281-01): the installed gates.py itself refuses an unreadable or second tier line, so a
    later Norma release that loosens tier parsing turns this red, not only the copies above."""

    @classmethod
    def setUpClass(cls):
        import importlib.util
        spec = importlib.util.spec_from_file_location("_norma_gates_tiers", ROOT / ".claude" / "scripts" / "gates.py")
        cls.gates = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.gates)

    def test_the_copied_patterns_are_the_installed_ones(self):
        self.assertEqual(TIER_LINE.pattern, self.gates.TIER_LINE.pattern)
        self.assertEqual(TIER_LIKE.pattern, self.gates.TIER_LIKE.pattern)
        self.assertEqual(TIER_LIKE.flags, self.gates.TIER_LIKE.flags)

    def test_gates_flags_a_note_or_a_second_tier_line(self):
        import tempfile
        from unittest import mock
        table = "\n| Gate | Owner | Status | Artifact | Note |\n| --- | --- | --- | --- | --- |\n"
        cases = {
            1: ("- Tier: light (Marco 2026-11-06)\n", "full", True),        # a note: not read, so Full and flagged
            2: ("- Tier: light\n- **Tier:** light\n", "light", True),       # two tier lines: flagged
            3: ("- Tier: light\n- Tier, as recorded: light (note)\n", "light", False),  # #281's form: clean
        }
        with tempfile.TemporaryDirectory() as folder:
            for n, (head, _, _) in cases.items():
                (Path(folder) / f"{n}.md").write_text(f"# Pipeline – issue #{n}\n\n{head}{table}", encoding="utf-8")
            with mock.patch.object(self.gates, "PIPELINE", Path(folder)):
                for n, (_, tier_read, flagged) in cases.items():
                    with self.subTest(case=n):
                        self.assertEqual(self.gates.manifest_tier(n), tier_read)
                        self.assertEqual(self.gates.tier_problem(n) is not None, flagged)


if __name__ == "__main__":
    unittest.main()
