"""#114: the per-agent Bash allow-list, freeze on deny and the stop-and-report rule (T-02).

Design: docs/architecture/agent-containment.md (D1 to D5); threat delta and red cases:
docs/security/threat-models/agent-containment.md (G4-114-01 to 05, S-114-03, S-114-05).
Commands are only evaluated by the hook, never executed. Freeze tests run main() against a
temporary repository, so nothing touches the real .agent-logs/.
"""
import contextlib
import io
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE.parent / "hooks"))
sys.path.insert(0, str(HERE.parent / "scripts"))
import _hooklib as lib  # noqa: E402
import agent_boundaries  # noqa: E402
import lint  # noqa: E402
import secret_guard  # noqa: E402

E = "." + "env"
LOGGER = "--log" + "ger"  # lint bans the literal (MTP rejects it); the hook must still deny it
MAIN_RANGE = "ma" + "in..HEAD"  # lint bans the literal (use origin/main); the grammar must still allow it
FIXTURE = ["git status", "git diff *", "git fetch *", "dotnet test *", "dotnet build *", "dotnet format *",
           "dotnet restore *", "gitleaks git *", "actionlint *", "pre-commit run *", "aspire publish *", "echo *",
           "gh issue view *"]


def problem(command, patterns=FIXTURE):
    return agent_boundaries.allowlist_problem(command, "fixture", patterns, {"cwd": str(ROOT)}, None)


def real(agent, command, cwd=ROOT):
    """The decision of the real agent file and boundaries for one command (D3)."""
    return agent_boundaries.decide_bash({"tool_input": {"command": command}, "cwd": str(cwd)}, agent,
                                        lib.load_boundaries())


