"""#217: no test may start System32's bash.exe (the WSL launcher); bash comes only from gitbash.py."""
import os
import re
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import gitbash

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent


def touch(path: Path) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"")
    return path


class FindBashTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.system = self.tmp / "Windows"
        touch(self.system / "System32" / "bash.exe")

    def test_windows_takes_bash_from_gits_install_folder_not_from_path(self):
        git = touch(self.tmp / "Git" / "cmd" / "git.exe")
        bash = touch(self.tmp / "Git" / "bin" / "bash.exe")
        which = {"git": str(git), "bash": str(self.system / "System32" / "bash.exe")}.get
        self.assertEqual(gitbash.find_bash(which, windows=True, system_root=str(self.system)), str(bash.resolve()))

    def test_windows_never_returns_a_bash_under_the_windows_folder(self):
        git = touch(self.system / "System32" / "git.exe")
        touch(self.system / "bin" / "bash.exe")
        which = {"git": str(git), "bash": str(self.system / "System32" / "bash.exe")}.get
        self.assertIsNone(gitbash.find_bash(which, windows=True, system_root=str(self.system)))

    def test_windows_without_git_or_git_bash_finds_none(self):
        which = {"bash": str(self.system / "System32" / "bash.exe")}.get
        self.assertIsNone(gitbash.find_bash(which, windows=True, system_root=str(self.system)))
        git = touch(self.tmp / "Git" / "cmd" / "git.exe")
        which = {"git": str(git), "bash": str(self.system / "System32" / "bash.exe")}.get
        self.assertIsNone(gitbash.find_bash(which, windows=True, system_root=str(self.system)))

    def test_elsewhere_bash_is_the_one_on_path(self):
        self.assertEqual(gitbash.find_bash({"bash": "/usr/bin/bash"}.get, windows=False), "/usr/bin/bash")

    @unittest.skipUnless(os.name == "nt" and shutil.which("git"), "Windows with git only")
    def test_this_host_with_system32_first_on_path_still_gets_git_bash(self):
        system32 = str(Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32")
        git_cmd = str(Path(shutil.which("git")).parent)
        with mock.patch.dict(os.environ, {"PATH": os.pathsep.join([system32, git_cmd])}):
            bash = gitbash.find_bash()
        self.assertIsNotNone(bash, gitbash.MISSING)
        self.assertFalse(Path(bash).resolve().is_relative_to(Path(system32).parent.resolve()), bash)


class HostTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("git"), "needs git")
    def test_bash_is_found_where_git_is(self):
        # The one visible failure when Git Bash is missing; the bash-based tests skip on it.
        self.assertIsNotNone(gitbash.BASH, gitbash.MISSING)


class NoBareBashLookupTests(unittest.TestCase):
    # Built from parts so this file does not match its own pattern. Catches a lookup or a list or
    # tuple that starts bash or wsl, with or without .exe, in any case (G6-217 L-1).
    LOOKUP = re.compile(r"""(?i)(which\(|[\[(])\s*["'](""" + "ba" + "sh|ws" + "l" + r""")(\.exe)?["']""")

    def test_the_pattern_flags_every_form_that_reaches_wsl(self):
        b, w = "ba" + "sh", "ws" + "l"
        flagged = [f'shutil.which("{b}")', f"shutil.which('{b}.exe')", f'subprocess.run(["{b}", "-c", s])',
                   f'subprocess.run(("{b.capitalize()}", "-c"))', f'["{w}.exe", "-d"]', f"[ '{w.upper()}' ]"]
        passed = ["from gitbash import BASH", "subprocess.run([BASH, script])", f'{{"git": g, "{b}": x}}.get',
                  f'["sh", "-c", s]']
        self.assertEqual([line for line in flagged if not self.LOOKUP.search(line)], [])
        self.assertEqual([line for line in passed if self.LOOKUP.search(line)], [])

    def test_no_test_looks_up_bash_outside_gitbash(self):
        offenders = []
        for folder in (ROOT / ".claude" / "tests", ROOT / "deploy" / "tests"):
            for path in sorted(folder.rglob("*.py")):
                if path.name == "gitbash.py":
                    continue
                for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
                    if self.LOOKUP.search(line):
                        offenders.append(f"{path.relative_to(ROOT)}:{number}")
        self.assertEqual(offenders, [], "take bash from gitbash.BASH (#217)")


if __name__ == "__main__":
    unittest.main()
