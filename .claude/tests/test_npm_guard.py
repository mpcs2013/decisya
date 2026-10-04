"""#58: npm is an allow-list for project agents; npx and the other package managers are denied
(threat model docs/security/threat-models/npm-install-guard.md, M1 to M4)."""
from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "hooks"))
import _hooklib as lib  # noqa: E402
import agent_boundaries  # noqa: E402

ALLOWED = [
    "npm run build",
    "npm run-script lint",
    "npm test",
    "npm ls",
    "npm outdated",
    "npm audit",
    "npm audit --json",
    "npm run lint -- --fix",
    "cd src/Decisya.Web && npm run typecheck",
    "dotnet build -warnaserror",
]

DENIED = [
    "npm install",
    "npm i",
    "npm ci",
    "npm add left-pad",
    "npm insta",
    "npm install-cl",
    "npm installTest",
    "npm isnt",
    "npm ic",
    "npm x vite",
    "npm exec vite",
    "npm create vite",
    "npm init vite",
    "npm update",
    "npm udpate",
    "npm rebuild",
    "npm rb",
    "npm audit fix",
    "npm audit fix --force",
    "npm --prefix src/Decisya.Web run build",
    "npm ci --prefix .devcontainer/claude",
    "npm",
    "npm.cmd install",
    "npm.ps1 ci",
    "C:\\Program Files\\nodejs\\npm.cmd ci",
    "/usr/local/bin/npm ci",
    '"npm" "install"',
    "n\\pm install",
    "echo ok && npm i",
    "echo $(npm i)",
    "true; npm ci",
    "node C:/nodejs/node_modules/npm/bin/npm-cli.js install",
    "npx vite",
    "npx tsc",
    "npx --no tsc",
    "npx playwright test",
    "npx.cmd eslint .",
    "node node_modules/npm/bin/npx-cli.js tsc",
    "pnpm i",
    "pnpx vite",
    "yarn",
    "yarnpkg add x",
    "bun install",
    "bunx vite",
    "corepack enable",
    "deno install",
    # G6-58-01: npm reads a true/false after -v/--version as its value, and the next word as the command.
    "npm --version false install x",
    "npm -v false ci",
    "npm -v true i x",
    "npm -v",
    "npm --version",
]


class NpmGuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root / ".claude" / "agents").mkdir(parents=True)
        config = {"deny": [".claude/**"], "agents": {"frontend-dev": ["src/Decisya.Web/**"],
                                                    "backend-dev": ["src/**"]}}
        (self.root / ".claude" / "boundaries.json").write_text(json.dumps(config), encoding="utf-8")
        # #114: the allow-list runs first. Both fixtures may run any npm-like or dotnet build command,
        # so the npm guard (the second layer) is what decides the cases below.
        (self.root / "src" / "Decisya.Web").mkdir(parents=True)
        tools = ("Read, Bash(npm *), Bash(npm.cmd *), Bash(npm.ps1 *), Bash(npx *), Bash(npx.cmd *), Bash(pnpm *), "
                 "Bash(pnpx *), Bash(yarn), Bash(yarnpkg *), Bash(bun *), Bash(bunx *), Bash(corepack *), "
                 "Bash(deno *), Bash(node *), Bash(dotnet build *)")
        for name in ("frontend-dev", "backend-dev"):
            (self.root / ".claude" / "agents" / f"{name}.md").write_text(
                f"---\nname: {name}\ntools: {tools}\n---\n", encoding="utf-8")
        self.runs = 0
        self.saved = (lib.ROOT, lib.LOG_DIR, lib.LOG_FILE)
        lib.ROOT, lib.LOG_DIR = self.root, self.root / ".agent-logs"
        lib.LOG_FILE = lib.LOG_DIR / "hooks.jsonl"

    def tearDown(self):
        lib.ROOT, lib.LOG_DIR, lib.LOG_FILE = self.saved
        self.tmp.cleanup()

    def run_hook(self, agent, command):
        self.runs += 1
        payload = {"agent_type": agent, "tool_name": "Bash", "tool_input": {"command": command},
                   "session_id": "s1", "tool_use_id": "t1", "agent_id": f"run{self.runs}", "cwd": str(self.root)}
        out = io.StringIO()
        saved = sys.stdin
        sys.stdin = io.StringIO(json.dumps(payload))
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(agent_boundaries.main(), 0)
        finally:
            sys.stdin = saved
        text = out.getvalue().strip()
        return json.loads(text)["hookSpecificOutput"] if text else None

    def test_allowed_npm_verbs_pass_for_every_listed_agent(self):
        # M4: frontmatter tools do not bind subagents (cc:T-02), so backend-dev is checked too.
        for agent in ("frontend-dev", "backend-dev"):
            for command in ALLOWED:
                with self.subTest(agent=agent, command=command):
                    self.assertIsNone(self.run_hook(agent, command))

    def test_installs_npx_and_other_package_managers_are_denied(self):
        for agent in ("frontend-dev", "backend-dev"):
            for command in DENIED:
                with self.subTest(agent=agent, command=command):
                    result = self.run_hook(agent, command)
                    self.assertIsNotNone(result, "not denied")
                    self.assertEqual(result["permissionDecision"], "deny")
                    reason = result["permissionDecisionReason"]
                    self.assertTrue("Marco" in reason or "declared Bash patterns" in reason, reason)
                    if not command.startswith(("echo", "true", "C:", "/", '"', "n\\")):
                        self.assertIsNotNone(agent_boundaries.npm_violation(command), "npm guard must deny on its own")

    def test_denials_are_audited(self):
        self.run_hook("frontend-dev", "npm ci")
        entries = [json.loads(line) for line in lib.LOG_FILE.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(entries[-1]["decision"], "deny")
        self.assertEqual(entries[-1]["rule"], "agent.npm")

    def test_main_session_and_unlisted_agents_are_unaffected(self):
        self.assertIsNone(self.run_hook("", "npm install"))
        self.assertIsNone(self.run_hook("general-purpose", "npx vite"))

    def test_case_variants_fail_the_allow_list(self):
        result = self.run_hook("frontend-dev", "NPM RUN build")  # #114: patterns are case-sensitive
        self.assertIn("declared Bash patterns", result["permissionDecisionReason"])

    def test_the_deny_reason_points_to_the_grep_tool(self):
        # S4: a text search naming npm reads as an npm call; the reason says what to use instead.
        result = self.run_hook("frontend-dev", "grep -rn npm src")
        self.assertIsNotNone(result)
        self.assertIn("Grep tool", result["permissionDecisionReason"])

    def test_the_frontmatter_grants_no_install_or_npx(self):
        # M5: frontend-dev and test-engineer list no npm ci and no npx entry.
        agents = Path(__file__).resolve().parents[1] / "agents"
        for name in ("frontend-dev", "test-engineer"):
            tools = next(line for line in (agents / f"{name}.md").read_text(encoding="utf-8").splitlines()
                         if line.startswith("tools:"))
            with self.subTest(agent=name):
                self.assertNotIn("npm ci", tools)
                self.assertNotIn("npx", tools)


if __name__ == "__main__":
    unittest.main()
