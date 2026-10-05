"""ops.local/ holds local-only operations notes (home network and NAS hand-over, #124): private network
addresses and account names. It is never tracked, never committed and never part of an image build context.
"""
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which("git"), "needs git")
class LocalOnlyPathTests(unittest.TestCase):
    def git(self, *args):
        return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True)

    def test_ops_local_is_ignored_at_any_depth(self):
        for path in ("ops.local/notes.md", "ops.local/diagram.png", "ops.local/sub/dir/file.txt"):
            with self.subTest(path=path):
                self.assertEqual(self.git("check-ignore", "-q", path).returncode, 0, f"{path} is not ignored")

    def test_nothing_under_ops_local_is_tracked(self):
        self.assertEqual(self.git("ls-files", "--", "ops.local").stdout.strip(), "")

    def test_ops_local_is_outside_the_build_context(self):
        lines = (ROOT / ".dockerignore").read_text(encoding="utf-8").splitlines()
        self.assertIn("ops.local", [line.strip() for line in lines])


if __name__ == "__main__":
    unittest.main()
