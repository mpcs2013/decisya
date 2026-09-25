"""The local commitlint mirror matches the real commitlint 19 golden outputs byte for byte."""
import json
import shutil
import subprocess
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
import commitlint  # noqa: E402

FIXTURES = HERE / "fixtures" / "commitlint"


class GoldenTests(unittest.TestCase):
    def test_every_fixture_matches_golden_output_and_exit_code(self):
        expected = json.loads((FIXTURES / "expected.json").read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(expected), 29)
        for name, code in sorted(expected.items()):
            with self.subTest(fixture=name):
                message = (FIXTURES / f"{name}.msg").read_text(encoding="utf-8")
                out, got = commitlint.report(message)
                self.assertEqual(got, code)
                self.assertEqual(out, (FIXTURES / f"{name}.expected").read_text(encoding="utf-8"))

    @unittest.skipUnless(shutil.which("git"), "needs git")
    def test_git_strip_matches_git_stripspace(self):
        """G6-39-14: blank-line runs, trailing spaces, comments and blank ends as git cleans them."""
        raw = "\n\nfeat: x  \n\n\n\nbody line\t\n# comment\n\n\nRefs #39\n\n\n"
        real = subprocess.run(["git", "stripspace", "-s"], input=raw, capture_output=True, text=True, check=True).stdout
        self.assertEqual(commitlint.git_strip(raw), real.rstrip("\n"))


if __name__ == "__main__":
    unittest.main()
