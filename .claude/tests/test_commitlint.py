"""The local commitlint mirror matches the real commitlint 19 golden outputs byte for byte."""
import contextlib
import io
import json
import re
import shutil
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

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

    def test_ci_pins_the_version_the_goldens_came_from(self):
        """G4-59-23: a wagoid bump fails here until the goldens are regenerated with it."""
        source = json.loads((FIXTURES / "source.json").read_text(encoding="utf-8"))
        ci = (HERE.parents[1] / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        m = re.search(r"uses:\s*wagoid/commitlint-github-action@(\S+)(?:\s+#\s*(v\S+))?", ci)
        self.assertIsNotNone(m, "commitlint action not found in ci.yml")
        pinned = m.group(2) or m.group(1)  # SHA pins (0.16) carry the version as a `# vX.Y.Z` comment
        self.assertEqual(pinned, source["action_version"])

    def test_range_rejects_option_like_values_without_calling_git(self):
        """G4-59-25."""
        for bad in ("--output=x", "-p", "main", "a..-b", "a..b;rm", "a b..c"):
            with self.subTest(bad=bad), mock.patch.object(commitlint.subprocess, "run") as run, \
                    contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(commitlint.main(["--range", bad]), 2)
                run.assert_not_called()

    @unittest.skipUnless(shutil.which("git"), "needs git")
    def test_range_passes_end_of_options_to_git(self):
        with mock.patch.object(commitlint.subprocess, "run") as run:
            run.return_value = subprocess.CompletedProcess([], 0, stdout="", stderr="")
            self.assertEqual(commitlint.main(["--range", "origin/main..HEAD"]), 0)
        self.assertEqual(run.call_args_list[0].args[0][:4], ["git", "rev-list", "--reverse", "--end-of-options"])

    @unittest.skipUnless(shutil.which("git"), "needs git")
    def test_git_strip_matches_git_stripspace(self):
        """G6-39-14: blank-line runs, trailing spaces, comments and blank ends as git cleans them."""
        raw = "\n\nfeat: x  \n\n\n\nbody line\t\n# comment\n\n\nRefs #39\n\n\n"
        real = subprocess.run(["git", "stripspace", "-s"], input=raw, capture_output=True, text=True, check=True).stdout
        self.assertEqual(commitlint.git_strip(raw), real.rstrip("\n"))


if __name__ == "__main__":
    unittest.main()