class AllowListGrammarTests(unittest.TestCase):
    DENIED = [
        # G2 tests section
        "python .claude/scripts/gates.py 114", "git diff && python e.py", "git diff $(python e.py)",
        "git diff `x`", "git diff > f", "git diff | tee f", "git diff <(cat x)", "(git diff)", "git diff &",
        "FOO=1 git diff", "cd .. && git diff", "git diff --output=f", "git diff --no-index a b",
        "git diff /dev/null /c/Users/x/.ssh/id", "git diff -- ../x", "git fetch --upload-pack=x .",
        "git difftool", "git diff\npython e.py", "echo $HOME",
        # G4-114-01: shell expansion and redirect remnants
        "git diff --o{u,u}tput=f", "git diff {/c/x,README.md}", "git diff . .*", "git diff >/dev/nullx",
        "git status 2>&1x", "git diff ?", "git diff [ab]",
        # G6-114-01, 03, 04, 05: cd into a writable folder, unlisted dotnet options, a quoted program
        "cd docs && git diff", "cd - && git diff", "cd src/Decisya.Web/.. && git diff",
        "dotnet build -getProperty:X -getResultOutputFile:.claude/settings.json",
        "dotnet build --getResultOutputFile .claude/x", "dotnet build --artifacts-path .claude",
        "dotnet restore --packages .claude/x", "dotnet build -tl", "'git status'",
        # S-114-05 and the scanner: a lone CR, escapes, unbalanced quotes, comments, background
        "git diff\rpython e.py", "git diff \\\n--output=x", "git diff 'x", "git diff # x", "git status |& tee x",
        "git status & git diff",
        # G4-114-02: paths inside a token, MSBuild switches, response files
        f'dotnet test {LOGGER} "trx;LogFileName=C:\\Users\\marco\\x"', "dotnet test -flp:logfile=C:/x",
        "gitleaks git --log-opts=--output=/c/x", "dotnet build /bl:C:/x", "dotnet build /p:OutDir=C:/x",
        "dotnet test --list-tests @docs/x.rsp", "git diff ~/x", "git diff a=~/x", "git diff c:x",
        "git diff '\\\\server\\s'",
        # G4-114-03: options that write a chosen file, load code or change package sources
        f'dotnet test --results-directory .claude {LOGGER} "trx;LogFileName=settings.json"',
        "dotnet test --list-tests -p:PostBuildEvent=x", "dotnet test --diag .claude/settings.json",
        "dotnet build -o .claude/hooks", "dotnet format --report .claude", "dotnet build -property:X=1",
        "dotnet build -t:Foo", "dotnet build -bl", "dotnet test -- RunConfiguration.ResultsDirectory=.claude",
        "dotnet restore --source x", "dotnet restore --configfile x", "dotnet test --settings x.runsettings",
        "actionlint -shellcheck=.github/x.cmd", "actionlint --pyflakes=x", "pre-commit run -c deploy/x.yaml",
        "pre-commit run --config=x", "aspire publish -o .claude", "aspire publish --output-path x",
        "gitleaks git -r x", "gitleaks git --report-path x", "gitleaks git --diagnostics-dir .claude",
        "git diff --no-i a b", "git diff --outp=x", "git fetch origin +main:refs/heads/x",
    ]
    ALLOWED = [
        "git diff", "git diff --stat", "git diff origin/main...HEAD -- docs/", "git status 2>&1",
        "git diff 2>/dev/null", "git status && git diff --stat", "git diff HEAD~1",
        f"git diff {MAIN_RANGE}", "git diff -- ':/docs'", "git status >/dev/null 2>&1", "git diff --stat | git status",
        'dotnet test --project tests/Decisya.Bff.Tests --filter-trait "Category=Integration"',
        "dotnet build -warnaserror", "dotnet build -c Release -v q", "dotnet test --no-build --list-tests",
        "gh issue view 1 --json title --jq '.t | ascii'", "gh issue view 1 --json title --jq '{t: .title}'",
        "git fetch origin", "pre-commit run gitleaks --all-files", "echo 'plain text'",
    ]

    def test_red_cases_denied(self):
        for command in self.DENIED:
            with self.subTest(command=command):
                self.assertIsNotNone(problem(command))

    def test_green_cases_allowed(self):
        for command in self.ALLOWED:
            with self.subTest(command=command):
                self.assertIsNone(problem(command))

    def test_legacy_colon_star_and_exact_patterns(self):
        patterns = [lib.pattern_body("git status"), lib.pattern_body("git diff:*")]
        self.assertEqual(patterns, ["git status", "git diff *"])
        self.assertIsNone(problem("git diff --stat", patterns))
        self.assertIsNotNone(problem("git status --short", patterns))  # exact means exact
        self.assertIsNotNone(problem("git difftool", patterns))

    def test_pattern_grammar(self):
        for bad in ("git diff*", "git * diff", "*", "git diff $(x)", "git diff;x", ""):
            with self.subTest(bad=bad), self.assertRaises(lib.ConfigError):
                lib.pattern_body(bad)

    def test_no_patterns_means_no_bash(self):
        self.assertIsNotNone(problem("git status", []))

    def test_reason_never_echoes_the_command(self):
        rule, cls = problem("python3 -c 'secret-canary-7f3a'")
        self.assertEqual(cls, "python3")
        self.assertNotIn("canary", rule + cls)


