"""The production realm's password list is exactly the file Marco approved (issue #121, G3 T121-14,
`deploy/keycloak/production/common-passwords.SOURCE.md`).

Keycloak reads every line of the list itself, so the file carries no provenance comment: the record is
`common-passwords.SOURCE.md`, and this test ties the two together. Any change to the list changes its
SHA-256, fails here, and needs a new approval and a new record (third-party data, Marco 2026-10-07).

Also checked: the Git attributes keep the list, the realm, the identity check and the wrapper at LF on
a machine with `core.autocrlf=true` (a carriage return would become part of every password). The
general rule `* text=auto eol=lf` of .gitattributes already does that, so the test pins that nothing
overrides it.
"""
from __future__ import annotations

import fnmatch
import hashlib
import json
import re
import unittest

from deploy_common import ROOT

PRODUCTION = ROOT / "deploy" / "keycloak" / "production"
LIST = PRODUCTION / "common-passwords.txt"
SOURCE = PRODUCTION / "common-passwords.SOURCE.md"
REALM = PRODUCTION / "realm-decisya.json"

# Approved by Marco on 2026-10-07; copied from the manifest, not computed from the file.
APPROVED_SHA256 = "ed0469a2ad1fd4afa94b53d0878eaaa80018a843d6de3fbfaa94a7c68740c976"
APPROVED_LINES = 1195
APPROVED_BYTES = 17894


def lines_of(raw: bytes) -> list:
    return raw.decode("utf-8").split("\n")[:-1]


def format_problems(raw: bytes) -> list:
    """The format rules the build steps promise (SOURCE.md, "Build steps")."""
    problems = []
    if b"\r" in raw:
        problems.append("carriage return")
    if not raw.endswith(b"\n") or raw.endswith(b"\n\n"):
        problems.append("must end with exactly one newline")
    try:
        lines = lines_of(raw)
    except UnicodeDecodeError:
        return problems + ["not UTF-8"]
    if raw.startswith(b"\xef\xbb\xbf"):
        problems.append("byte order mark")
    if any(line == "" for line in lines):
        problems.append("empty line")
    if any(line != line.strip() for line in lines):
        problems.append("surrounding whitespace")
    if any(line != line.lower() for line in lines):
        problems.append("not lowercase")
    if any(not 12 <= len(line) <= 128 for line in lines):
        problems.append("length outside 12 to 128 characters")
    if len(set(lines)) != len(lines):
        problems.append("duplicate line")
    if lines != sorted(lines):
        problems.append("not sorted")
    return problems


class PasswordListTests(unittest.TestCase):
    def setUp(self):
        self.raw = LIST.read_bytes()
        self.record = SOURCE.read_text(encoding="utf-8")

    def test_the_file_is_the_approved_one(self):
        self.assertEqual(hashlib.sha256(self.raw).hexdigest(), APPROVED_SHA256,
                         "common-passwords.txt changed: a new approval and a new SOURCE.md record are needed")
        self.assertEqual(len(self.raw), APPROVED_BYTES)
        self.assertEqual(len(lines_of(self.raw)), APPROVED_LINES)

    def test_the_source_record_states_the_same_hash_size_and_line_count(self):
        # The "Result" table: `| SHA-256 | `...` |`. The "Upstream SHA-256" row starts with another word.
        hashes = re.findall(r"^\|\s*SHA-256\s*\|\s*`([0-9a-f]{64})`\s*\|", self.record, re.MULTILINE)
        self.assertEqual(hashes, [APPROVED_SHA256])
        lines = re.findall(r"^\|\s*Lines\s*\|\s*([0-9,]+)", self.record, re.MULTILINE)
        size = re.findall(r"^\|\s*Bytes\s*\|\s*([0-9,]+)", self.record, re.MULTILINE)
        self.assertEqual([int(v.replace(",", "")) for v in lines], [APPROVED_LINES])
        self.assertEqual([int(v.replace(",", "")) for v in size], [APPROVED_BYTES])

    def test_the_record_keeps_the_licence_and_the_pinned_upstream(self):
        for needle in ("MIT License", "Copyright (c) 2018 Daniel Miessler", "1a7bb9127eca9e6ff2fc0301c597fe6e16a0cb56",
                       "100k-most-used-passwords-NCSC.txt", "Approved by Marco"):
            self.assertIn(needle, self.record)

    def test_the_format_rules_hold(self):
        self.assertEqual(format_problems(self.raw), [])

    def test_the_realm_policy_names_this_file(self):
        realm = json.loads(REALM.read_text(encoding="utf-8"))
        policy = realm["passwordPolicy"]
        self.assertIn("passwordBlacklist(%s)" % LIST.name, policy.split(" and "))

    def test_the_format_check_can_fail(self):
        good = b"aaaaaaaaaaaa\nbbbbbbbbbbbb\n"
        self.assertEqual(format_problems(good), [])
        cases = {
            "carriage return": b"aaaaaaaaaaaa\r\nbbbbbbbbbbbb\r\n",
            "must end with exactly one newline": b"aaaaaaaaaaaa\nbbbbbbbbbbbb",
            "empty line": b"aaaaaaaaaaaa\n\nbbbbbbbbbbbb\n",
            "surrounding whitespace": b"aaaaaaaaaaaa \nbbbbbbbbbbbb\n",
            "not lowercase": b"Aaaaaaaaaaaa\nbbbbbbbbbbbb\n",
            "length outside": b"short\nbbbbbbbbbbbb\n",
            "duplicate line": b"aaaaaaaaaaaa\naaaaaaaaaaaa\n",
            "not sorted": b"bbbbbbbbbbbb\naaaaaaaaaaaa\n",
            "byte order mark": b"\xef\xbb\xbfaaaaaaaaaaaa\nbbbbbbbbbbbb\n",
            "not UTF-8": b"\xff\xfeaaaaaaaaaaaa\nbbbbbbbbbbbb\n",
        }
        for fragment, raw in cases.items():
            with self.subTest(case=fragment):
                problems = format_problems(raw)
                self.assertTrue(any(fragment in p for p in problems), problems)
        self.assertTrue(any("length outside" in p for p in format_problems(b"a" * 129 + b"\nbbbbbbbbbbbb\n")))

    def test_the_hash_check_can_fail(self):
        self.assertNotEqual(hashlib.sha256(self.raw + b"x\n").hexdigest(), APPROVED_SHA256)
        self.assertNotEqual(hashlib.sha256(self.raw.replace(b"\n", b"\r\n")).hexdigest(), APPROVED_SHA256)


