"""settings.json through the Norma lock keeps every entry Decisya had (#246, G3 M3, ADR-0023).

Norma's `bootstrap --adopt` rewrites .claude/settings.json: it may only append to `permissions.deny` and register
the two core hooks. The fixture pins the allow, ask and deny lists as they were before the lock, so a reading by
eye is never the only check. The two hook registrations must be exactly the consumer guide's exec form.
"""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SETTINGS = ROOT / ".claude" / "settings.json"
PINNED = Path(__file__).resolve().parent / "fixtures" / "settings-permissions-pre-norma.json"

CORE_HOOKS = [
    {"matcher": "Write|Edit|MultiEdit|NotebookEdit|Bash|PowerShell|Monitor",
     "hooks": [{"type": "command", "command": "python",
                "args": ["-I", "${CLAUDE_PROJECT_DIR}/.claude/hooks/agent_boundaries.py"],
                "timeout": 10, "onFailure": "block"}]},
    {"matcher": "Bash|PowerShell|Monitor",
     "hooks": [{"type": "command", "command": "python",
                "args": ["-I", "${CLAUDE_PROJECT_DIR}/.claude/hooks/secret_guard.py"],
                "timeout": 10, "onFailure": "block"}]},
]


class SettingsAdoptionTests(unittest.TestCase):
    def setUp(self):
        self.settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        self.pinned = json.loads(PINNED.read_text(encoding="utf-8"))

    def test_allow_and_ask_are_unchanged(self):
        for key in ("allow", "ask"):
            with self.subTest(list=key):
                self.assertEqual(self.settings["permissions"][key], self.pinned[key])

    def test_every_pinned_deny_stays_in_order_and_only_appends_follow(self):
        deny = self.settings["permissions"]["deny"]
        self.assertEqual(deny[:len(self.pinned["deny"])], self.pinned["deny"], "adopt only appends to deny")
        self.assertEqual(len(deny), len(set(deny)), "a deny entry repeated")

    def test_the_file_tools_never_reach_the_operations_folder(self):
        """#259 (G3 M2): tracked, so every clone denies Read (Grep, Glob) and Edit, not only one local file.
        `./` is the project root; a leading `/` would mean the settings file's own folder."""
        deny = self.settings["permissions"]["deny"]
        name = ".local." + "ops"
        for rule in (f"Read(./{name}/**)", f"Read(./**/{name}/**)", f"Edit(./{name}/**)", f"Edit(./**/{name}/**)"):
            with self.subTest(rule=rule):
                self.assertIn(rule, deny)

    def test_the_core_hooks_are_registered_exactly(self):
        self.assertEqual(self.settings["hooks"]["PreToolUse"], CORE_HOOKS)

    def test_no_other_top_level_key_appears(self):
        self.assertLessEqual(set(self.settings), {"permissions", "hooks"})


class ProjectValueTests(unittest.TestCase):
    """G6-246-03: Decisya's project.json values that no other test pins. The CI ignore lists are pinned path by
    path through test_realm_guard_trigger (pre-push needs_build against ci.yml's lanes)."""

    def setUp(self):
        self.project = json.loads((ROOT / ".claude" / "project.json").read_text(encoding="utf-8"))
        self.ci = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")

    def test_unit_test_filters_match_ci(self):
        line = next(ln for ln in self.ci.splitlines() if "dotnet test --no-build --filter-not-trait" in ln)
        pairs = re.findall(r'(--filter-(?:not-)?trait) "([^"]+)"', line)
        flat = [part for pair in pairs for part in pair]
        self.assertTrue(flat)
        self.assertEqual(self.project["packs"]["dotnet"]["unit_test_filters"], flat)
        self.assertEqual(self.project["packs"]["dotnet"]["solution"], "decisya.slnx")

    def test_locked_restore_uses_cis_variable(self):
        name = self.project["packs"]["dotnet"]["locked_restore"]["env_name"]
        self.assertEqual(name, "DECISYA_LOCKED_RESTORE")
        self.assertRegex(self.ci, rf'(?m)^\s+{name}:\s*"true"\s*$')

    def test_agent_and_gate_values(self):
        self.assertEqual(self.project["agent_boundaries"]["cd_targets"], ["src/Decisya.Web"])
        # N-016 (#259): the operations folder, by the letter after `.local.`; never a `.local.u…` or `.local.d…` name.
        self.assertEqual(self.project["secret_guard"]["blocked_folders"], [".local." + "ops"])
        self.assertEqual(self.project["gates"]["approvers"], ["Marco"])
        self.assertEqual(self.project["gates"]["docs_line_from"], 76)
        self.assertEqual(self.project["repository"]["visibility"], "public")
        self.assertEqual(self.project["usage"], {"capture_folder": ".local.usage", "project_id": "GL-0001"})


