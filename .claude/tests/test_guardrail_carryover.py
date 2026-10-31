"""Decisya's guardrails, pinned black-box so they survive the Norma lock (#246, G3 M1).

The Norma core replaces .claude/hooks and .claude/scripts with files of the same names. The older
hook tests import those modules' internals, so they cannot tell whether the installed core still
denies what Decisya denies. These tests do not import any hook: each case runs every installed
PreToolUse hook as its own process on a temporary copy of .claude/, sends one Claude Code payload
on stdin and reads only the decision (a JSON `permissionDecision` of deny, or exit code 2).

A case that fails after a Norma install or bump means the core no longer denies it. Then G4 stops:
the deny moves to the project layer (a settings.json deny entry or a narrower agent tools line) and a
Norma core option is drafted for the exchange. Nobody patches a locked file (ADR-0023).
Forbidden names are built at run time, as in the other hook tests (G4-39-57).
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXPECTED_HOOKS = {"agent_boundaries.py", "secret_guard.py"}
HOOK_ERROR = re.compile(r"\berror \(fail closed\b")  # the hooks' own fail-closed reason (_hooklib.fail_closed_reason)
E = "." + "env"
SECRETS = "secrets" + ".json"
SKIP = shutil.ignore_patterns("tests", "__pycache__", "*.local.json", "*.local.*", ".agent-logs")

# (agent, path relative to the root): a write that must be denied.
WRITE_DENY = [
    # #218: build output, IDE user files and node_modules, without case, at any depth
    ("backend-dev", "src/Decisya.Api/Decisya.Api.csproj.user"),
    ("backend-dev", "src/Decisya.Api/DECISYA.API.CSPROJ.USER"),
    ("backend-dev", "src/Decisya.Api/obj/x.cs"),
    ("backend-dev", "src/Decisya.Api/OBJ/x.cs"),
    ("frontend-dev", "src/Decisya.Web/node_modules/x/index.js"),
    ("frontend-dev", "src/Decisya.Web/Node_Modules/x/index.js"),
    # #218: Windows names that alias another file
    ("backend-dev", "src/Decisya.Api/x.cs::$DATA"),
    ("backend-dev", "src/Decisya.Api/x.cs."),
    ("backend-dev", "src/Decisya.Api/x.cs "),
    # the guardrails themselves and the gate state
    ("backend-dev", ".claude/hooks/x.py"),
    ("devops", ".claude/settings.json"),
    ("devops", ".claude/boundaries.json"),
    ("backend-dev", "CLAUDE.md"),
    ("security-reviewer", "docs/ai/pipeline/1.md"),
    # outside the agent's lane
    ("architect", "src/Decisya.Api/x.cs"),
    ("frontend-dev", ".github/workflows/ci.yml"),
]
# Controls: the harness does not deny everything.
WRITE_ALLOW = [
    ("backend-dev", "src/Decisya.Api/X.cs"),
    ("architect", "docs/architecture/x.md"),
    ("frontend-dev", "src/Decisya.Web/src/x.ts"),
]

# (agent, command): a Bash call that must be denied. Commands are only evaluated, never run.
BASH_DENY = [
    # the cd target is src/Decisya.Web only
    ("frontend-dev", "cd docs && npm run lint"),
    ("frontend-dev", "cd .. && npm run lint"),
    # dotnet options that write files
    ("backend-dev", "dotnet build --artifacts-path .claude"),
    ("backend-dev", "dotnet build -o out"),
    ("backend-dev", "dotnet restore --packages .claude/x"),
    ("backend-dev", "dotnet build -getProperty:X -getResultOutputFile:.claude/settings.json"),
    ("backend-dev", "dotnet ef migrations add X --force"),
    ("backend-dev", "dotnet ef migrations script --connection Host=x"),
    ("backend-dev", "dotnet ef migrations add X --output-dir ../../.claude"),
    # git options that write a file or reach outside
    ("security-reviewer", "git diff --output=f"),
    ("security-reviewer", "git diff --no-index a b"),
    ("security-reviewer", "git fetch origin --upload-pack=x"),
    ("security-reviewer", "git fetch origin main:refs/heads/x"),
    # devops tools with an option that writes or runs something else
    ("devops", "gitleaks git --report-path x.json"),
    ("devops", "pre-commit run -c x.yaml"),
    ("devops", "actionlint -shellcheck x"),
    ("devops", "aspire publish -o out"),
    # commands no agent runs
    ("backend-dev", "dotnet add package X"),
    ("backend-dev", "dotnet tool install x"),
    ("backend-dev", "dotnet nuget add source x"),
    ("devops", "gh api repos/x/y"),
    ("devops", "gh pr merge 1"),
    ("devops", "gh secret list"),
    ("devops", "aspire add x"),
    # Docker
    ("devops", "docker exec x sh"),
    ("devops", "docker inspect x"),
    ("devops", "docker compose config"),
    ("devops", "docker compose down -v"),
    ("platform-dev", "docker logs -f x"),
    ("platform-dev", "docker ps -H tcp://x"),
    # npm
    ("frontend-dev", "npm install x"),
    ("frontend-dev", "npm audit fix"),
    ("frontend-dev", "npx x"),
    ("frontend-dev", "pnpm i"),
    # secret_guard, for an agent
    ("security-reviewer", f"git diff {E}"),
]
BASH_ALLOW = [
    ("frontend-dev", "cd src/Decisya.Web && npm run lint"),
    ("backend-dev", "dotnet build"),
    ("security-reviewer", "git diff origin/main"),
    ("devops", "actionlint"),
    ("platform-dev", "docker ps"),
]
# secret_guard for the main session (no agent_type): the .env forms, user-secrets, compose output.
MAIN_DENY = [
    f"cat {E}",
    f"cat src/x/{E}.local",
    f"cat ~/AppData/Roaming/Microsoft/UserSecrets/x/{SECRETS}",
    "docker compose config",
    "docker compose convert",
]
MAIN_ALLOW = ["git status"]


def payload(root: Path, agent: str, tool: str, tool_input: dict, agent_id: str | None = None) -> dict:
    data = {"session_id": "carryover", "tool_use_id": uuid.uuid4().hex, "hook_event_name": "PreToolUse",
            "tool_name": tool, "tool_input": tool_input, "cwd": str(root)}
    if agent:
        data["agent_type"] = agent
        data["agent_id"] = agent_id or uuid.uuid4().hex
    return data


def registered_hooks(root: Path) -> list[tuple[re.Pattern, str]]:
    """(matcher, hook file name) for each PreToolUse hook in settings.json, shell or exec form alike."""
    settings = json.loads((root / ".claude" / "settings.json").read_text(encoding="utf-8"))
    found = []
    for entry in settings.get("hooks", {}).get("PreToolUse", []):
        matcher = re.compile(rf"(?:{entry.get('matcher') or '.*'})\Z")
        for hook in entry.get("hooks", []):
            text = json.dumps(hook)
            names = re.findall(r"\.claude/hooks/([A-Za-z0-9_]+\.py)", text.replace("\\\\", "/"))
            assert len(names) == 1, f"cannot tell which hook this registration runs: {text}"
            found.append((matcher, names[0]))
    return found


def decide(root: Path, data: dict) -> str:
    """deny if any hook registered for this tool denies (JSON deny or exit 2), else allow.

    A deny whose reason says the hook itself failed is reported as "error", so a crashing hook can
    never pass for a guardrail that holds.
    """
    env = dict(os.environ, CLAUDE_PROJECT_DIR=str(root))
    for matcher, name in registered_hooks(root):
        if not matcher.match(data["tool_name"]):
            continue
        hook = root / ".claude" / "hooks" / name
        out = subprocess.run([sys.executable, str(hook)], input=json.dumps(data), capture_output=True,
                             text=True, encoding="utf-8", cwd=root, env=env, timeout=60)
        if out.returncode == 2:
            return "error"
        for line in out.stdout.splitlines():
            try:
                output = json.loads(line).get("hookSpecificOutput", {})
            except (ValueError, AttributeError):
                continue
            if output.get("permissionDecision") == "deny":
                return "error" if HOOK_ERROR.search(output.get("permissionDecisionReason") or "") else "deny"
    return "allow"


class Sandbox:
    """A temporary root with a copy of the installed .claude/ and the folders the cases name."""

    def __init__(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        shutil.copytree(ROOT / ".claude", self.root / ".claude", ignore=SKIP)
        for extra in ("CLAUDE.md", "project.json"):
            if (ROOT / extra).is_file():
                shutil.copy2(ROOT / extra, self.root / extra)
        for folder in ("src/Decisya.Web", "src/Decisya.Api", "docs/architecture"):
            (self.root / folder).mkdir(parents=True, exist_ok=True)

    def close(self):
        self.tmp.cleanup()


class CarryoverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.box = Sandbox()
        root = cls.box.root
        jobs = {}
        for agent, rel in WRITE_DENY + WRITE_ALLOW:
            jobs[("write", agent, rel)] = payload(root, agent, "Write", {"file_path": str(root / rel), "content": "x"})
        jobs[("edit", "backend-dev", "CLAUDE.md")] = payload(
            root, "backend-dev", "Edit", {"file_path": str(root / "CLAUDE.md"), "old_string": "a", "new_string": "b"})
        for agent, command in BASH_DENY + BASH_ALLOW:
            jobs[("cmd", agent, command)] = payload(root, agent, "Bash", {"command": command})
        for command in MAIN_DENY + MAIN_ALLOW:
            jobs[("cmd", "", command)] = payload(root, "", "Bash", {"command": command})
        with ThreadPoolExecutor(max_workers=8) as pool:
            results = pool.map(lambda item: (item[0], decide(root, item[1])), jobs.items())
            cls.decisions = dict(results)

    @classmethod
    def tearDownClass(cls):
        cls.box.close()

    def check(self, cases, kind, expected):
        for agent, value in cases:
            with self.subTest(agent=agent or "main", case=value):
                self.assertEqual(self.decisions[(kind, agent, value)], expected)

    def test_writes_are_denied(self):
        self.check(WRITE_DENY, "write", "deny")
        self.assertEqual(self.decisions[("edit", "backend-dev", "CLAUDE.md")], "deny")

    def test_lane_writes_are_allowed(self):
        self.check(WRITE_ALLOW, "write", "allow")

    def test_agent_commands_are_denied(self):
        self.check(BASH_DENY, "cmd", "deny")

    def test_listed_agent_commands_are_allowed(self):
        self.check(BASH_ALLOW, "cmd", "allow")

    def test_secret_reads_are_denied_for_the_main_session(self):
        self.check([("", c) for c in MAIN_DENY], "cmd", "deny")
        self.check([("", c) for c in MAIN_ALLOW], "cmd", "allow")


class FailClosedTests(unittest.TestCase):
    def setUp(self):
        self.box = Sandbox()

    def tearDown(self):
        self.box.close()

    def test_both_guard_hooks_are_registered(self):
        names = {name for _, name in registered_hooks(self.box.root)}
        self.assertEqual(names & EXPECTED_HOOKS, EXPECTED_HOOKS)

    def test_the_first_deny_freezes_the_agent_run(self):
        run = uuid.uuid4().hex
        root = self.box.root
        self.assertEqual(decide(root, payload(root, "backend-dev", "Bash", {"command": "dotnet add package X"}, run)), "deny")
        self.assertEqual(decide(root, payload(root, "backend-dev", "Bash", {"command": "dotnet build"}, run)), "deny")
        self.assertEqual(decide(root, payload(root, "backend-dev", "Bash", {"command": "dotnet build"})), "allow",
                         "another run is not frozen")

    def test_an_unreadable_boundaries_file_fails_closed_for_writes(self):
        """Writes need boundaries.json and deny without it. Bash keeps the agent's own tools: line
        (.claude/agents/<agent>.md), so an unlisted command stays denied and a listed one runs."""
        root = self.box.root
        (root / ".claude" / "boundaries.json").write_text("{not json", encoding="utf-8")
        self.assertEqual(decide(root, payload(root, "backend-dev", "Write",
                                             {"file_path": str(root / "src/Decisya.Api/X.cs"), "content": "x"})), "error")
        self.assertEqual(decide(root, payload(root, "backend-dev", "Bash", {"command": "dotnet add package X"})), "deny")
        self.assertEqual(decide(root, payload(root, "frontend-dev", "Bash", {"command": "npm install x"})), "deny")


if __name__ == "__main__":
    unittest.main()
