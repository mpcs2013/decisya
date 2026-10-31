"""Decisya's own lint rules, kept after the Norma lock (#246, ADR-0023).

Norma's lint.py replaced Decisya's; a project cannot add lint patterns except through a pack. The dotnet pack
carries the generic .NET bans (test filters, MTP logger options, the paid assertion library, the xUnit v2
template), which never come back here. The rules below are Decisya's alone, ported unchanged from the
pre-lock .claude/scripts/lint.py:
the ADR-0010 sandbox checks over .devcontainer/ and three text bans over .claude/ and CLAUDE.md. Norma's
pre-push and CI's claude-config run every test here, so they keep gating every push and PR.
"""
import re
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CLAUDE = ROOT / ".claude"
SELF = Path(__file__).resolve()

BANNED = [
    (r"\bnet11(\.0)?\b", "the repo targets net10.0 (ADR-0009)"),
    (r"\bV3(\.\d+)*\s+[Ss]ession|\bV4\s+[Aa]ccess control|\bV5\s+Validation and encoding|\bV7\s+Error handling",
     "ASVS 4.0 chapter label; ASVS 5.0 numbering differs (see asvs-checklist references)"),
    (r"\bgit diff main\b", "diff against origin/main...HEAD; local main may be stale"),
]
# ADR-0010: the agent sandbox never sees the host Docker daemon or host secret folders.
SANDBOX_FORBIDDEN = [
    (r"docker\.sock", "mounts the host Docker socket (full host control)"),
    (r"UserSecrets|usersecrets", "mounts dotnet user-secrets"),
    (r"localEnv:(USERPROFILE|HOME)\}?[/\\]\.(ssh|claude|aws|azure|kube|docker)\b", "mounts a host credential folder"),
    (r"(^|[\s\"'=])(~|\$HOME|\$\{HOME\})[/\\]\.(ssh|aws|azure|kube)\b", "mounts a host credential folder"),
    (r"SSH_AUTH_SOCK", "forwards the SSH agent"),
    # #41 threat model (G4-41-02 stop rule, O-41-01): never, no marker can allow these.
    (r"--privileged|\"privileged\"\s*:\s*true|privileged:\s*true", "privileged container (never; #41 fallback is no sidecar)"),
    (r"seccomp\s*[:=]\s*[\"']?unconfined", "seccomp=unconfined (never shipped)"),
    (r"\b(CAP_)?SYS_ADMIN\b", "adds CAP_SYS_ADMIN"),
    (r"^\s*[\"']?(pid|ipc|uts|cgroup|network_mode|userns_mode)[\"']?\s*:\s*[\"']?host\b|--(pid|ipc|uts|network|net|userns)[= ]host\b",
     "joins a host namespace"),
    (r"^\s*[\"']?o[\"']?\s*:\s*[\"']?[^\"'\n]*\bbind\b", "volume driver_opts bind (host path mount)"),
    (r"[{,]\s*[\"']?o[\"']?\s*:\s*[\"']?[^,}\n]*\bbind\b", "volume driver_opts bind in flow style (host path mount)"),
    # Conditional rungs: only with an explicit "sandbox-lint: allow-rung (<reason>)" marker on the line.
    (r"systempaths\s*[:=]\s*[\"']?unconfined",
     "systempaths=unconfined (only with no-new-privileges on and no setuid/file-capability binary; mark 'sandbox-lint: allow-rung')"),
    (r"apparmor\s*[:=]\s*[\"']?unconfined", "apparmor=unconfined (only if AppArmor is inactive; mark 'sandbox-lint: allow-rung')"),
]
SANDBOX_RUNG_MARKER = "sandbox-lint: allow-rung"
SANDBOX_RUNG_PATTERNS = {r"systempaths\s*[:=]\s*[\"']?unconfined", r"apparmor\s*[:=]\s*[\"']?unconfined"}
# #41 G6 (N41-01, N41-04): the only capabilities and devices any sandbox service may add.
SANDBOX_ALLOWED_CAPS = {"SETUID", "SETGID", "SYS_CHROOT"}
SANDBOX_ALLOWED_DEVICES = {"/dev/net/tun"}