class RealAgentTests(unittest.TestCase):
    """D3 and G4-114-04 against the real tools: lines."""

    DENIED = [
        ("devops", "aspire exec x"), ("devops", "aspire run"), ("devops", "pre-commit install"),
        ("devops", "dotnet run --project src/Decisya.AppHost"), ("devops", "python -c 'print(1)'"),
        ("devops", "python .claude/scripts/lint.py --x"), ("devops", "gh api repos/x"),
        ("devops", "gitleaks dir ."), ("backend-dev", "dotnet ef database drop"),
        ("backend-dev", "dotnet ef migrations remove --force"), ("backend-dev", "dotnet ef migrations add X --force"),
        ("backend-dev", "dotnet ef dbcontext scaffold x"), ("backend-dev", "dotnet run"),
        ("backend-dev", "dotnet ef migrations add X --project src/Modules/A --output-dir ../../../.claude"),
        ("backend-dev", "dotnet ef migrations add X --project .claude --output-dir hooks"),
        ("backend-dev", "dotnet ef migrations script -o .claude/x.sql"),
        ("security-reviewer", "git fetch origin +main:refs/heads/x"), ("security-reviewer", "git fetch upstream"),
        ("security-reviewer", "cat x"), ("security-reviewer", "gitleaks git"),
        ("tech-writer", "dotnet test --list-tests -p:PostBuildEvent=x"), ("tech-writer", "gh issue comment 1"),
        ("architect", "git status"), ("product-owner", "ls"), ("test-engineer", "mkdir x"),
        ("frontend-dev", "cd src/Decisya.Web && npx vite"), ("frontend-dev", "npm run build && cd .."),
        ("tech-writer", "dotnet test --list-tests --project docs/x.csproj"), ("tech-writer", "cd docs && dotnet test --list-tests"),
        ("security-reviewer", "cd docs/security && dotnet list package --vulnerable"),
        ("security-reviewer", "dotnet list package --vulnerable docs/x.csproj"),
        ("devops", "cd deploy && python .claude/scripts/lint.py"), ("devops", "'python .claude/scripts/lint.py'"),
        ("test-engineer", "dotnet test --report-xunit-trx"),
    ]
    ALLOWED = [
        ("devops", "python .claude/scripts/lint.py"), ("devops", "pre-commit run gitleaks --all-files"),
        ("devops", "aspire --version"), ("devops", "gh run view 1 --log-failed"), ("devops", "actionlint"),
        ("backend-dev", "dotnet build -warnaserror"),
        ("backend-dev", "dotnet ef migrations add AddX --project src/Modules/Tenancy/Decisya.Modules.Tenancy "
                        "--startup-project src/Decisya.Infrastructure.Migrator --context TenancyDbContext "
                        "--output-dir Infrastructure/Migrations"),
        ("backend-dev", "dotnet ef migrations list --project src/Modules/Tenancy/Decisya.Modules.Tenancy"),
        ("security-reviewer", "git fetch origin"), ("security-reviewer", "git diff origin/main...HEAD"),
        ("security-reviewer", "dotnet list package --vulnerable --include-transitive"),
        ("tech-writer", "gh issue view 1 --json title --jq '.t | ascii'"),
        ("frontend-dev", "cd src/Decisya.Web && npm run build"), ("test-engineer", "git diff --stat"),
        ("tech-writer", "dotnet test --list-tests"), ("security-reviewer", "dotnet list package --vulnerable --include-transitive"),
        ("test-engineer", 'dotnet test --project tests/Decisya.Bff.Tests --filter-trait "Category=Integration" --ignore-exit-code 8'),
        ("platform-dev", "docker ps --filter name=decisya-"),
    ]

    def test_spa_folder_is_the_only_other_working_directory(self):
        web = ROOT / "src" / "Decisya.Web"
        self.assertIsNone(real("frontend-dev", "npm run build", cwd=web))
        self.assertIsNotNone(real("tech-writer", "dotnet test --list-tests", cwd=ROOT / "docs"))

    def test_denied(self):
        for agent, command in self.DENIED:
            with self.subTest(agent=agent, command=command):
                self.assertIsNotNone(real(agent, command))

    def test_allowed(self):
        for agent, command in self.ALLOWED:
            with self.subTest(agent=agent, command=command):
                self.assertIsNone(real(agent, command))

    def test_every_agent_file_parses_and_has_no_bare_or_no_space_wildcard(self):
        for name in lib.load_boundaries()["agents"]:
            with self.subTest(agent=name):
                for pattern in lib.bash_patterns(name):
                    self.assertFalse(pattern.endswith("*") and not pattern.endswith(" *"))

    def test_devops_stop_and_report_rule(self):
        # Done when 3: removing the rule from devops.md fails here.
        text = (ROOT / ".claude" / "agents" / "devops.md").read_text(encoding="utf-8")
        rule = next((line for line in text.splitlines() if "(stop-and-report)" in line), "")
        for fragment in ("(stop-and-report)", "stop at once", "Report to your caller", "Do not retry"):
            self.assertIn(fragment, rule)

    def test_matcher_covers_every_side_effect_tool(self):
        hooks = json.loads((ROOT / ".claude" / "settings.json").read_text(encoding="utf-8"))["hooks"]["PreToolUse"]
        matcher = next(h["matcher"] for h in hooks if "agent_boundaries" in h["hooks"][0]["command"])
        self.assertEqual(set(matcher.split("|")), {"Write", "Edit", "MultiEdit", "NotebookEdit", "Bash"})
        self.assertNotIn("SubagentHandback", matcher)


class FreezeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / ".claude" / "agents").mkdir(parents=True)
        (self.root / ".claude" / "boundaries.json").write_text(json.dumps(
            {"deny": [".claude/**", ".agent-logs/**"], "agents": {"backend-dev": ["src/**"]}}), encoding="utf-8")
        (self.root / ".claude" / "agents" / "backend-dev.md").write_text(
            "---\nname: backend-dev\ntools: Read, Write, Bash(git status *), Bash(cat *)\n---\n", encoding="utf-8")
        self.saved = (lib.ROOT, lib.LOG_DIR, lib.LOG_FILE)
        lib.ROOT, lib.LOG_DIR = self.root, self.root / ".agent-logs"
        lib.LOG_FILE = lib.LOG_DIR / "hooks.jsonl"

    def tearDown(self):
        lib.ROOT, lib.LOG_DIR, lib.LOG_FILE = self.saved
        self.tmp.cleanup()

    def call(self, module, payload):
        out, saved = io.StringIO(), sys.stdin
        sys.stdin = io.StringIO(json.dumps(payload))
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(module.main(), 0)
        finally:
            sys.stdin = saved
        text = out.getvalue().strip()
        return json.loads(text)["hookSpecificOutput"] if text else None

    def bash(self, command, run="run1", agent="backend-dev"):
        payload = {"agent_type": agent, "tool_name": "Bash", "tool_input": {"command": command}, "cwd": str(self.root)}
        if run is not None:
            payload["agent_id"] = run
        return payload

    def write(self, rel, run="run1"):
        return {"agent_type": "backend-dev", "tool_name": "Write", "cwd": str(self.root), "agent_id": run,
                "tool_input": {"file_path": str(self.root / rel)}}

    def test_one_deny_freezes_the_run_and_only_that_run(self):
        self.assertIsNone(self.call(agent_boundaries, self.bash("git status")))
        first = self.call(agent_boundaries, self.bash("python -c 'canary-91x'"))
        self.assertEqual(first["permissionDecision"], "deny")
        self.assertIn(lib.FREEZE_NOTICE, first["permissionDecisionReason"])
        self.assertNotIn("canary-91x", first["permissionDecisionReason"])
        for payload in (self.bash("git status"), self.write("src/x.cs")):
            with self.subTest(tool=payload["tool_name"]):
                d = self.call(agent_boundaries, payload)
                self.assertEqual(d["permissionDecision"], "deny")
                self.assertIn("frozen", d["permissionDecisionReason"])
        self.assertIsNone(self.call(agent_boundaries, self.bash("git status", run="run2")))
        self.assertIsNone(self.call(agent_boundaries, self.bash("python x", run=None, agent="")))
        self.assertIsNone(self.call(agent_boundaries, self.bash("python x", agent="general-purpose")))
        marker = (lib.LOG_DIR / "frozen" / "run1").read_text(encoding="utf-8")
        self.assertNotIn("canary", marker)
        record = json.loads(lib.LOG_FILE.read_text(encoding="utf-8").splitlines()[-1])
        self.assertEqual((record["rule"], record["agent_id"]), ("freeze.frozen", "run1"))

    def test_write_lane_deny_freezes_bash(self):
        self.assertEqual(self.call(agent_boundaries, self.write("docs/x.md"))["permissionDecision"], "deny")
        self.assertIn("frozen", self.call(agent_boundaries, self.bash("git status"))["permissionDecisionReason"])

    def test_secret_guard_deny_freezes_the_run(self):
        self.assertEqual(self.call(secret_guard, self.bash(f"cat {E}"))["permissionDecision"], "deny")
        self.assertIn("frozen", self.call(agent_boundaries, self.bash("git status"))["permissionDecisionReason"])
        main = self.call(secret_guard, self.bash(f"cat {E}", run=None, agent=""))
        self.assertNotIn(lib.FREEZE_NOTICE, main["permissionDecisionReason"])

    def test_missing_or_invalid_agent_id_is_denied(self):
        for run in (None, "", "../x", "a/b", "x" * 129):
            with self.subTest(run=run):
                d = self.call(agent_boundaries, self.bash("git status", run=run))
                self.assertEqual(d["permissionDecision"], "deny")
        self.assertFalse((lib.LOG_DIR / "frozen").exists() and any((lib.LOG_DIR / "frozen").iterdir()))

    def test_unreadable_freeze_state_denies(self):
        lib.LOG_DIR.mkdir()
        (lib.LOG_DIR / "frozen").write_text("a file where the folder should be", encoding="utf-8")
        d = self.call(agent_boundaries, self.bash("git status"))
        self.assertEqual(d["permissionDecision"], "deny")
        self.assertEqual(lib.freeze_state(self.bash("git status")), "freeze.unreadable")

    def test_concurrent_marker_creation_is_success(self):
        payload = self.bash("x")
        self.assertIsNone(lib.freeze(payload, "a"))
        self.assertIsNone(lib.freeze(payload, "b"))  # S-114-03: both hooks may freeze the same run

    def test_agent_file_without_tools_line_fails_closed(self):
        (self.root / ".claude" / "agents" / "backend-dev.md").write_text("---\nname: backend-dev\n---\n", encoding="utf-8")
        d = self.call(agent_boundaries, self.bash("git status"))
        self.assertEqual(d["permissionDecision"], "deny")
        self.assertIn("fail closed", d["permissionDecisionReason"])


