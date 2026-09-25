"""gates.py verdict parsing and artifact-path confinement (#39 item 6, G4-39-17 to 23)."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
import gates  # noqa: E402

ISSUE = 39
OK = "<!-- gate: G6 | verdict: PASS | issue: #39 -->"


class GateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / ".claude").mkdir()
        (self.root / ".claude" / "boundaries.json").write_text(json.dumps({"agents": {
            "security-reviewer": ["docs/security/**"], "product-owner": ["docs/requirements/**"],
            "architect": ["docs/**"], "test-engineer": ["docs/requirements/**"]}}), encoding="utf-8")
        self._root = gates.ROOT
        gates.ROOT = self.root

    def tearDown(self):
        gates.ROOT = self._root
        self.tmp.cleanup()

    def check(self, text, artifact="docs/security/reviews/39.md", gate="G6", bom=False):
        path = self.root / artifact
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        return gates.check_gate(ISSUE, gate, {"status": "required", "artifact": artifact, "note": ""})

    # green
    def test_own_line_pass(self):
        self.assertTrue(self.check(f"{OK}\n# Review\n")[0])

    def test_bom_first_line(self):
        self.assertTrue(self.check(f"{OK}\n# Review\n", bom=True)[0])

    def test_verdict_at_end_of_file(self):
        self.assertTrue(self.check(f"# Review\n\nbody\n\n{OK}\n")[0])

    def test_placeholder_example_does_not_compete(self):
        self.assertTrue(self.check(f"{OK}\n```\n<!-- gate: G6 | verdict: BLOCK | issue: #<n> -->\n```\n")[0])

    def test_na_with_reason(self):
        self.assertTrue(self.check("<!-- gate: G6 | verdict: N/A | issue: #39 | reason: nothing to review -->\n")[0])

    # red
    def test_block_hidden_in_fence_then_pass(self):
        ok, msg = self.check(f"```\n<!-- gate: G6 | verdict: BLOCK | issue: #39 -->\n```\n{OK}\n")
        self.assertFalse(ok)
        self.assertIn("AMBIGUOUS", msg)

    def test_own_block_with_fenced_pass(self):
        self.assertFalse(self.check(f"<!-- gate: G6 | verdict: BLOCK | issue: #39 -->\n```\n{OK}\n```\n")[0])

    def test_indented_pass_only(self):
        ok, msg = self.check(f"    {OK}\n")
        self.assertFalse(ok)
        self.assertIn("NO VERDICT", msg)

    def test_mid_sentence_copy_competes(self):
        ok, msg = self.check(f"{OK}\nAs quoted: {OK} here.\n")
        self.assertFalse(ok)
        self.assertIn("AMBIGUOUS", msg)

    def test_unknown_verdict(self):
        ok, msg = self.check("<!-- gate: G6 | verdict: OK | issue: #39 -->\n")
        self.assertFalse(ok)
        self.assertIn("UNKNOWN VERDICT", msg)

    def test_na_without_reason(self):
        self.assertFalse(self.check("<!-- gate: G6 | verdict: N/A | issue: #39 -->\n")[0])

    def test_wrong_issue(self):
        self.assertFalse(self.check("<!-- gate: G6 | verdict: PASS | issue: #16 -->\n")[0])

    def test_invalid_paths_are_not_read(self):
        for artifact in ("../x.md", "docs/../../x.md", "C:/x.md", "/tmp/x.md", "docs\\security\\x.md"):
            with self.subTest(artifact=artifact):
                ok, msg = gates.check_gate(ISSUE, "G6", {"status": "required", "artifact": artifact, "note": ""})
                self.assertFalse(ok)
                self.assertIn("INVALID PATH", msg)

    def test_artifact_outside_owner_lane(self):
        ok, msg = self.check(f"{OK}\n", artifact="docs/requirements/x.md")
        self.assertFalse(ok)
        self.assertIn("write lane", msg)

    def test_directory_is_not_an_artifact(self):
        (self.root / "docs" / "security" / "reviews" / "39.md").mkdir(parents=True)
        ok, msg = gates.check_gate(ISSUE, "G6", {"status": "required", "artifact": "docs/security/reviews/39.md", "note": ""})
        self.assertFalse(ok)
        self.assertIn("not a regular file", msg)

    def test_symlink_in_lane_to_another_lane(self):
        """G6-39-12: the lane is checked on the resolved target too."""
        target = self.root / "docs" / "requirements" / "x.md"
        target.parent.mkdir(parents=True)
        target.write_text(f"{OK}\n", encoding="utf-8")
        link = self.root / "docs" / "security" / "reviews" / "39.md"
        link.parent.mkdir(parents=True)
        try:
            link.symlink_to(target)
        except OSError:
            self.skipTest("symlinks need Developer Mode or admin on Windows")
        ok, msg = gates.check_gate(ISSUE, "G6", {"status": "required", "artifact": "docs/security/reviews/39.md", "note": ""})
        self.assertFalse(ok)
        self.assertIn("write lane", msg)


if __name__ == "__main__":
    unittest.main()
