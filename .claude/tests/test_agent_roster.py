"""#74 agent roster: lanes, the Docker allow-list and the command policy for the split agents
(docs/security/threat-models/agent-roster.md, G4-74-01 to 05, 08b, 14).

Lane checks run against the real .claude/boundaries.json; nothing is written to disk. Docker
commands are only evaluated by the hook, never executed.
"""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE.parent / "hooks"))
import _hooklib as lib  # noqa: E402
import agent_boundaries  # noqa: E402

CONFIG = json.loads((ROOT / ".claude" / "boundaries.json").read_text(encoding="utf-8"))


def write_decision(agent: str, rel: str):
    payload = {"agent_type": agent, "tool_name": "Write", "cwd": str(ROOT),
               "tool_input": {"file_path": str(ROOT / rel)}}
    return agent_boundaries.decide_write(payload, CONFIG, agent)


def bash_decision(agent: str, command: str):
    return agent_boundaries.decide_bash({"tool_input": {"command": command}}, agent)


class LaneTests(unittest.TestCase):
    """G4-74-02 and 03: the proposed table with G3's narrowings."""

    ROWS = [
        ("platform-dev", "src/Decisya.AppHost/AppHost.cs", True),
        ("platform-dev", "deploy/postgres/init/x.sh", True),
        # Any file under deploy/keycloak/ (never the realm file's own name: only tests/, docs/ and the
        # AppHost may name it, RealmExportFileTests G4-17-12).
        ("platform-dev", "deploy/keycloak/example.json", False),
        ("platform-dev", "src/Decisya.Api/Program.cs", False),
        ("backend-dev", "src/Decisya.AppHost/AppHost.cs", False),
        ("backend-dev", "deploy/postgres/init/x.sh", False),
        ("backend-dev", "tests/Decisya.Identity.Tests/x.cs", False),
        ("backend-dev", "tests/Decisya.AppHost.Tests/x.cs", False),
        ("backend-dev", "src/Decisya.Web/x.ts", False),
        ("backend-dev", "src/Modules/Ledger/x.cs", True),
        ("backend-dev", "src/Decisya.Infrastructure.Ai/x.cs", True),
        ("identity-dev", "deploy/keycloak/example.json", True),
        ("identity-dev", "src/Decisya.Api/Authentication/JwtSetup.cs", True),
        ("identity-dev", "src/Decisya.Api/Program.cs", True),
        ("identity-dev", "src/Decisya.AppHost/AppHost.cs", False),
        ("identity-dev", "src/Modules/x/y.cs", False),
        ("identity-dev", "src/Decisya.Api/Decisya.Api.csproj", False),
    ]

    def test_lane_table(self):
        for agent, rel, allowed in self.ROWS:
            with self.subTest(agent=agent, path=rel):
                decision = write_decision(agent, rel)
                if allowed:
                    self.assertIsNone(decision)
                else:
                    self.assertIsNotNone(decision)

    def test_never_in_any_lane(self):
        """G4-74-04: package pins, agent config and pipeline manifests are never agent-writable."""
        for agent in CONFIG["agents"]:
            for rel in ("Directory.Packages.props", "NuGet.config", ".claude/x", "CLAUDE.md", "docs/ai/pipeline/x.md"):
                with self.subTest(agent=agent, path=rel):
                    self.assertIsNotNone(write_decision(agent, rel))