def rel(path: Path, root: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.as_posix()


def banned_problems(path: Path, root: Path = ROOT) -> list[str]:
    found = []
    for i, line in enumerate(path.read_text(encoding="utf-8", errors="ignore").splitlines(), start=1):
        for pattern, msg in BANNED:
            if re.search(pattern, line):
                found.append(f"{rel(path, root)}:{i}: banned pattern /{pattern}/: {msg}")
    return found


def sandbox_line_problems(path: Path, root: Path = ROOT) -> list[str]:
    found = []
    for i, line in enumerate(path.read_text(encoding="utf-8", errors="ignore").splitlines(), start=1):
        if line.lstrip().startswith(("#", "//")):
            continue
        for pattern, msg in SANDBOX_FORBIDDEN:
            allowed = pattern in SANDBOX_RUNG_PATTERNS and SANDBOX_RUNG_MARKER in line
            if re.search(pattern, line, re.IGNORECASE) and not allowed:
                found.append(f"{rel(path, root)}:{i}: sandbox-config: {msg} (ADR-0010)")
    return found


def compose_list_problems(path: Path, root: Path = ROOT) -> list[str]:
    """cap_add and devices entries must be on the allow-lists in every list form; in overlays (every compose file
    except the base compose.yaml, which binds the repository by design) no host path may be mounted (#41 N41-16)."""
    found = []

    def check_item(line: int, key: str, item: str) -> None:
        if key == "cap_add":
            if item.upper().removeprefix("CAP_") not in SANDBOX_ALLOWED_CAPS:
                found.append(f"{rel(path, root)}:{line}: sandbox-config: cap_add {item} is not in "
                             f"{sorted(SANDBOX_ALLOWED_CAPS)} (ADR-0010, #41 N41-01)")
        elif item.split(":", 1)[0] not in SANDBOX_ALLOWED_DEVICES:
            found.append(f"{rel(path, root)}:{line}: sandbox-config: device {item} is not in "
                         f"{sorted(SANDBOX_ALLOWED_DEVICES)} (ADR-0010, #41 N41-04)")

    lines = [raw.split("#", 1)[0].rstrip() for raw in path.read_text(encoding="utf-8", errors="ignore").splitlines()]
    i = 0
    while i < len(lines):
        m = re.match(r"^(\s*)[\"']?(cap_add|devices)[\"']?\s*:\s*(.*)$", lines[i])
        if not m:
            i += 1
            continue
        indent, key, rest, j = len(m.group(1)), m.group(2), m.group(3).strip(), i + 1
        if not rest:
            while j < len(lines) and not lines[j].strip():
                j += 1
            if j < len(lines) and lines[j].strip().startswith("["):
                rest, j = lines[j].strip(), j + 1
        if rest.startswith("["):
            line_no, buf = j, rest
            while "]" not in buf and j < len(lines):
                buf, j = buf + " " + lines[j].strip(), j + 1
            for item in (s.strip().strip("\"'") for s in buf.strip().strip("[]").split(",")):
                if item:
                    check_item(line_no, key, item)
            i = j
            continue
        k = i + 1
        while k < len(lines):
            text = lines[k]
            if text.strip():
                if len(text) - len(text.lstrip()) <= indent:
                    break
                if text.lstrip().startswith("-"):
                    check_item(k + 1, key, text.lstrip()[1:].strip().strip("\"'"))
            k += 1
        i = k

    if path.name == "compose.yaml":
        return found
    parents: list[tuple[int, str]] = []
    for n, text in enumerate(lines, start=1):
        if not text.strip():
            continue
        indent = len(text) - len(text.lstrip())
        while parents and parents[-1][0] >= indent:
            parents.pop()
        if re.search(r"\btype[\"']?\s*:\s*[\"']?bind\b", text):
            found.append(f"{rel(path, root)}:{n}: sandbox-config: bind mount in a compose overlay (ADR-0010, #41 N41-16)")
        in_volumes = bool(parents) and parents[-1][1] == "volumes"
        if in_volumes and re.match(r"^\s*-\s*[\"']?[/.~$][^\s\"',]*:", text):
            found.append(f"{rel(path, root)}:{n}: sandbox-config: short-syntax host path mount in a compose overlay "
                         "(ADR-0010, #41 N41-16)")
        km = re.match(r"^\s*[\"']?([\w.-]+)[\"']?\s*:\s*$", text)
        if km:
            parents.append((indent, km.group(1)))
    return found


def sandbox_files(devcontainer: Path) -> list[Path]:
    """Only files that can define host mounts, capabilities or privileges."""
    return sorted(p for p in devcontainer.rglob("*") if p.is_file() and (
        p.name in {"docker-compose.yml", "devcontainer.json", ".devcontainer.json"}
        or re.fullmatch(r"compose(\.[\w-]+)?\.ya?ml", p.name)
        or p.name.startswith("Dockerfile") or p.name.endswith(".Dockerfile")))


def sandbox_problems(devcontainer: Path, root: Path = ROOT) -> list[str]:
    found = []
    for path in sandbox_files(devcontainer):
        found += sandbox_line_problems(path, root)
        if path.suffix in {".yaml", ".yml"}:
            found += compose_list_problems(path, root)
    return found


class RepositoryTests(unittest.TestCase):
    def test_no_banned_text_in_claude_files(self):
        files = [p for p in CLAUDE.rglob("*") if p.is_file() and p.suffix in {".md", ".json", ".py", ".cs", ".csproj"}
                 and "__pycache__" not in p.parts and p.resolve() != SELF] + [ROOT / "CLAUDE.md"]
        problems = [msg for path in sorted(files) for msg in banned_problems(path)]
        self.assertEqual(problems, [])

    def test_the_sandbox_config_passes(self):
        self.assertTrue(sandbox_files(ROOT / ".devcontainer"), "no sandbox files found: the scope is wrong")
        self.assertEqual(sandbox_problems(ROOT / ".devcontainer"), [])


class RedCaseTests(unittest.TestCase):
    """Each rule still fires (from test_lint.py before the lock)."""

    def scan(self, name: str, text: str) -> list[str]:
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            path = root / ".devcontainer" / name
            path.parent.mkdir(parents=True)
            path.write_text(text, encoding="utf-8")
            return sandbox_problems(root / ".devcontainer", root)

    def test_every_forbidden_line_fires(self):
        lines = ["      - /var/run/docker.sock:/var/run/docker.sock", "    source: ${localEnv:USERPROFILE}/.ssh",
                 "      - ~/.aws:/root/.aws", "    environment: [SSH_AUTH_SOCK]", "    privileged: true",
                 "    security_opt: [seccomp=unconfined]", "    cap_add: [SYS_ADMIN]", "    network_mode: host",
                 "      o: bind", "    security_opt: [systempaths=unconfined]", "    security_opt: [apparmor=unconfined]",
                 "    target: /root/.microsoft/usersecrets"]
        for line in lines:
            with self.subTest(line=line):
                self.assertTrue(self.scan("compose.yaml", f"services:\n  claude:\n{line}\n"))

    def test_only_the_two_rungs_accept_the_marker(self):
        ok = "    security_opt: [apparmor=unconfined]  # sandbox-lint: allow-rung (AppArmor inactive)\n"
        self.assertEqual(self.scan("compose.yaml", f"services:\n  claude:\n{ok}"), [])
        never = "    privileged: true  # sandbox-lint: allow-rung (no)\n"
        self.assertTrue(self.scan("compose.yaml", f"services:\n  claude:\n{never}"))

    def test_caps_and_devices_follow_the_allow_lists_in_every_form(self):
        self.assertEqual(self.scan("compose.yaml", "services:\n  c:\n    cap_add: [SETUID, CAP_SETGID]\n"
                                                   "    devices:\n      - /dev/net/tun\n"), [])
        for body in ("    cap_add: [NET_ADMIN]\n", "    cap_add:\n      - NET_RAW\n",
                     "    cap_add:\n      [SETUID,\n       NET_ADMIN]\n", "    devices: [/dev/kvm]\n"):
            with self.subTest(body=body):
                self.assertTrue(self.scan("compose.yaml", "services:\n  c:\n" + body))

    def test_overlays_mount_no_host_path(self):
        self.assertTrue(self.scan("compose.docker.yaml", "services:\n  c:\n    volumes:\n      - type: bind\n"))
        self.assertTrue(self.scan("compose.docker.yaml", "services:\n  c:\n    volumes:\n      - ./x:/y\n"))
        self.assertEqual(self.scan("compose.yaml", "services:\n  c:\n    volumes:\n      - ./x:/y\n"), [],
                         "the base compose.yaml binds the repository by design")

    def test_comments_and_other_files_are_not_scanned(self):
        self.assertEqual(self.scan("compose.yaml", "# privileged: true\n"), [])
        self.assertEqual(self.scan("managed-settings.json", '{"privileged": true}\n'), [])

    def test_the_text_bans_fire(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "x.md"
            for text in ("target " + "net" + "11.0", "V3 " + "Session management", "run git diff " + "main"):
                with self.subTest(text=text):
                    path.write_text(text + "\n", encoding="utf-8")
                    self.assertTrue(banned_problems(path, Path(d)))


if __name__ == "__main__":
    unittest.main()
