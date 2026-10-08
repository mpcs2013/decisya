"""The go-live gate file exists and says what it must (issue #121, G2 D5, NFR-48).

`docs/runbooks/go-live-gate.md` is the one blocking list for the first real account and the first real
data. It names the controls that block, how each is closed, and where to switch the C-02 controls on. A
repository test cannot see the Done-when lines of #132 and #29 (they are GitHub issue text); it checks
that this file names both issues, and Marco checks the pointers on GitHub.

The check is a function over the text, so it has red cases: a gate file that loses an item, a sentence
or a pointer fails here, in the always-run `deploy-guards` job, docs-only pull requests included.
"""
from __future__ import annotations

import re
import unittest

from deploy_common import ROOT

GATE = ROOT / "docs" / "runbooks" / "go-live-gate.md"
ITEMS = ("C-02", "C-03", "C-03b", "C-05", "C-09", "C-10", "C-20")
# G1 D1 item 1, word for word.
C02_SENTENCE = "No account that is not synthetic exists until MFA for tenant users and the breached-password check are on."


def sections(text: str) -> dict:
    """`### C-xx: title` sections of the 'How each item is closed' part, by item id."""
    found = {}
    parts = re.split(r"(?m)^### (C-\d+b?)\b", text)
    for index in range(1, len(parts) - 1, 2):
        # A section ends at the next heading of any level.
        body = re.split(r"(?m)^#{1,3} ", parts[index + 1], maxsplit=1)[0]
        found[parts[index]] = body
    return found


def gate_problems(text: str) -> list:
    problems = []
    found = sections(text)
    for item in ITEMS:
        if item not in found:
            problems.append("no section for %s" % item)
        elif "**Closed when" not in found[item]:
            problems.append("%s says nothing about when it is closed" % item)
        elif len(found[item].strip()) < 150:
            problems.append("%s is a stub" % item)
    for issue in ("#132", "#29"):
        if issue not in text:
            problems.append("the file does not name %s" % issue)
    if C02_SENTENCE not in text:
        problems.append("the C-02 sentence is missing or reworded")
    if C02_SENTENCE not in found.get("C-02", ""):
        problems.append("the C-02 section does not carry the C-02 sentence")
    for needle, why in (
        ("level-2-tenant", "the tenant-MFA switch"),
        ("breached-passwords.txt", "the breached-password list"),
        ("stackctl.py verify", "the check that trips on a real account"),
        ("realm rebuild", "the Phase 0 stop-gap that C-03b retires"),
        ("kcadm.sh", "the CLI route for the switch"),
        ("synthetic=true", "the convention the check reads"),
    ):
        if needle not in text:
            problems.append("the file does not mention %s (%s)" % (needle, why))
    if not re.search(r"(?m)^\|\s*#\s*\|\s*Visual Studio 2026 / VS Code\s*\|\s*CLI\s*\|", text):
        problems.append("the switch-on steps are not a Visual Studio 2026 / VS Code | CLI table")
    for line in text.splitlines():
        if re.search(r"(?:password|secret|token)\s*=\s*\S", line, re.IGNORECASE) and "passwordPolicy" not in line:
            problems.append("a line looks like it carries a credential")
            break
    return problems


class GoLiveGateFileTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(GATE.is_file(), "docs/runbooks/go-live-gate.md is missing")
        self.text = GATE.read_text(encoding="utf-8")

    def test_the_committed_file_passes(self):
        self.assertEqual(gate_problems(self.text), [])

    def test_the_blocking_table_lists_every_item_once(self):
        table = self.text.split("## The blocking list", 1)[1].split("## How each item is closed", 1)[0]
        ids = re.findall(r"(?m)^\|\s*(C-\d+b?)\s*\|", table)
        self.assertEqual(ids, list(ITEMS))

    def test_the_c02_sentence_is_in_the_rule_and_in_its_section(self):
        self.assertGreaterEqual(self.text.count(C02_SENTENCE), 2)

    def test_the_file_holds_no_home_address_or_secret_value(self):
        # The address scan covers the whole repository; this adds the gate file's own promise.
        self.assertNotRegex(self.text, r"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b")

    def test_the_commands_in_the_file_are_marked_unverified_until_run(self):
        self.assertIn("(unverified)", self.text)


class GateCheckCanFailTests(unittest.TestCase):
    def setUp(self):
        self.text = GATE.read_text(encoding="utf-8")

    def assert_flagged(self, label, mutated, fragment):
        problems = gate_problems(mutated)
        self.assertTrue(any(fragment in p for p in problems), "%s: expected %r in %s" % (label, fragment, problems))

    def test_a_lost_item_is_flagged(self):
        for item in ITEMS:
            with self.subTest(item=item):
                mutated = re.sub(r"(?m)^### %s\b" % re.escape(item), "### X-00", self.text)
                self.assert_flagged(item, mutated, "no section for %s" % item)

    def test_an_item_without_a_closing_condition_is_flagged(self):
        mutated = self.text.replace("### C-09: rate limiting\n\n**Closed when**", "### C-09: rate limiting\n\n**Done**")
        self.assertNotEqual(mutated, self.text)
        self.assert_flagged("C-09", mutated, "C-09 says nothing about when it is closed")

    def test_a_stub_is_flagged(self):
        mutated = re.sub(r"(?s)(### C-05: [^\n]*\n)(.*?)(?=\n## |\n### )", r"\1\n**Closed when:** later.\n", self.text)
        self.assertNotEqual(mutated, self.text)
        self.assert_flagged("C-05", mutated, "C-05 is a stub")

    def test_a_missing_pointer_is_flagged(self):
        self.assert_flagged("#132", self.text.replace("#132", "the NAS issue"), "does not name #132")
        self.assert_flagged("#29", self.text.replace("#29", "the deployment issue"), "does not name #29")

    def test_a_reworded_sentence_is_flagged(self):
        reworded = self.text.replace(C02_SENTENCE, C02_SENTENCE.replace("synthetic", "test"))
        self.assert_flagged("sentence", reworded, "C-02 sentence is missing or reworded")

    def test_a_missing_switch_or_check_is_flagged(self):
        for needle in ("level-2-tenant", "breached-passwords.txt", "stackctl.py verify", "realm rebuild", "kcadm.sh", "synthetic=true"):
            with self.subTest(needle=needle):
                self.assert_flagged(needle, self.text.replace(needle, "x"), "does not mention %s" % needle)

    def test_the_two_column_table_is_required(self):
        mutated = self.text.replace("| # | Visual Studio 2026 / VS Code | CLI |", "| # | Steps |")
        self.assert_flagged("table", mutated, "Visual Studio 2026 / VS Code | CLI table")

    def test_a_credential_looking_line_is_flagged(self):
        self.assert_flagged("credential", self.text + "\nKC_DB_PASSWORD=abc123\n", "carries a credential")


if __name__ == "__main__":
    unittest.main()
