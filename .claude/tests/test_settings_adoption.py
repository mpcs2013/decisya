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


if __name__ == "__main__":
    unittest.main()
