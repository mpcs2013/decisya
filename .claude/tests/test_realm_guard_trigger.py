"""#77: every file the realm guard scans also triggers the .NET tests, in CI and in the pre-push hook
(docs/security/threat-models/realm-guard-scope.md, G4-77-05 to 08).

The shared case table tests/Decisya.Identity.Tests/realm-guard-cases.json drives both the .NET
guard (RealmGuardTests) and this test. CI's decision is taken by running the block between the
`lanes` markers of ci.yml in bash, so both sides are compared by behaviour, not by regex text.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE.parent / "scripts"))
import prepush  # noqa: E402

CI = ROOT / ".github" / "workflows" / "ci.yml"
CASES = ROOT / "tests" / "Decisya.Identity.Tests" / "realm-guard-cases.json"
BASH = shutil.which("bash")  # full path: on Windows a bare "bash" resolves to System32's WSL launcher first

# Extra rows from G4-77-08, beyond the case table.
EXTRA = ["ALL", "docs/x.md", "README.md", "src/Decisya.Api/Program.cs", ".claude/skills/k/SKILL.md", ".vscode/tasks.json",
         # G6-77-03: a nested .claude/ area is code too, Markdown included
         "src/Decisya.Web/.claude/skills/k/SKILL.md", "tools/.claude/settings.json"]
# The one known difference: CI keeps SPA-only changes out of the .NET lane (its Web filter); the
# pre-push hook is stricter. The always-run realm-guard job covers the guard for these (G4-77-12).
EXPECTED_DIFFERENCE = {"src/Decisya.Web/package.json": (False, True), "src/Decisya.Web/src/x.ts": (False, True),
                       # a nested .claude/ inside the SPA: the Web filter wins in CI (G6-77-03)
                       "src/Decisya.Web/.claude/skills/k/SKILL.md": (False, True)}


def cases() -> list[dict]:
    return json.loads(CASES.read_text(encoding="utf-8"))


def lanes_block() -> str:
    m = re.search(r"# lanes: begin\n(.*?)\n\s*# lanes: end", CI.read_text(encoding="utf-8"), re.DOTALL)
    assert m, "lanes markers not found in ci.yml"
    return "\n".join(line.strip() for line in m.group(1).splitlines())


def ci_dotnet(paths: list[str]) -> bool:
    """CI's `dotnet=` output for a changed-file list, from the real ci.yml block."""
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "out"
        out.write_text("", encoding="utf-8")
        env = {**os.environ, "CHANGED": "\n".join(paths), "GITHUB_OUTPUT": str(out)}
        # -e as GitHub Actions runs `run:` steps (bash --noprofile --norc -eo pipefail), G6-77-08
        subprocess.run([BASH, "--noprofile", "--norc", "-eo", "pipefail", "-c", 'changed="$CHANGED"\n' + lanes_block()],
                       cwd=ROOT, env=env, check=True, capture_output=True, text=True)
        values = dict(line.split("=", 1) for line in out.read_text(encoding="utf-8").splitlines() if "=" in line)
    return values["dotnet"] == "true"


class GuardScopeTriggerTests(unittest.TestCase):
    """G4-77-07: a file the guard scans can never skip the .NET tests."""

    def test_case_table_is_shared_and_complete(self):
        rows = cases()
        self.assertGreater(len(rows), 20)
        for row in rows:
            self.assertEqual(set(row), {"path", "content", "offends", "in_scope"})

    def test_every_in_scope_path_triggers_prepush(self):
        for row in cases():
            if row["in_scope"]:
                with self.subTest(path=row["path"]):
                    self.assertTrue(prepush.needs_dotnet([row["path"]]), "in the guard's scope but skips .NET on push")

    @unittest.skipUnless(BASH, "needs bash")
    def test_every_in_scope_path_triggers_ci(self):
        in_scope = [row["path"] for row in cases() if row["in_scope"] and row["path"] not in EXPECTED_DIFFERENCE]
        for path in in_scope:
            with self.subTest(path=path):
                self.assertTrue(ci_dotnet([path]), "in the guard's scope but CI skips .NET")

    def test_now_triggering_paths(self):
        """G4-77-10 item 4: false before #77, true now; docs stay fast."""
        for path in (".claude/scripts/lint.py", ".claude/tests/test_x.py", ".claude/skills/k/SKILL.md",
                     ".vscode/tasks.json", ".github/workflows/release.yml"):
            with self.subTest(path=path):
                self.assertTrue(prepush.needs_dotnet([path]))
        self.assertFalse(prepush.needs_dotnet(["docs/x.md"]))

    def test_nested_claude_area_triggers(self):
        """G6-77-03: `.claude/` below the root is still executable config."""
        for path in ("src/Decisya.Web/.claude/skills/k/SKILL.md", "tools/.claude/settings.json"):
            with self.subTest(path=path):
                self.assertTrue(prepush.needs_dotnet([path]))
        self.assertFalse(prepush.needs_dotnet(["src/x.claude/notes.md"]))  # not a .claude directory


@unittest.skipUnless(BASH, "needs bash")
class BehaviourParityTests(unittest.TestCase):
    """G4-77-08: CI and the pre-push hook make the same decision for every row."""

    def test_same_decision(self):
        paths = sorted({row["path"] for row in cases()} | set(EXTRA) | set(EXPECTED_DIFFERENCE))
        for path in paths:
            with self.subTest(path=path):
                ci, hook = ci_dotnet([path]), prepush.needs_dotnet([path])
                if path in EXPECTED_DIFFERENCE:
                    self.assertEqual((ci, hook), EXPECTED_DIFFERENCE[path], "the documented Web-lane difference changed")
                else:
                    self.assertEqual(ci, hook)

    def test_unknown_base_runs_everything(self):
        """T77-12: `changed=ALL` (first or force push) keeps the .NET lane."""
        self.assertTrue(ci_dotnet(["ALL"]))

    def test_docs_only_stays_fast_in_both(self):
        for paths in (["docs/x.md"], ["docs/x.md", "README.md", "LICENSE", ".github/dependabot.yml"]):
            with self.subTest(paths=paths):
                self.assertFalse(ci_dotnet(paths))
                self.assertFalse(prepush.needs_dotnet(paths))


if __name__ == "__main__":
    unittest.main()