class LintContainmentTests(unittest.TestCase):
    """#114 lint checks 10 to 13 and G4-114-05, on a temporary tree."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / ".claude" / "agents").mkdir(parents=True)
        (self.root / ".claude" / "skills").mkdir()
        (self.root / "CLAUDE.md").write_text("# test\n", encoding="utf-8")
        (self.root / ".claude" / "boundaries.json").write_text(json.dumps(
            {"deny": [".claude/**"], "agents": {"alpha": ["src/**"]}}), encoding="utf-8")
        self.saved = (lint.ROOT, lint.CLAUDE, lint.problems)
        lint.ROOT, lint.CLAUDE, lint.problems = self.root, self.root / ".claude", []

    def tearDown(self):
        lint.ROOT, lint.CLAUDE, lint.problems = self.saved
        self.tmp.cleanup()

    def lint_agent(self, frontmatter):
        (self.root / ".claude" / "agents" / "alpha.md").write_text(f"---\n{frontmatter}\n---\nBody.\n", encoding="utf-8")
        lint.problems = []
        with contextlib.redirect_stdout(io.StringIO()) as out:
            code = lint.main()
        return code, out.getvalue()

    def test_good_agent_passes(self):
        code, out = self.lint_agent("name: alpha\ndescription: d\ntools: Read, Bash(git diff *), Bash(git status)")
        self.assertEqual(code, 0, out)

    def test_red_frontmatter(self):
        cases = {
            "no-space wildcard": "name: alpha\ndescription: d\ntools: Read, Bash(git diff*)",
            "bare Bash": "name: alpha\ndescription: d\ntools: Read, Bash",
            "unhooked tool": "name: alpha\ndescription: d\ntools: Read, WebFetch",
            "extra key": "name: alpha\ndescription: d\ntools: Read\nmcpServers: x",
            "permissionMode": "name: alpha\ndescription: d\ntools: Read\npermissionMode: bypassPermissions",
            "comma in Bash": "name: alpha\ndescription: d\ntools: Read, Bash(git diff a,b)",
            "aspire wildcard": "name: alpha\ndescription: d\ntools: Read, Bash(aspire *)",
            "pre-commit wildcard": "name: alpha\ndescription: d\ntools: Read, Bash(pre-commit *)",
            "no tools line": "name: alpha\ndescription: d",
            "name differs": "name: beta\ndescription: d\ntools: Read",
        }
        for label, frontmatter in cases.items():
            with self.subTest(case=label):
                code, out = self.lint_agent(frontmatter)
                self.assertEqual(code, 1, out)


class GitignoreAndDenyTests(unittest.TestCase):
    def test_freeze_markers_are_outside_every_lane(self):
        self.assertIn(".agent-logs/**", lib.load_boundaries()["deny"])
        self.assertIn(".agent-logs/", (ROOT / ".gitignore").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
