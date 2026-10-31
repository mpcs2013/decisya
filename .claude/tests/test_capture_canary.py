"""Norma's usage capture keeps counts only (#246, G3 M5, G6-246-04).

The installed .claude/scripts/capture.py reads Claude Code's session records under the user profile. This test runs it,
start and finish, in a throwaway git repository with a copy of .claude/, against a fixture config folder
(CLAUDE_CONFIG_DIR) whose records carry canaries in every place a record can hold text: the user prompt, the assistant
text, a tool call and its result, a token-shaped string, a profile path and a subagent's records. Nothing but counts,
model, source and agent may reach the capture folder. Canaries are built at run time, so none sits in source.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SKIP = shutil.ignore_patterns("tests", "__pycache__", "*.local.json", "*.local.*", ".agent-logs")


def canary(kind: str) -> str:
    return f"canary-{kind}-" + uuid.uuid4().hex[:10]


@unittest.skipUnless(shutil.which("git"), "needs git")
class CaptureCanaryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = Path(self.tmp.name)
        self.repo, self.config = base / "repo", base / "config"
        shutil.copytree(ROOT / ".claude", self.repo / ".claude", ignore=SKIP)
        (self.repo / ".gitignore").write_text("/.local.usage/\n", encoding="utf-8")
        env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
        for args in (["init", "-q", "-b", "main"], ["config", "user.name", "t"], ["config", "user.email", "t@example.invalid"],
                     ["config", "commit.gpgsign", "false"], ["add", "-A"], ["commit", "-q", "-m", "t"]):
            subprocess.run(["git", *args], cwd=self.repo, check=True, capture_output=True, env=env)
        self.env = dict(env, CLAUDE_CONFIG_DIR=str(self.config))
        self.canaries = {k: canary(k) for k in ("prompt", "text", "tool", "result", "token", "profile", "subagent")}

    def tearDown(self):
        self.tmp.cleanup()

    def capture(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-I", str(self.repo / ".claude" / "scripts" / "capture.py"), *args],
                              cwd=self.repo, capture_output=True, text=True, encoding="utf-8", env=self.env, timeout=120)

    def records(self) -> None:
        c, now = self.canaries, datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z")
        folder = self.config / "projects" / "fixture"
        common = {"cwd": str(self.repo), "gitBranch": "issue/1-canary", "timestamp": now, "sessionId": "s1",
                  "transcript_path": f"C:\\Users\\{c['profile']}\\.claude\\projects\\fixture\\s1.jsonl"}
        session = [
            dict(common, type="user", message={"role": "user", "content": f"please read {c['prompt']}"}),
            dict(common, type="assistant", message={
                "id": "msg_" + uuid.uuid4().hex[:12], "model": "claude-opus-5-5", "role": "assistant",
                "content": [{"type": "text", "text": c["text"]},
                            {"type": "tool_use", "id": "t1", "name": "Bash", "input": {"command": f"echo {c['tool']}"}}],
                "usage": {"input_tokens": 120, "output_tokens": 30, "cache_read_input_tokens": 7,
                          "cache_creation_input_tokens": 0, "service_tier": c["token"]}}),
            dict(common, type="user", message={"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "t1", "content": f"{c['result']} sk-{c['token']}"}]}),
        ]
        folder.mkdir(parents=True)
        (folder / "s1.jsonl").write_text("".join(json.dumps(r) + "\n" for r in session), encoding="utf-8")
        sub = folder / "s1" / "subagents"
        sub.mkdir(parents=True)
        agent_record = dict(common, type="assistant", isSidechain=True, message={
            "id": "msg_" + uuid.uuid4().hex[:12], "model": "claude-opus-5-5", "role": "assistant",
            "content": [{"type": "text", "text": c["subagent"]}], "usage": {"input_tokens": 40, "output_tokens": 9}})
        (sub / "agent-a1.jsonl").write_text(json.dumps(agent_record) + "\n", encoding="utf-8")
        (sub / "agent-a1.meta.json").write_text(json.dumps({"agentType": "security-reviewer"}), encoding="utf-8")

    def test_only_counts_reach_the_capture_folder(self):
        started = self.capture("start", "1", "--where", "this-machine")
        self.assertEqual(started.returncode, 0, started.stdout + started.stderr)
        self.records()
        finished = self.capture("finish", "1")
        self.assertEqual(finished.returncode, 0, finished.stdout + finished.stderr)
        folder = self.repo / ".local.usage"
        captures = sorted(folder.glob("capture-*.json"))
        self.assertTrue(captures, "no capture was written")
        written = "".join(p.read_text(encoding="utf-8", errors="replace") for p in folder.rglob("*") if p.is_file())
        printed = finished.stdout + finished.stderr + started.stdout + started.stderr
        for kind, value in self.canaries.items():
            with self.subTest(canary=kind):
                self.assertNotIn(value, written)
                self.assertNotIn(value, printed)
        data = next(d for d in (json.loads(p.read_text(encoding="utf-8")) for p in captures) if d.get("issue") == 1)
        self.assertEqual(data["project_id"], "GL-0001")
        rows = data["usage"]["by_model"]
        totals = {k: sum(row.get(k, 0) for row in rows) for k in ("input_tokens", "output_tokens")}
        self.assertEqual(totals, {"input_tokens": 160, "output_tokens": 39}, "both the session and the subagent count")
        self.assertEqual({row["source"] for row in rows}, {"main", "subagent"})
        allowed = {"model", "source", "agent", "input_tokens", "output_tokens", "cache_read_tokens",
                   "cache_creation_tokens", "cache_creation_5m_tokens", "cache_creation_1h_tokens"}
        for row in rows:
            self.assertLessEqual(set(row), allowed, "a capture row holds only counts, model, source and agent")

    def test_nothing_is_written_when_the_folder_is_not_ignored(self):
        (self.repo / ".gitignore").write_text("", encoding="utf-8")
        result = self.capture("start", "1", "--where", "this-machine")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.repo / ".local.usage" / "start-1.json").exists())


if __name__ == "__main__":
    unittest.main()
