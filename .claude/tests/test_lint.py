"""lint.py: boundaries.json schema and two-way agent match, wildcard gh/dotnet verbs (#39 items 2, 4, 5).

Each test runs lint.main() against a temporary .claude/ tree (G4-39-06, 07, 12).
"""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "scripts"))
import lint  # noqa: E402

GOOD_TOOLS = "Read, Bash(dotnet build*), Bash(gh issue view*), Bash(git diff*)"


class LintTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / ".claude" / "agents").mkdir(parents=True)
        (self.root / ".claude" / "skills").mkdir()
        (self.root / "CLAUDE.md").write_text("# test\n", encoding="utf-8")
        for name in ("alpha", "beta"):
            self.agent(name)
        self.boundaries({"deny": [".claude/**"], "agents": {"alpha": ["src/**"], "beta": []}})
        self.saved = (lint.ROOT, lint.CLAUDE, lint.problems)
        lint.ROOT, lint.CLAUDE, lint.problems = self.root, self.root / ".claude", []

    def tearDown(self):
        lint.ROOT, lint.CLAUDE, lint.problems = self.saved
        self.tmp.cleanup()

    def agent(self, stem, name=None, tools=GOOD_TOOLS):
        (self.root / ".claude" / "agents" / f"{stem}.md").write_text(
            f"---\nname: {name or stem}\ndescription: d\ntools: {tools}\n---\nBody.\n", encoding="utf-8")

    def boundaries(self, config):
        text = json.dumps(config, indent=2) if isinstance(config, dict) else config
        (self.root / ".claude" / "boundaries.json").write_text(text, encoding="utf-8")

    def lint(self):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            code = lint.main()
        return code, out.getvalue()

    def assertProblem(self, fragment):
        code, out = self.lint()
        self.assertEqual(code, 1, out)
        self.assertIn(fragment, out)
        return out

    def test_clean_tree_passes(self):
        code, out = self.lint()
        self.assertEqual(code, 0, out)

    # G4-39-06
    def test_agent_without_entry(self):
        self.agent("gamma")
        out = self.assertProblem("agent 'gamma' has no boundaries.json entry")
        self.assertIn(".claude/agents/gamma.md:2:", out)

    def test_entry_without_agent(self):
        self.boundaries({"agents": {"alpha": ["src/**"], "beta": [], "ghost": ["docs/**"]}})
        out = self.assertProblem("boundaries for 'ghost'")
        self.assertRegex(out, r"\.claude/boundaries\.json:\d+:")

    def test_name_differs_from_stem_and_name_is_what_counts(self):
        self.agent("beta", name="beta2")
        out = self.assertProblem("name 'beta2' differs from file name 'beta'")
        self.assertIn("agent 'beta2' has no boundaries.json entry", out)

    # G4-39-07
    def test_schema_violations(self):
        for bad in ({"agents": {"alpha": ["src/**"], "beta": []}, "extra": 1},
                    {"deny": "x", "agents": {"alpha": [], "beta": []}},
                    {"agents": {"alpha": ["/abs/**"], "beta": []}},
                    {"agents": {"alpha": ["C:/x/**"], "beta": []}},
                    {"agents": {"alpha": ["src/../x"], "beta": []}},
                    {"agents": {"alpha": [""], "beta": []}},
                    {"agents": {"alpha": "src/**", "beta": []}},
                    {"deny": []}):
            with self.subTest(bad=bad):
                lint.problems = []
                self.boundaries(bad)
                self.assertProblem("boundaries.json schema")

    def test_invalid_json(self):
        self.boundaries("{ not json")
        self.assertProblem("boundaries.json is not valid JSON")

    # G4-39-12
    def test_wildcard_verbs_rejected(self):
        for tools in ("Bash(gh issue *)", "Bash(gh run *)", "Bash(gh *)", "Bash(dotnet *)", "Bash(dotnet*)",
                      "Bash(gh issue*)", "Bash(dotnet new *)", "Bash(dotnet tool *)",
                      "Bash(dotnet p*)", "Bash(dotnet tool in*)", "Bash(gh issue c*)", "Bash(gh pr m*)"):
            with self.subTest(tools=tools):
                lint.problems = []
                self.agent("alpha", tools=f"Read, {tools}")
                self.assertProblem("wildcard verb")

    def test_explicit_verbs_allowed(self):
        self.agent("alpha", tools="Read, Bash(dotnet build*), Bash(dotnet new list*), Bash(dotnet tool restore*), "
                                  "Bash(dotnet --version*), Bash(gh issue view*), Bash(gh run list*), Bash(git log*)")
        code, out = self.lint()
        self.assertEqual(code, 0, out)


if __name__ == "__main__":
    unittest.main()
