"""settings.json through the Norma lock keeps every entry Decisya had (#246, G3 M3, ADR-0023).

Norma's `bootstrap --adopt` rewrites .claude/settings.json: it may only append to `permissions.deny` and register
the two core hooks. The fixture pins the allow, ask and deny lists as they were before the lock, so a reading by
eye is never the only check. The two hook registrations must be exactly the consumer guide's exec form.
"""
import json
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

    def test_the_core_hooks_are_registered_exactly(self):
        self.assertEqual(self.settings["hooks"]["PreToolUse"], CORE_HOOKS)

    def test_no_other_top_level_key_appears(self):
        self.assertLessEqual(set(self.settings), {"permissions", "hooks"})


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
