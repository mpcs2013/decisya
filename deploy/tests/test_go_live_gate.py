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
# #122 (G2 D8): C-09 is split into C-09a (the BFF limiter, closed by #122) and C-09b (the edge limit, open).
ITEMS = ("C-02", "C-03", "C-03b", "C-05", "C-09a", "C-09b", "C-10", "C-20")
# G1 D1 item 1, word for word.
C02_SENTENCE = "No account that is not synthetic exists until MFA for tenant users and the breached-password check are on."

# #122 statements the gate must keep (G2 D8, G3 S-122-03 and G4-122-04 b). Each is a phrase of the text, checked
# in the section it belongs to, so a reworded or moved sentence fails here.
SECTION_PHRASES = {
    "C-05": (
        ("closes after #122 and the #30 restore drill", "#122 has merged", "#30 restore drill is recorded"),
        ("the Hyper Backup source is `dumps` alone", "The Hyper Backup source is the `dumps` folder alone"),
        ("secrets/ is never inside the backup", "`secrets/` is never inside `dumps`"),
        ("stack-folder snapshots never leave the box", "stack-folder snapshots never leave the box"),
    ),
    "C-09a": (
        ("closed by #122 on merge", "Closed when** (by #122 on merge)"),
        ("C-09a does not stop password guessing", "it does not stop password guessing"),
    ),
    "C-09b": (
        ("open and blocks public exposure", "open and **blocks public-internet exposure**"),
        ("covers the id host", "**and the id host**"),
        ("needs the ADR-0016 R4 amendment", "ADR-0016 R4"),
    ),
}


def sections(text: str) -> dict:
    """`### C-xx: title` sections of the 'How each item is closed' part, by item id."""
    found = {}
    parts = re.split(r"(?m)^### (C-\d+[ab]?)\b", text)
    for index in range(1, len(parts) - 1, 2):
        # A section ends at the next heading of any level.
        body = re.split(r"(?m)^#{1,3} ", parts[index + 1], maxsplit=1)[0]
        found[parts[index]] = body
    return found


def table_problems(text: str) -> list:
    """The blocking table is the one place a status sits: C-09a closed by #122, C-09b open, blocking and
    covering the id host, C-05 owned by #122 with #30 for the backup."""
    if "## The blocking list" not in text or "## How each item is closed" not in text:
        return ["the blocking list or the closing section is missing"]
    table = text.split("## The blocking list", 1)[1].split("## How each item is closed", 1)[0]
    rows = {m.group(1): m.group(0) for m in re.finditer(r"(?m)^\|\s*(C-\d+[ab]?)\s*\|.*$", table)}
    problems = []
    if "Closed by #122" not in rows.get("C-09a", ""):
        problems.append("C-09a is not closed by #122 in the table")
    row = rows.get("C-09b", "")
    if "Open" not in row or "blocks public-internet exposure" not in row or "id host" not in row:
        problems.append("C-09b is not open and blocking public exposure for the id host in the table")
    row = rows.get("C-05", "")
    if "#122" not in row or "#30" not in row:
        problems.append("C-05 does not name #122 and #30 in the table")
    return problems


def gate_problems(text: str) -> list:
    problems = table_problems(text)
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
    for item, rows in SECTION_PHRASES.items():
        for label, *phrases in rows:
            if not all(phrase in found.get(item, "") for phrase in phrases):
                problems.append("%s does not say that %s" % (item, label))
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
        ids = re.findall(r"(?m)^\|\s*(C-\d+[ab]?)\s*\|", table)
        self.assertEqual(ids, list(ITEMS))

    def test_the_table_closes_c09a_and_keeps_c09b_open_and_blocking(self):
        table = self.text.split("## The blocking list", 1)[1].split("## How each item is closed", 1)[0]
        rows = {m.group(1): m.group(0) for m in re.finditer(r"(?m)^\|\s*(C-\d+[ab]?)\s*\|.*$", table)}
        self.assertIn("Closed by #122", rows["C-09a"])
        self.assertIn("Open", rows["C-09b"])
        self.assertIn("blocks public-internet exposure", rows["C-09b"])
        self.assertIn("id host", rows["C-09b"])
        self.assertIn("#30", rows["C-05"])
        self.assertIn("#122", rows["C-05"])
        self.assertNotIn("C-09 ", table.replace("C-09a", "").replace("C-09b", ""))

    def test_the_old_c09_item_is_gone(self):
        self.assertNotRegex(self.text, r"(?m)^###? C-09\b(?![ab])")
        self.assertNotRegex(self.text, r"(?m)^\|\s*C-09\s*\|")

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
        mutated = self.text.replace("### C-09a: rate limiting in the BFF\n\n**Closed when**", "### C-09a: rate limiting in the BFF\n\n**Done**")
        self.assertNotEqual(mutated, self.text)
        self.assert_flagged("C-09a", mutated, "C-09a says nothing about when it is closed")

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

    def test_each_122_statement_can_fail(self):
        for item, rows in SECTION_PHRASES.items():
            for label, *phrases in rows:
                with self.subTest(item=item, statement=label):
                    mutated = self.text.replace(phrases[0], "x")
                    self.assertNotEqual(mutated, self.text)
                    self.assert_flagged(label, mutated, "%s does not say that %s" % (item, label))

    def test_a_reopened_c09a_or_a_closed_c09b_is_flagged(self):
        # The table is the one place the status sits; a test of the sections alone would not notice a flip.
        for old, new, fragment in (
            ("| Closed by #122 on merge |\n| C-09b", "| Open |\n| C-09b", "C-09a is not closed by #122"),
            ("| Open: blocks public-internet exposure |", "| Closed by #29 |", "C-09b is not open and blocking"),
            ("the app host and the id host |", "the app host |", "C-09b is not open and blocking"),
        ):
            with self.subTest(fragment=fragment):
                self.assertIn(old, self.text)
                self.assert_flagged(fragment, self.text.replace(old, new), fragment)

    def test_a_credential_looking_line_is_flagged(self):
        self.assert_flagged("credential", self.text + "\nKC_DB_PASSWORD=abc123\n", "carries a credential")


if __name__ == "__main__":
    unittest.main()
