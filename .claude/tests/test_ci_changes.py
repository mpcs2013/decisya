"""ci.yml `changes` job: the changed-file list is printed with workflow commands paused (#39 item 8).

Runs the block between the `print-changed` markers in bash with a fake list that contains a
workflow command (G4-39-33, 34).
"""
import re
import shutil
import subprocess
import unittest
from pathlib import Path

CI = Path(__file__).resolve().parents[2] / ".github" / "workflows" / "ci.yml"
BASH = shutil.which("bash")  # full path: on Windows a bare "bash" resolves to System32's WSL launcher first


def print_block() -> str:
    m = re.search(r"# print-changed: begin\n(.*?)\n\s*# print-changed: end", CI.read_text(encoding="utf-8"), re.DOTALL)
    assert m, "print-changed markers not found in ci.yml"
    return "\n".join(line.strip() for line in m.group(1).splitlines())


@unittest.skipUnless(BASH and shutil.which("openssl"), "needs bash and openssl")
class ChangesJobTests(unittest.TestCase):
    def run_block(self):
        script = 'changed=$(printf "src/a.cs\\n::warning::pwned\\n::add-mask::x")\n' + print_block()
        return subprocess.run([BASH, "-c", script], capture_output=True, text=True, check=True).stdout.splitlines()

    def test_list_is_wrapped_between_stop_and_resume(self):
        out = self.run_block()
        stop = re.fullmatch(r"::stop-commands::([0-9a-f]{32})", out[0])
        self.assertTrue(stop, out)
        self.assertEqual(out[-1], f"::{stop.group(1)}::")
        self.assertIn("::warning::pwned", out[1:-1])
        self.assertEqual(sum(stop.group(1) in line for line in out), 2, "token printed elsewhere")

    def test_token_is_random_per_run(self):
        self.assertNotEqual(self.run_block()[0], self.run_block()[0])

    def test_nothing_between_markers_writes_outputs(self):
        self.assertNotRegex(print_block(), r"GITHUB_(OUTPUT|ENV)")


if __name__ == "__main__":
    unittest.main()
