"""Pre-push check (#59, G4-59-01 to 11): CI parity, range from the hook's refs, fail-closed cases,
the package/build-logic warning, and redacted secret output.

Git scenarios run in a throwaway repository; nothing touches this clone's refs. Secret-shaped strings
are built at run time so this file never contains one (G4-39-57).
"""
import contextlib
import io
import random
import re
import shutil
import string
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE.parent / "scripts"))
import prepush  # noqa: E402

GIT = shutil.which("git")


def ci_text() -> str:
    return (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")


class CiParityTests(unittest.TestCase):
    """G4-59-04: the hook runs exactly CI's unit filters and skips on exactly CI's ignore list."""

    def test_unit_filters_match_ci(self):
        line = next(ln for ln in ci_text().splitlines() if "dotnet test --no-build" in ln and "Category=AppHost" in ln)
        pairs = re.findall(r'(--filter-not-trait)\s+"([^"]+)"', line)
        self.assertEqual([x for pair in pairs for x in pair], prepush.CI_UNIT_FILTERS)

    def test_ignore_regex_matches_ci(self):
        m = re.search(r"^\s*ignore='([^']+)'", ci_text(), re.MULTILINE)
        self.assertIsNotNone(m, "the changes job's ignore= line was not found in ci.yml")
        self.assertEqual(m.group(1), prepush.CI_IGNORE)

    def test_docs_only_push_skips_dotnet(self):
        self.assertFalse(prepush.needs_dotnet(["docs/x.md", ".claude/scripts/lint.py", "README.md"]))
        self.assertTrue(prepush.needs_dotnet(["docs/x.md", "src/Decisya.Api/Program.cs"]))


class WarningRuleTests(unittest.TestCase):
    """G4-59-09, 10, 11 (path rules need no git: warnings_for only diffs .csproj/.slnx)."""

    def test_package_and_build_files_warn_case_insensitively(self):
        for path in ("Directory.Packages.props", "directory.build.props", "Directory.Build.targets",
                     "src/X/NuGet.Config", "nuget.config", "global.json", ".config/dotnet-tools.json",
                     "src/Decisya.Web/package.json", "package-lock.json", ".npmrc", ".pre-commit-config.yaml",
                     ".gitleaks.toml", "BannedSymbols.txt", ".globalconfig", ".github/workflows/ci.yml",
                     "build/custom.targets", "src/X/X.rsp"):
            with self.subTest(path=path):
                self.assertEqual(len(prepush.warnings_for([path], "b", "t")), 1)

    def test_ordinary_files_do_not_warn(self):
        self.assertEqual(prepush.warnings_for(["src/X/Program.cs", "docs/a.md", ".github/dependabot.yml"], "b", "t"), [])

    def test_output_escapes_control_characters(self):
        self.assertEqual(prepush.safe("a\nb\x1b[31m"), "a\\x0ab\\x1b[31m")


@unittest.skipUnless(GIT, "needs git")
class GitScenarioTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.repo = Path(self.tmp.name)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "t@example.invalid")
        self.git("config", "user.name", "t")
        self.git("config", "commit.gpgsign", "false")
        self.commit("README.md", "hello\n", "docs: start")
        self.git("update-ref", "refs/remotes/origin/main", "HEAD")

    def tearDown(self):
        self.tmp.cleanup()

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.repo, capture_output=True, text=True, check=True).stdout.strip()

    def commit(self, rel, content, message):
        path = self.repo / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        self.git("add", rel)
        self.git("commit", "-q", "-m", message)
        return self.git("rev-parse", "HEAD")

    def test_branch_deletion_is_a_no_op(self):
        self.assertIsNone(prepush.resolve_range({"PRE_COMMIT_TO_REF": "0" * 40}, cwd=self.repo))

    def test_missing_origin_main_fails_closed(self):
        self.git("update-ref", "-d", "refs/remotes/origin/main")
        with self.assertRaises(prepush.RangeError) as ctx:
            prepush.resolve_range({}, cwd=self.repo)
        self.assertIn("git fetch origin", str(ctx.exception))

    def test_range_is_merge_base_to_pushed_ref(self):
        base = self.git("rev-parse", "HEAD")
        self.git("switch", "-q", "-c", "issue/1-x")
        first = self.commit("src/a.cs", "x\n", "feat: a")
        second = self.commit("src/b.cs", "y\n", "feat: b")
        self.assertEqual(prepush.resolve_range({"PRE_COMMIT_TO_REF": second}, cwd=self.repo), (base, second, True))
        self.assertEqual(prepush.resolve_range({"PRE_COMMIT_TO_REF": first}, cwd=self.repo), (base, first, False))
        self.assertEqual(prepush.changed_files(base, second, cwd=self.repo), ["src/a.cs", "src/b.cs"])

    def test_renames_and_deletions_count_and_csproj_tokens(self):
        base = self.git("rev-parse", "HEAD")
        self.commit("src/A/A.csproj", '<Project Sdk="Microsoft.NET.Sdk">\n</Project>\n', "feat: add project")
        mid = self.git("rev-parse", "HEAD")
        self.git("mv", "README.md", "Directory.Build.props")
        self.git("commit", "-q", "-m", "chore: rename")
        (self.repo / "src/A/A.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk">\n  <Target Name="X" />\n</Project>\n', encoding="utf-8")
        self.git("commit", "-q", "-am", "chore: target")
        to = self.git("rev-parse", "HEAD")
        found = dict(prepush.warnings_for(prepush.changed_files(mid, to, cwd=self.repo), mid, to, cwd=self.repo))
        self.assertIn("README.md", {p for p in prepush.changed_files(mid, to, cwd=self.repo)})
        self.assertIn("Directory.Build.props", found)
        self.assertIn("<Target", found["src/A/A.csproj"])
        plain = self.commit("src/A/A.csproj", '<Project Sdk="Microsoft.NET.Sdk">\n  <Target Name="X" />\n  <!-- note -->\n</Project>\n', "docs: comment")
        self.assertEqual(prepush.warnings_for(["src/A/A.csproj"], to, plain, cwd=self.repo), [], "a comment-only edit must not warn")
        del base

    def test_uncommitted_tracked_change_blocks_before_build(self):
        self.git("switch", "-q", "-c", "issue/2-y")
        self.commit("src/a.cs", "x\n", "feat: a")
        (self.repo / "src/a.cs").write_text("changed\n", encoding="utf-8")
        err = io.StringIO()
        with mock.patch.object(prepush, "ROOT", self.repo), \
                mock.patch.object(prepush, "run_step", return_value=True), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
            code = prepush.main(env={}, run_dotnet=False)
        self.assertEqual(code, 1)
        self.assertIn("uncommitted tracked changes", err.getvalue())

    def test_non_head_push_reports_not_run(self):
        self.git("switch", "-q", "-c", "issue/3-z")
        first = self.commit("src/a.cs", "x\n", "feat: a")
        self.commit("src/b.cs", "y\n", "feat: b")
        out = io.StringIO()
        with mock.patch.object(prepush, "ROOT", self.repo), \
                mock.patch.object(prepush, "run_step", return_value=True), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            code = prepush.main(env={"PRE_COMMIT_TO_REF": first}, run_dotnet=True)
        self.assertEqual(code, 0)
        self.assertIn("NOT RUN: the pushed ref is not HEAD", out.getvalue())

    @unittest.skipUnless(shutil.which("gitleaks"), "needs gitleaks")
    def test_secret_scan_blocks_and_redacts(self):
        """G4-59-07: the pre-push scan (same entry as .pre-commit-config.yaml) fails and never prints the token."""
        rnd = random.Random(59)
        token = "ghp_" + "".join(rnd.choice(string.ascii_letters + string.digits) for _ in range(36))
        self.commit("src/settings.txt", f"token = {token}\n", "chore: leak")
        entry = re.search(r"entry:\s*(gitleaks git[^\n]*)", (ROOT / ".pre-commit-config.yaml").read_text(encoding="utf-8"))
        self.assertIsNotNone(entry)
        self.assertIn("--redact", entry.group(1))
        r = subprocess.run(entry.group(1).split(), cwd=self.repo, capture_output=True, text=True, check=False)
        self.assertNotEqual(r.returncode, 0, "the fake token was not detected")
        self.assertNotIn(token, r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()


class HookInstallCheckTests(unittest.TestCase):
    """G4-59-02: prereqs.py hooks flags a clone without the pre-push shim."""

    def test_missing_and_foreign_hooks_are_flagged(self):
        import prereqs
        with tempfile.TemporaryDirectory() as tmp:
            hooks = Path(tmp)
            for name in ("pre-commit", "commit-msg"):
                (hooks / name).write_text(f"#!/bin/sh\n# {prereqs.SHIM_MARKER}\n", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(prereqs.check_hooks(hooks), 1)
            self.assertIn("MISSING  git hook pre-push", out.getvalue())
            (hooks / "pre-push").write_text("#!/bin/sh\necho hand-written\n", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(prereqs.check_hooks(hooks), 1, "a hook that is not pre-commit's shim must not count")
            (hooks / "pre-push").write_text(f"#!/bin/sh\n# {prereqs.SHIM_MARKER}\n", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(prereqs.check_hooks(hooks), 0)