class DockerAllowListTests(unittest.TestCase):
    """G4-74-05 and 08b: an allow-list per agent, evaluated after normalisation."""

    PLATFORM_DENY = [
        "docker inspect decisya-x", "docker container inspect x", "docker inspect -f '{{.Config.Env}}' x",
        "docker exec x env", "docker cp x:/a b", "docker top x", "docker run --rm -v C:/:/h alpine",
        "docker rm -f x", "docker stop x", "docker volume rm decisya-postgres-data", "docker volume prune -f",
        "docker system prune", "docker compose down -v", "docker.exe volume rm x",
        "docker -H npipe:////./pipe/docker_engine volume rm x", "docker --context default rm x",
        '"docker" volume rm x', "ls; docker rm x", "DOCKER_HOST=x docker rm y", "sudo docker rm x",
        "curl --unix-socket /var/run/docker.sock http://x/containers/json",
    ]
    PLATFORM_ALLOW = ["docker ps --filter name=decisya-", "docker logs --tail 200 decisya-keycloak",
                      "docker port decisya-postgres", "docker volume ls"]

    def test_platform_dev_denied_forms(self):
        for command in self.PLATFORM_DENY:
            with self.subTest(command=command):
                decision = bash_decision("platform-dev", command)
                self.assertIsNotNone(decision)
                self.assertIn(decision[0], {"agent.docker", "agent.docker-socket"})

    # G6-74-01/02/06/09: shell-syntax bypasses found in the G6 review
    SHELL_BYPASSES = [
        ("backend-dev", "(docker volume rm decisya-postgres-data)"),
        ("identity-dev", "echo $(docker rm -f decisya-postgres)"),
        ("platform-dev", "docker ps $(docker volume rm decisya-postgres-data)"),
        ("platform-dev", "docker ps `docker stop x`"),
        ("platform-dev", "docker>NUL volume rm x"),
        ("platform-dev", "d''ocker volume rm x"),
        ("platform-dev", r"do\cker volume rm x"),
        ("platform-dev", "/usr/bin/docker volume rm x"),
        ("platform-dev", "C:/Docker/docker.exe rm x"),
        ("platform-dev", "{ docker rm x; }"),
        ("platform-dev", "docker logs -f decisya-keycloak"),
        ("platform-dev", "docker logs --follow x"),
        ("devops", "docker compose down -vt1"),
        ("devops", "docker compose logs -f"),
        # G6-74-10: Windows backslash paths, quoted and unquoted
        ("platform-dev", r'"C:\Program Files\Docker\Docker\resources\bin\docker.exe" volume rm x'),
        ("platform-dev", r"C:\Docker\docker.exe volume rm x"),
        ("backend-dev", r"cmd /c C:\Docker\docker.exe rm x"),
    ]

    def test_shell_syntax_bypasses_denied(self):
        for agent, command in self.SHELL_BYPASSES:
            with self.subTest(agent=agent, command=command):
                self.assertIsNotNone(bash_decision(agent, command))

    def test_platform_dev_allowed_forms(self):
        for command in self.PLATFORM_ALLOW:
            with self.subTest(command=command):
                self.assertIsNone(bash_decision("platform-dev", command))

    def test_agents_without_docker(self):
        for agent in ("backend-dev", "identity-dev", "frontend-dev", "test-engineer"):
            with self.subTest(agent=agent):
                self.assertIsNotNone(bash_decision(agent, "docker ps"))

    def test_devops_keeps_its_set_but_never_removes_volumes(self):
        for command in ("docker compose down", "docker compose logs", "docker compose up -d", "docker ps"):
            with self.subTest(command=command):
                self.assertIsNone(bash_decision("devops", command))
        for command in ("docker compose down -v", "docker compose down --volumes", "docker compose down --rmi all",
                        "docker compose down -vt 1", "docker compose down -tv 1", "docker compose down --volumes=true",
                        "docker-compose down -v", "docker volume rm x", "docker inspect x"):
            with self.subTest(command=command):
                self.assertIsNotNone(bash_decision("devops", command))


class CommandPolicyTests(unittest.TestCase):
    """G4-74-14: the #39 command policy applies to both new agents."""

    def test_package_and_gh_denies(self):
        for agent in ("platform-dev", "identity-dev"):
            for command in ("dotnet package add Foo", "dotnet add src/X package Foo", "dotnet tool install -g x",
                            "gh issue close 5", "gh api repos/x", "aspire add redis"):
                with self.subTest(agent=agent, command=command):
                    self.assertIsNotNone(bash_decision(agent, command))
        for agent in ("platform-dev", "identity-dev"):
            with self.subTest(agent=agent):
                self.assertIsNone(bash_decision(agent, "dotnet build -warnaserror"))


class FailClosedTests(unittest.TestCase):
    """G4-74-01: with boundaries.json unreadable, both new agents are still listed (frontmatter
    fallback) and their writes fail closed."""

    def test_unreadable_boundaries_denies_both_new_agents(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".claude" / "agents").mkdir(parents=True)
            (root / ".claude" / "boundaries.json").write_text("{ not json", encoding="utf-8")
            for name in ("platform-dev", "identity-dev"):
                (root / ".claude" / "agents" / f"{name}.md").write_text(f"---\nname: {name}\n---\n", encoding="utf-8")
            saved = (lib.ROOT, lib.LOG_DIR, lib.LOG_FILE)
            lib.ROOT, lib.LOG_DIR = root, root / ".agent-logs"
            lib.LOG_FILE = lib.LOG_DIR / "hooks.jsonl"
            try:
                for name in ("platform-dev", "identity-dev"):
                    payload = {"agent_type": name, "tool_name": "Write", "cwd": str(root),
                               "tool_input": {"file_path": str(root / "src" / "x.cs")}}
                    out = io.StringIO()
                    saved_stdin = sys.stdin
                    sys.stdin = io.StringIO(json.dumps(payload))
                    try:
                        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                            agent_boundaries.main()
                    finally:
                        sys.stdin = saved_stdin
                    with self.subTest(agent=name):
                        self.assertEqual(json.loads(out.getvalue())["hookSpecificOutput"]["permissionDecision"], "deny")
            finally:
                lib.ROOT, lib.LOG_DIR, lib.LOG_FILE = saved


if __name__ == "__main__":
    unittest.main()