class MainSessionRouteTests(unittest.TestCase):
    """#261 (G3 M-2): Norma's lint skips the `main-session` row, so Decisya pins it. Every glob it names is in the
    shared deny (so no agent can write it), it names no agent lane, and the signer files stay out of it."""

    def setUp(self):
        self.boundaries = json.loads((ROOT / ".claude" / "boundaries.json").read_text(encoding="utf-8"))
        rows = [r for r in self.boundaries["routing"]["G4"] if r["agent"] == "main-session"]
        self.assertEqual(len(rows), 1, "exactly one main-session row")
        self.row = rows[0]
        self.globs = re.findall(r"`([^`]+)`", self.row["paths"])

    def test_every_named_glob_is_denied_to_every_agent(self):
        deny = set(self.boundaries["deny"])
        self.assertTrue(self.globs)
        for glob in self.globs:
            with self.subTest(glob=glob):
                self.assertIn(glob, deny)
                self.assertIn(f"**/{glob}", deny, "denied at any depth too")

    def test_no_lane_and_no_signer_file(self):
        lanes = {path for paths in self.boundaries["agents"].values() for path in paths}
        for glob in self.globs:
            with self.subTest(glob=glob):
                self.assertNotIn(glob, lanes)
                self.assertNotIn("allowed-signers", glob)
                self.assertNotIn("allowed_signers", glob)


class ReviewRequiredTests(unittest.TestCase):
    """G3 M2: every path Decisya's own gates.py made review-required still is, through the core's list plus
    `gates.review_required_paths` (gates.REVIEW_REQUIRED_PATHS, the regex the gate checker uses)."""
    PATHS = [
        ".github/rulesets/main.json", ".github/dependabot.yml", ".github/allowed-signers/norma",
        ".github/allowed-signers/ostium", ".github/scripts/image_scan.py", ".github/image-scan/exceptions.json",
        ".github/release/Dockerfile", ".github/zap/hook.py", ".github/workflows/ci.yml", "Directory.Build.targets",
        ".dockerignore", "release-please-config.json", ".devcontainer/sandbox.py", "deploy/compose/stackctl.py",
        "deploy/compose/stackguards.py", "deploy/tests/test_stackctl.py",
        "tests/Decisya.Identity.Tests/RealmGuard.cs", "tests/Decisya.Identity.Tests/RealmGuardTests.cs",
        "tests/Decisya.Identity.Tests/realm-guard-cases.json", ".claude/boundaries.json", ".claude/settings.json",
        ".claude/agents/devops.md", ".claude/hooks/agent_boundaries.py", ".claude/scripts/gates.py",
        ".claude/skills/issue/SKILL.md", ".claude/addenda/security-reviewer.md", ".claude/project.json", "CLAUDE.md",
    ]

    def test_decisya_review_required_paths_hold(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("_norma_gates", ROOT / ".claude" / "scripts" / "gates.py")
        gates = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(gates)
        for path in self.PATHS:
            with self.subTest(path=path):
                self.assertTrue(gates.REVIEW_REQUIRED_PATHS.match(path))
        self.assertIsNone(gates.REVIEW_REQUIRED_PATHS.match("docs/x.md"), "the list is not match-everything")


class NormaSignerTests(unittest.TestCase):
    """G3 M4: Norma's release signer lives in its own file, like Ostium's (ADR-0019 A2, ADR-0023)."""
    LINE = (b'norma-release namespaces="git" ssh-ed25519 '
            b"AAAAC3NzaC1lZDI1NTE5AAAAICbE/7Syfn+2hYqCSQT8sBxdTwslZFcsP/Cpfz93a6SF\n")
    FINGERPRINT = "SHA256:FN0FhN2iaOAzUvB2d6zWXoORMikapGpRS1wXUt9noCo"  # Norma's release notices N-009 to N-017

    def test_the_signer_line_is_byte_exact(self):
        self.assertEqual((ROOT / ".github" / "allowed-signers" / "norma").read_bytes(), self.LINE)

    def test_the_lock_names_this_file_and_this_signer(self):
        project = json.loads((ROOT / ".claude" / "project.json").read_text(encoding="utf-8"))
        lock = json.loads((ROOT / ".claude" / "core.lock.json").read_text(encoding="utf-8"))
        self.assertEqual(project["lock"]["signers_file"], ".github/allowed-signers/norma")
        self.assertEqual(lock["signer"], self.FINGERPRINT)

    def test_no_stray_bootstrap_signers_file(self):
        """bootstrap.py writes .github/allowed_signers whatever lock.signers_file says (reported as D-011)."""
        self.assertFalse((ROOT / ".github" / "allowed_signers").exists())


if __name__ == "__main__":
    unittest.main()
