"""gates.py verdict parsing and artifact-path confinement (#39 item 6, G4-39-17 to 23)."""
import contextlib
import io
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


class ReviewRequiredTests(unittest.TestCase):
    """#74: lane, tool, hook or permission changes always need G3 and G6."""

    def rows(self, g3, g6):
        return {"G3": {"status": g3}, "G6": {"status": g6}}

    def test_lane_change_with_skipped_reviews_fails(self):
        for path in (".claude/boundaries.json", ".claude/agents/platform-dev.md", ".claude/hooks/agent_boundaries.py",
                     ".claude/settings.json", ".claude/scripts/gates.py", ".claude/scripts/lint.py", ".claude/agents/sub/x.md",
                     ".claude/hooks/data.json", ".github/workflows/ci.yml"):
            with self.subTest(path=path):
                problem = gates.review_required_problem([path], self.rows("skipped", "skipped"))
                self.assertIsNotNone(problem)
                self.assertIn("G3 and G6 must run", problem)
        self.assertIn("G6 must run", gates.review_required_problem([".claude/settings.json"], self.rows("passed", "skipped")))

    def test_lane_change_with_reviews_passes(self):
        for g3, g6 in (("required", "required"), ("passed", "required"), ("passed", "passed")):
            with self.subTest(g3=g3, g6=g6):
                self.assertIsNone(gates.review_required_problem([".claude/boundaries.json"], self.rows(g3, g6)))

    def test_every_script_and_the_docker_fixtures_need_review(self):
        """#76 G4-76-22/23: any module in scripts/ can shadow an import of the checkers."""
        for path in (".claude/scripts/json.py", ".claude/scripts/_claude_cfg.py", ".claude/scripts/roster.py",
                     ".claude/scripts/commitlint.py", ".claude/tests/test_agent_roster.py", ".claude/tests/test_hooks.py",
                     ".claude/skills/issue/SKILL.md"):
            with self.subTest(path=path):
                self.assertIsNotNone(gates.review_required_problem([path], self.rows("skipped", "skipped")))

    def test_other_files_need_no_review(self):
        for path in (".claude/skills/keycloak/SKILL.md", "docs/x.md", "docs/ai/README.md", "src/X.cs",
                     ".claude/tests/test_x.py", ".github/dependabot.yml", ".claude/scripts/sub/x.py"):
            with self.subTest(path=path):
                self.assertIsNone(gates.review_required_problem([path], self.rows("skipped", "skipped")))


class CurrentBranchTests(unittest.TestCase):
    """G6-74-04: in CI the PR's head ref wins over the detached merge commit."""

    def test_ci_head_ref_is_used(self):
        from unittest import mock
        with mock.patch.dict(gates.os.environ, {"HEAD_REF": "issue/74-agent-roster"}, clear=False):
            self.assertEqual(gates.current_branch(), "issue/74-agent-roster")
        env = {k: v for k, v in gates.os.environ.items() if k not in ("HEAD_REF",)}
        env["GITHUB_HEAD_REF"] = "issue/9-x"
        with mock.patch.dict(gates.os.environ, env, clear=True):
            self.assertEqual(gates.current_branch(), "issue/9-x")

    def test_check_runs_the_review_rule_on_a_detached_ci_checkout(self):
        """check() must reach review_required_problem when git reports a detached HEAD."""
        from unittest import mock
        rows = {g: {"owner": "x", "status": "passed", "artifact": "", "note": "ok"} for g, *_ in gates.GATES}
        rows["G3"]["status"] = rows["G6"]["status"] = "skipped"
        with mock.patch.object(gates, "manifest_path", return_value=Path(__file__)),                 mock.patch.object(gates, "read_rows", return_value=rows),                 mock.patch.object(gates, "check_gate", return_value=(True, "ok")),                 mock.patch.object(gates, "git", return_value="HEAD"),                 mock.patch.object(gates, "changed_files", return_value=[".claude/boundaries.json"]),                 mock.patch.dict(gates.os.environ, {"HEAD_REF": "issue/99-x"}),                 contextlib.redirect_stdout(io.StringIO()) as out:
            code = gates.check(99, next_only=False)
        self.assertEqual(code, 1)
        self.assertIn("G3 and G6 must run", out.getvalue())