def effective_eol(rules: list, path: str):
    """The `eol` Git applies to `path`: the last matching rule that sets it wins. `binary`, `-text` and
    `-eol` switch conversion off. A pattern with no slash matches the file name at any depth."""
    eol = None
    for pattern, attributes in rules:
        name = path.rsplit("/", 1)[-1]
        if not (fnmatch.fnmatchcase(path, pattern) if "/" in pattern else fnmatch.fnmatchcase(name, pattern)):
            continue
        for attribute in attributes:
            if attribute.startswith("eol="):
                eol = attribute[4:]
            elif attribute in ("binary", "-text", "-eol"):
                eol = None
            elif attribute == "text=auto" or attribute == "text":
                pass
    return eol


def read_rules(text: str) -> list:
    rules = []
    for line in text.splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            pattern, _, attributes = line.partition(" ")
            rules.append((pattern, attributes.split()))
    return rules


class GitAttributesTests(unittest.TestCase):
    """The general rule `* text=auto eol=lf` already covers these files; this pins that it keeps doing so."""

    FILES = (
        "deploy/keycloak/production/common-passwords.txt",
        "deploy/keycloak/production/realm-decisya.json",
        "deploy/keycloak/production/identity-check.sql",
        "deploy/keycloak/entrypoint-stack.sh",
    )

    def setUp(self):
        self.rules = read_rules((ROOT / ".gitattributes").read_text(encoding="utf-8"))

    def test_every_file_the_stack_mounts_or_hashes_is_converted_to_lf(self):
        for path in self.FILES:
            with self.subTest(path=path):
                self.assertTrue((ROOT / path).is_file())
                self.assertEqual(effective_eol(self.rules, path), "lf")

    def test_the_files_really_are_lf_in_this_working_tree(self):
        for path in self.FILES:
            with self.subTest(path=path):
                self.assertNotIn(b"\r", (ROOT / path).read_bytes())

    def test_the_check_can_fail(self):
        base = read_rules("* text=auto eol=lf\n*.png binary\n")
        self.assertEqual(effective_eol(base, "deploy/keycloak/production/common-passwords.txt"), "lf")
        self.assertIsNone(effective_eol(base, "a/b.png"))
        for extra in (
            "deploy/keycloak/production/*.txt eol=crlf\n",
            "*.txt -text\n",
            "common-passwords.txt binary\n",
            "deploy/keycloak/production/common-passwords.txt -eol\n",
        ):
            with self.subTest(extra=extra.strip()):
                rules = base + read_rules(extra)
                self.assertNotEqual(effective_eol(rules, "deploy/keycloak/production/common-passwords.txt"), "lf")
        self.assertEqual(effective_eol([], "x.txt"), None)


if __name__ == "__main__":
    unittest.main()
