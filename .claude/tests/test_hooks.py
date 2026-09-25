"""Hook behaviour: write lanes, agent-only command policy, fail closed, audit log (#39 items 1, 4, 5, 7).

Each test runs a hook's main() against a temporary repository, so nothing touches the real
.claude/ or .agent-logs/. Forbidden names are built at run time (G4-39-57).
"""
import contextlib
import io
import json
import sys
import tempfile
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "hooks"))
import _hooklib as lib  # noqa: E402
import agent_boundaries  # noqa: E402
import secret_guard  # noqa: E402

E = "." + "env"


class HookTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / ".claude" / "agents").mkdir(parents=True)
        self.write_boundaries({"deny": [".claude/**"], "agents": {"security-reviewer": ["docs/security/**"],
                                                                 "backend-dev": ["src/**"]}})
        for name in ("security-reviewer", "backend-dev"):
            (self.root / ".claude" / "agents" / f"{name}.md").write_text(f"---\nname: {name}\n---\n", encoding="utf-8")
        self.saved = (lib.ROOT, lib.LOG_DIR, lib.LOG_FILE)
        lib.ROOT, lib.LOG_DIR = self.root, self.root / ".agent-logs"
        lib.LOG_FILE = lib.LOG_DIR / "hooks.jsonl"

    def tearDown(self):
        lib.ROOT, lib.LOG_DIR, lib.LOG_FILE = self.saved
        self.tmp.cleanup()

    def write_boundaries(self, config):
        (self.root / ".claude" / "boundaries.json").write_text(json.dumps(config) if isinstance(config, dict) else config,
                                                                 encoding="utf-8")

    def run_hook(self, module, payload):
        stdin = payload if isinstance(payload, str) else json.dumps(payload)
        out, err = io.StringIO(), io.StringIO()
        saved_stdin = sys.stdin
        sys.stdin = io.StringIO(stdin)
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = module.main()
        finally:
            sys.stdin = saved_stdin
        self.assertEqual(code, 0)
        text = out.getvalue().strip()
        return json.loads(text)["hookSpecificOutput"] if text else None

    def write(self, agent, rel):
        return {"agent_type": agent, "tool_name": "Write", "cwd": str(self.root),
                "tool_input": {"file_path": str(self.root / rel)}, "session_id": "s1", "tool_use_id": "t1"}

    def bash(self, agent, command):
        return {"agent_type": agent, "tool_name": "Bash", "tool_input": {"command": command},
                "session_id": "s1", "tool_use_id": "t2"}

    def log(self):
        return [json.loads(l) for l in lib.LOG_FILE.read_text(encoding="utf-8").splitlines()] if lib.LOG_FILE.exists() else []

    # item 1: lanes and fail closed
    def test_main_session_is_unrestricted(self):
        self.assertIsNone(self.run_hook(agent_boundaries, self.write("", "anything/x.md")))

    def test_agent_in_lane_allowed_outside_denied_and_logged(self):
        self.assertIsNone(self.run_hook(agent_boundaries, self.write("security-reviewer", "docs/security/r.md")))
        d = self.run_hook(agent_boundaries, self.write("security-reviewer", "src/x.cs"))
        self.assertEqual(d["permissionDecision"], "deny")
        rec = self.log()[-1]
        self.assertEqual((rec["rule"], rec["path"], rec["agent"]), ("boundary.not-in-lane", "src/x.cs", "security-reviewer"))

    def test_unlisted_agent_unchanged(self):
        self.assertIsNone(self.run_hook(agent_boundaries, self.write("Explore", "src/x.cs")))

    def test_invalid_boundaries_fails_closed_for_agents_only(self):
        self.write_boundaries("{ not json")
        d = self.run_hook(agent_boundaries, self.write("backend-dev", "src/x.cs"))
        self.assertEqual(d["permissionDecision"], "deny")
        self.assertIn("fail closed for backend-dev", d["permissionDecisionReason"])
        self.assertIsNone(self.run_hook(agent_boundaries, self.write("", "src/x.cs")))

    def test_no_config_at_all_treats_every_agent_as_listed(self):
        self.write_boundaries("{ not json")
        for p in (self.root / ".claude" / "agents").glob("*.md"):
            p.unlink()
        d = self.run_hook(agent_boundaries, self.write("some-plugin-agent", "src/x.cs"))
        self.assertEqual(d["permissionDecision"], "deny")

    def test_error_reason_names_only_the_exception_class(self):
        payload = self.write("backend-dev", "src/x.cs")
        payload["tool_input"] = {"file_path": 12345}
        d = self.run_hook(agent_boundaries, payload)
        self.assertEqual(d["permissionDecision"], "deny")
        self.assertRegex(d["permissionDecisionReason"], r"^agent_boundaries error \(fail closed for backend-dev\): \w+\. ")
        self.assertNotIn("12345", d["permissionDecisionReason"])

    def test_unreadable_stdin_fails_open(self):
        self.assertIsNone(self.run_hook(agent_boundaries, "not json"))
        self.assertIsNone(self.run_hook(secret_guard, "not json"))

    def test_too_long_input_denied_for_agents_scanned_for_main(self):
        big = "echo " + "x" * (lib.MAX_COMMAND + 10)
        d = self.run_hook(secret_guard, self.bash("backend-dev", big))
        self.assertEqual(d["permissionDecision"], "deny")
        self.assertEqual(self.log()[-1]["rule"], "input.too-long")
        start = time.perf_counter()
        self.assertIsNone(self.run_hook(secret_guard, self.bash("", "echo " + "x" * (1024 * 1024))))
        self.assertLess(time.perf_counter() - start, 1.0)

    # items 4 and 5: agent-only command policy (Marco's decision: the main session keeps prompting)
    def test_agent_command_policy(self):
        for cmd in ("dotnet add src/X package Foo", "dotnet package add Foo", "dotnet new install x",
                    "dotnet tool install -g x", "dotnet workload install aspire", "dotnet nuget add source u",
                    "gh issue delete 5", "gh issue comment 5 --body x", "gh run download 1", "gh run rerun 1",
                    "gh repo delete x", "gh secret list", "gh api repos/x", "gh pr merge 3"):
            with self.subTest(cmd=cmd):
                d = self.run_hook(agent_boundaries, self.bash("backend-dev", cmd))
                self.assertEqual(d and d["permissionDecision"], "deny")
                self.assertIsNone(self.run_hook(agent_boundaries, self.bash("", cmd)), "main session must not be denied")
        for cmd in ("dotnet build -warnaserror", "dotnet test --no-build", "gh issue view 39", "gh run list"):
            with self.subTest(cmd=cmd):
                self.assertIsNone(self.run_hook(agent_boundaries, self.bash("backend-dev", cmd)))

    def test_agent_command_policy_bypass_forms(self):
        """G6-39-09: global flags, .exe, quoted verbs and the extra installers."""
        for cmd in ("gh -R o/r issue close 5", "gh --repo=o/r run cancel 1", "dotnet.exe package add Foo",
                    'dotnet "package" add Foo', "DOTNET tool install -g x", "aspire add redis",
                    "gh extension install o/x", "pre-commit try-repo https://x"):
            with self.subTest(cmd=cmd):
                d = self.run_hook(agent_boundaries, self.bash("backend-dev", cmd))
                self.assertEqual(d and d["permissionDecision"], "deny")

    def test_missing_hooklib_denies_agents_only(self):
        """G6-39-10: a hook that cannot load its library denies subagents, never the main session."""
        import shutil
        import subprocess
        hooks = self.root / "hooks-copy"
        hooks.mkdir()
        for name in ("agent_boundaries.py", "secret_guard.py"):
            shutil.copy(HERE.parent / "hooks" / name, hooks / name)
            for agent, expect in (("backend-dev", "deny"), ("", None)):
                with self.subTest(hook=name, agent=agent or "main"):
                    out = subprocess.run([sys.executable, str(hooks / name)], input=json.dumps(self.bash(agent, "ls")),
                                         capture_output=True, text=True).stdout.strip()
                    got = json.loads(out)["hookSpecificOutput"]["permissionDecision"] if out else None
                    self.assertEqual(got, expect)

    # item 7: audit log
    def test_log_never_contains_command_text(self):
        canary = "canary-" + "7f3a9c"
        d = self.run_hook(secret_guard, self.bash("", f"cat {E} # {canary}"))
        self.assertEqual(d["permissionDecision"], "deny")
        text = lib.LOG_FILE.read_text(encoding="utf-8")
        self.assertNotIn(canary, text)
        self.assertNotIn(E, text)
        rec = json.loads(text.splitlines()[-1])
        self.assertEqual(set(rec), {"ts", "hook", "agent", "tool", "decision", "rule", "session_id", "tool_use_id"})
        self.assertEqual((rec["agent"], rec["rule"]), ("main", "secret.dotenv"))

    def test_unwritable_log_never_changes_the_decision(self):
        lib.LOG_DIR.parent.mkdir(exist_ok=True)
        lib.LOG_DIR.write_text("a file where the log directory should be", encoding="utf-8")
        d = self.run_hook(secret_guard, self.bash("", f"cat {E}"))
        self.assertEqual(d["permissionDecision"], "deny")
        d = self.run_hook(agent_boundaries, self.write("security-reviewer", "src/x.cs"))
        self.assertEqual(d["permissionDecision"], "deny")

    def test_log_rotates(self):
        lib.LOG_DIR.mkdir()
        lib.LOG_FILE.write_text("x" * (lib.LOG_ROTATE_BYTES + 1), encoding="utf-8")
        self.run_hook(secret_guard, self.bash("", f"cat {E}"))
        self.assertTrue((lib.LOG_DIR / "hooks.jsonl.1").exists())
        self.assertEqual(len(self.log()), 1)


if __name__ == "__main__":
    unittest.main()