class DocsLineTests(unittest.TestCase):
    """#76 D4, G4-76-19 to 21: the G7 PR body names the docs it updated."""

    BODY = "# m\n\n## G4 evidence\n\n## G7 PR body draft\n\nSummary.\n\n{docs}\n\nCloses #50\n"

    def problem(self, docs):
        return gates.docs_line_problem(self.BODY.format(docs=docs))

    def test_valid_values(self):
        for docs in ("Docs: docs/ai/README.md", "Docs: `docs/ai/README.md`, docs/runbooks/issue-pipeline.md",
                     "**Docs:** none: tooling only", "Docs: none: no user-facing change"):
            with self.subTest(docs=docs):
                self.assertIsNone(self.problem(docs))

    def test_invalid_values(self):
        for docs, fragment in (("", "no 'Docs:' line"), ("Docs: <files touched, or none: reason>", "placeholder"),
                               ("Docs: none:", "needs a reason"), ("Docs: ../x.md", "not a repository-relative"),
                               ("Docs: C:/x.md", "not a repository-relative"), ("Docs: /etc/x", "not a repository-relative"),
                               ("Docs: docs\\x.md", "not a repository-relative"), ("Docs: some prose here", "not a repository-relative"),
                               ("Docs: a.md\nDocs: b.md", "more than one"), ("  Docs: a.md", "no 'Docs:' line"),
                               ("<!--\nDocs: a.md\n-->", "no 'Docs:' line"), ("<!-- Docs: a.md -->", "no 'Docs:' line"),
                               ("```\nDocs: a.md\n```", "no 'Docs:' line"),
                               # G6-76-04: comments opening mid-line or indented, and tilde fences
                               ("text <!--\nDocs: a.md\n-->", "no 'Docs:' line"),
                               ("  <!--\nDocs: a.md\n-->", "no 'Docs:' line"),
                               ("~~~\nDocs: a.md\n~~~", "no 'Docs:' line"),
                               # G6-76-04 leftover: a fence closes only on its own character and length
                               ("```\n~~~\nDocs: a.md\n```", "no 'Docs:' line"),
                               ("````\n```\nDocs: a.md\n````", "no 'Docs:' line")):
            with self.subTest(docs=docs):
                self.assertIn(fragment, self.problem(docs) or "")

    def test_line_after_the_section_does_not_count(self):
        text = self.BODY.format(docs="") + "\n## Later\n\nDocs: a.md\n"
        self.assertIn("no 'Docs:' line", gates.docs_line_problem(text))

    def test_duplicate_section(self):
        text = self.BODY.format(docs="Docs: a.md") + "\n## G7 PR body draft\n\nDocs: b.md\n"
        self.assertIn("duplicate", gates.docs_line_problem(text))

    def test_syntax_only_no_file_access(self):
        """G4-76-21: listed paths are never opened, stat'ed or resolved."""
        from unittest import mock
        boom = mock.Mock(side_effect=AssertionError("file access"))
        with mock.patch.object(Path, "exists", boom), mock.patch.object(Path, "resolve", boom), \
                mock.patch("builtins.open", boom):
            self.assertIsNone(gates.docs_line_problem(self.BODY.format(docs="Docs: docs/ai/README.md")))

    def test_required_from_76_without_git(self):
        from unittest import mock
        with mock.patch.object(gates, "git_rc", side_effect=AssertionError("no git for n >= 76")):
            self.assertEqual(gates.docs_required(76), (True, None))
            self.assertEqual(gates.docs_required(120), (True, None))

    def test_old_number_not_on_main_is_required_even_on_a_detached_head(self):
        """G4-76-19: condition 2 uses origin/main, never the branch name."""
        from unittest import mock
        with mock.patch.object(gates, "git", return_value="HEAD"), \
                mock.patch.object(gates, "git_rc", side_effect=lambda *a: 0 if a[0] == "rev-parse" else 1):
            self.assertEqual(gates.docs_required(50), (True, None))
        with mock.patch.object(gates, "git_rc", return_value=0):
            self.assertEqual(gates.docs_required(50), (False, None))

    def test_unresolvable_origin_main(self):
        from unittest import mock
        with mock.patch.object(gates, "git_rc", return_value=128), \
                mock.patch.dict(gates.os.environ, {"GITHUB_ACTIONS": "true"}):
            required, failure = gates.docs_required(50)
            self.assertIn("cannot be decided", failure)
        env = {k: v for k, v in gates.os.environ.items() if k != "GITHUB_ACTIONS"}
        with mock.patch.object(gates, "git_rc", return_value=128), mock.patch.dict(gates.os.environ, env, clear=True), \
                contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(gates.docs_required(50), (False, None))
        self.assertIn("WARN  origin/main not available", out.getvalue())

    def test_ci_failure_reaches_check_exit_code(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as tmp:
            manifest = Path(tmp) / "50.md"
            manifest.write_text(self.BODY.format(docs=""), encoding="utf-8")
            rows = {g: {"owner": "x", "status": "passed", "artifact": "", "note": "ok"} for g, *_ in gates.GATES}
            with mock.patch.object(gates, "manifest_path", return_value=manifest), \
                    mock.patch.object(gates, "read_rows", return_value=rows), \
                    mock.patch.object(gates, "git_rc", return_value=128), \
                    mock.patch.object(gates, "current_branch", return_value="issue/50-x"), \
                    mock.patch.object(gates, "changed_files", return_value=[]), \
                    mock.patch.dict(gates.os.environ, {"GITHUB_ACTIONS": "true"}), \
                    contextlib.redirect_stdout(io.StringIO()) as out:
                code = gates.check(50, next_only=False)
        self.assertEqual(code, 1)
        self.assertIn("cannot be decided", out.getvalue())

    def test_g7_through_check_gate(self):
        """Story 5: a passed G7 row passes with a Docs line and fails without one (n >= 76)."""
        from unittest import mock
        row = {"owner": "orchestrator", "status": "passed", "artifact": "", "note": "PR body drafted"}
        with tempfile.TemporaryDirectory() as tmp:
            manifest = Path(tmp) / "80.md"
            with mock.patch.object(gates, "manifest_path", return_value=manifest):
                manifest.write_text(self.BODY.format(docs="Docs: docs/ai/README.md"), encoding="utf-8")
                self.assertEqual(gates.check_gate(80, "G7", row), (True, "PR body drafted"))
                manifest.write_text(self.BODY.format(docs=""), encoding="utf-8")
                ok, detail = gates.check_gate(80, "G7", row)
                self.assertFalse(ok)
                self.assertIn("no 'Docs:' line", detail)

    def test_init_template_fails_until_filled(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(gates, "PIPELINE", Path(tmp)), \
                mock.patch.object(gates, "ROOT", Path(tmp)), mock.patch.object(gates, "git", return_value="issue/99-x"), \
                contextlib.redirect_stdout(io.StringIO()):
            gates.init(99, "t", "ci-tooling")
            text = (Path(tmp) / "99.md").read_text(encoding="utf-8")
        self.assertIn("placeholder", gates.docs_line_problem(text))

    def test_earlier_manifests_unchanged(self):
        """Every manifest before #76 is on main and numbered below 76, so the rule does not apply."""
        for n in (14, 15, 17, 32, 35, 36, 39, 41, 56, 59, 72, 74):
            path = gates.manifest_path(n)
            if not path.exists():
                continue
            with self.subTest(n=n), contextlib.redirect_stdout(io.StringIO()):
                self.assertIsNone(gates.g7_docs_problem(n))
