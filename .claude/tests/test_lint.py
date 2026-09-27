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

    def skill(self, name, frontmatter_extra="", body="Body.\n"):
        folder = self.root / ".claude" / "skills" / name
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "SKILL.md").write_text(
            f"---\nname: {name}\ndescription: d\n{frontmatter_extra}---\n{body}", encoding="utf-8")

    # #74 G4-74-07: Docker wildcard verbs
    def test_docker_wildcard_verbs_rejected(self):
        for tools in ("Bash(docker *)", "Bash(docker*)", "Bash(docker volume *)", "Bash(docker container *)",
                      "Bash(docker compose *)"):
            with self.subTest(tools=tools):
                lint.problems = []
                self.agent("alpha", tools=f"Read, {tools}")
                self.assertProblem("wildcard verb")

    def test_devops_style_docker_entries_pass(self):
        self.agent("alpha", tools="Read, Bash(docker compose up*), Bash(docker compose down*), Bash(docker ps*), "
                                  "Bash(docker logs*), Bash(docker volume ls*)")
        code, out = self.lint()
        self.assertEqual(code, 0, out)

    # #76: G4 routing and the roster check
    def routed(self, agent, paths, summary="x"):
        return {"deny": [".claude/**", "docs/ai/pipeline/**"], "agents": {"alpha": ["src/**"], "beta": ["docs/**"]},
                "routing": {"G4": [{"agent": agent, "summary": summary, "paths": paths}]}}

    def test_route_inside_lane_passes(self):
        self.boundaries(self.routed("alpha", "`src/**`, `src/Api/**` (endpoints)"))
        code, out = self.lint()
        self.assertEqual(code, 0, out)

    def test_route_outside_lane_rejected(self):
        self.boundaries(self.routed("alpha", "`deploy/**`"))
        self.assertProblem("outside alpha's lanes")

    def test_route_to_shared_deny_rejected(self):
        """G4-76-24: docs/** covers docs/ai/pipeline/**, but no agent can write there."""
        self.boundaries(self.routed("beta", "`docs/ai/pipeline/**`"))
        self.assertProblem("not a writable repository path")

    def test_malformed_routing_summary_names_the_key(self):
        """G4-76-09: a bad summary makes lint red with the key in the message."""
        self.boundaries(self.routed("alpha", "`src/**`", summary="a | b"))
        self.assertProblem("routing.G4[0].summary")

    def test_malformed_docker_key_fails_lint(self):
        """Story 4: a bad allow-list entry reaches lint.main() with its key named."""
        for docker, fragment in (({"alpha": "ps"}, "docker.alpha"),
                                 ({"alpha": [{"label": "x", "pattern": "(ps|container rm)\\b"}]}, "docker.alpha[0].pattern")):
            with self.subTest(fragment=fragment):
                lint.problems = []
                self.boundaries({"deny": [".claude/**"], "agents": {"alpha": ["src/**"], "beta": []}, "docker": docker})
                self.assertProblem(fragment)

    def test_skill_name_grammar(self):
        """G4-76-13."""
        folder = self.root / ".claude" / "skills" / "Bad_Skill"
        folder.mkdir(parents=True)
        (folder / "SKILL.md").write_text("---\nname: Bad_Skill\ndescription: d\n---\nBody.\n", encoding="utf-8")
        self.assertProblem("must match")

    def test_roster_exception_fails_lint(self):
        """G4-76-17: a stale roster and any exception each fail lint."""
        (self.root / "docs" / "ai").mkdir(parents=True)
        (self.root / "docs" / "ai" / "README.md").write_text("x\n", encoding="utf-8")
        from unittest import mock

        class Raising:
            @staticmethod
            def check(root):
                raise RuntimeError("boom")

        class Stale:
            @staticmethod
            def check(root):
                return ["roster: docs/ai/README.md is stale; run: python .claude/scripts/roster.py"]

        for module, fragment in ((Raising, "roster check failed (RuntimeError)"), (Stale, "is stale")):
            with self.subTest(fragment=fragment), mock.patch.object(lint, "_roster_module", return_value=module):
                lint.problems = []
                out = self.assertProblem(fragment)
                self.assertNotIn("boom", out)

    # #74 G4-74-06: tool grants that print container environments
    def test_environment_printing_docker_tools_rejected(self):
        for tools in ("Bash(docker inspect*)", "Bash(docker container inspect*)", "Bash(docker exec*)",
                      "Bash(docker cp*)", "Bash(docker compose config*)"):
            with self.subTest(tools=tools):
                lint.problems = []
                self.agent("alpha", tools=f"Read, {tools}")
                self.assertProblem("container environments")

    # #74 G4-74-09: skill frontmatter keys
    def test_skill_frontmatter_rejects_tool_and_hook_keys(self):
        for extra in ("allowed-tools: Bash\n", "hooks: x\n", "model: opus\n"):
            with self.subTest(extra=extra):
                lint.problems = []
                self.skill("sk", frontmatter_extra=extra)
                self.assertProblem("is not allowed in a skill")

    def test_skill_known_keys_pass(self):
        self.skill("sk", frontmatter_extra="argument-hint: <n>\ndisable-model-invocation: true\n")
        code, out = self.lint()
        self.assertEqual(code, 0, out)

    # #74 G4-74-10: risky instructions in skills and agents
    def test_risky_instructions_rejected_unless_forbidden(self):
        for text in ("Run docker exec x sh to check.", "Use WithBindMount for the realm.",
                     "Set TESTCONTAINERS_RYUK_DISABLED=true.", "Clean up with docker volume rm x."):
            with self.subTest(text=text):
                lint.problems = []
                self.skill("sk", body=text + "\n")
                self.assertProblem("risky instruction")
        lint.problems = []
        self.skill("sk", body="Never run docker exec, and do not use WithBindMount.\n")
        code, out = self.lint()
        self.assertEqual(code, 0, out)

    # G6-74-03: the negation must precede the match in the same clause
    def test_negation_elsewhere_on_the_line_does_not_exempt(self):
        for text in ("If the import did not run, clean up with docker volume rm x.",
                     "Run docker exec x sh; never mind the warning.",
                     "Do not panic. Use WithBindMount for the realm."):
            with self.subTest(text=text):
                lint.problems = []
                self.skill("sk", body=text + "\n")
                self.assertProblem("risky instruction")

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


class DotnetTestFilterRuleTests(unittest.TestCase):
    """#72: only solution-wide *inclusion* trait filters are banned (they exit 8 in every project
    without that trait); exclusion filters are the everyday/CI/pre-push command."""

    def rule(self):
        return next(b for b in lint.BANNED if "filter-trait" in b[0])

    def banned(self, line):
        import re
        pattern, _, allowed = self.rule()
        return bool(re.search(pattern, line)) and not re.search(allowed, line)

    def test_inclusion_filter_without_project_is_banned(self):
        # built at run time: the literal would trip this very rule when lint.py scans .claude/
        self.assertTrue(self.banned("dotnet test " + "--filter-" + 'trait "Category=Integration"'))

    def test_inclusion_filter_with_project_is_allowed(self):
        self.assertFalse(self.banned('dotnet test --project tests/X --filter-trait "Category=Integration"'))

    def test_exclusion_filters_are_allowed(self):
        self.assertFalse(self.banned('dotnet test --filter-not-trait "Category=Integration" --filter-not-trait "Category=AppHost"'))
