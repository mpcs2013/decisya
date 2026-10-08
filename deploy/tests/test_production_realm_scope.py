"""Where the production realm file may be named, and what the two RealmGuard-exempt stack files may hold
(issue #121, G2 D1 guard h, G3 G4-121-02, S-121-02; docs/security/threat-models/realm-guard-scope.md,
"Amendment at G3 for #121").

Two things are checked here, in Python, in the always-run `deploy-guards` job:

1. The production realm file name appears only in the stack tooling, in tests, in docs and in Markdown,
   and never under `src/Decisya.AppHost/**`, whatever the case. The dev AppHost can then never pick the
   production file up, and the file name keeps its own meaning next to the dev file's.
2. A Python twin of the C# RealmGuard rule for the two files it exempts by exact path. The C# test
   (`tests/Decisya.Identity.Tests`) is the authority; the twin makes a breach fail here too, in a job
   that runs on every pull request.

This file must not hold the two RealmGuard needles itself: it takes the import folder from
`stackguards.IMPORT_DIR`, and it never writes the dev realm's file name.
"""
from __future__ import annotations

import re
import unittest

from deploy_common import COMPOSE_DIR, OVERLAY, ROOT, guards
from test_no_home_addresses import repository_files

# The process-start words the amendment lists. `stackguards.py` must hold none of them, in code or comment.
LAUNCH_MARKERS = ("subprocess", "os.system", "os.exec", "os.spawn", "popen", "--import-realm", "start-dev")
# The dev realm's file name as a pattern: written with an escaped dot so this file does not hold the
# needle it looks for. It matches the name case-insensitively, like RealmGuard.
DEV_REALM_NAME = re.compile(r"decisya-realm\.json", re.IGNORECASE)
NAME = guards.REALM_FILE_NAME


class ScopeRuleTests(unittest.TestCase):
    """The pure rule, with red cases."""

    def problems(self, path, text=None):
        return guards.realm_scope_problems(path, "x = '%s'\n" % NAME if text is None else text)

    def test_the_allowed_places_pass(self):
        for path in (
            "deploy/compose/stackctl.py", "deploy/compose/stackguards.py", "deploy/compose/docker-compose.stack.yaml",
            "deploy/tests/test_x.py", "deploy/tests/sub/test_y.py", "tests/Decisya.Identity.Tests/X.cs",
            "docs/runbooks/deployable-stack.md", "docs/security/threat-models/production-identity.md",
            "README.md", "deploy/keycloak/production/common-passwords.SOURCE.md", ".claude/skills/runbook/SKILL.md",
        ):
            with self.subTest(path=path):
                self.assertEqual(self.problems(path), [])

    def test_a_file_that_does_not_name_it_never_matters(self):
        self.assertEqual(guards.realm_scope_problems("src/Decisya.AppHost/AppHost.cs", "nothing to see\n"), [])
        self.assertEqual(guards.realm_scope_problems(".github/workflows/ci.yml", "name: ci\n"), [])

    def test_every_other_place_is_refused(self):
        for path in (
            ".github/workflows/ci.yml", ".github/scripts/image_scan.py", ".claude/hooks/x.py", ".claude/settings.json",
            ".vscode/tasks.json", "deploy/compose/docker-compose.yaml", "deploy/compose/other.py", "deploy/keycloak/entrypoint-stack.sh",
            "deploy/postgres/init/10-keycloak-db.sh", "Dockerfile", "compose.yaml", "Directory.Build.props",
            "src/Decisya.Api/Program.cs", "src/Decisya.Web/package.json", "deploy/testsx/y.py", "docs.old/y.txt", "tests-x/y.cs",
            "Deploy/compose/stackctl.py", "deploy/compose/sub/stackctl.py",
        ):
            with self.subTest(path=path):
                problems = self.problems(path)
                self.assertEqual(len(problems), 1, problems)
                self.assertIn("outside the places", problems[0])

    def test_the_dev_apphost_is_refused_even_for_markdown_and_whatever_the_case(self):
        for path in (
            "src/Decisya.AppHost/AppHost.cs", "src/Decisya.AppHost/Deploy/ComposeStack.cs", "src/Decisya.AppHost/README.md",
            "SRC/DECISYA.APPHOST/x.cs", "src/decisya.apphost/x.cs", "src\\Decisya.AppHost\\x.cs",
        ):
            with self.subTest(path=path):
                problems = self.problems(path)
                self.assertEqual(len(problems), 1, problems)
                self.assertIn("AppHost", problems[0])

    def test_the_name_is_matched_whatever_its_case(self):
        for text in (NAME.upper(), NAME.title(), "A" + NAME.upper() + "B"):
            with self.subTest(text=text):
                self.assertEqual(len(guards.realm_scope_problems(".github/workflows/ci.yml", text)), 1)
                self.assertEqual(len(guards.realm_scope_problems("src/Decisya.AppHost/AppHost.cs", text)), 1)

    def test_backslash_paths_are_normalised(self):
        self.assertEqual(guards.realm_scope_problems("deploy\\tests\\test_x.py", NAME), [])
        self.assertEqual(len(guards.realm_scope_problems("deploy\\keycloak\\x.sh", NAME)), 1)

    def test_a_report_names_the_path_not_the_content(self):
        text = "secret = 'CANARY-NOT-A-REAL-SECRET'  # " + NAME
        problems = guards.realm_scope_problems(".github/workflows/ci.yml", text)
        self.assertEqual(len(problems), 1)
        self.assertNotIn("CANARY", problems[0])


class RepositoryScopeTests(unittest.TestCase):
    def test_the_repository_names_the_production_realm_only_where_the_note_allows(self):
        found = []
        checked = 0
        for path in repository_files():
            raw = path.read_bytes()
            if b"\0" in raw[:8000]:
                continue
            checked += 1
            found += guards.realm_scope_problems(path.relative_to(ROOT).as_posix(), raw.decode("utf-8", "replace"))
        self.assertGreater(checked, 100, "the scan must really read the repository")
        self.assertEqual(found, [])

    def test_the_dev_apphost_sources_never_name_the_file(self):
        folder = ROOT / "src" / "Decisya.AppHost"
        files = [p for p in folder.rglob("*") if p.is_file() and "obj" not in p.parts and "bin" not in p.parts]
        self.assertGreater(len(files), 3)
        for path in files:
            if path.suffix.lower() in {".dll", ".pdb", ".png", ".ico"}:
                continue
            text = path.read_bytes().decode("utf-8", "replace")
            self.assertNotIn(NAME, text.lower(), path.relative_to(ROOT).as_posix())


class StackFilesStayExemptTests(unittest.TestCase):
    """The Python twin of the RealmGuard rule for the overlay and `stackguards.py` (G4-121-02)."""

    def test_stackguards_holds_no_process_start_marker_and_no_dev_realm_name(self):
        text = (COMPOSE_DIR / "stackguards.py").read_text(encoding="utf-8").lower()
        for marker in LAUNCH_MARKERS:
            with self.subTest(marker=marker):
                self.assertNotIn(marker, text)
        self.assertIsNone(DEV_REALM_NAME.search(text))

    def test_the_overlay_names_the_import_folder_only_as_the_exact_production_file(self):
        text = OVERLAY.read_text(encoding="utf-8")
        self.assertIsNone(DEV_REALM_NAME.search(text))
        self.assertEqual(self.occurrences_problems(text), [])
        self.assertEqual(len(re.findall(re.escape(guards.IMPORT_DIR), text, re.IGNORECASE)), 1)

    def test_nothing_else_in_the_stack_tooling_or_its_tests_names_the_import_folder_or_the_dev_realm(self):
        # These files are not exempt from RealmGuard: they take the folder from stackguards.IMPORT_DIR.
        folder = re.compile(re.escape(guards.IMPORT_DIR), re.IGNORECASE)
        files = [COMPOSE_DIR / "stackctl.py"] + sorted((ROOT / "deploy" / "tests").glob("*.py"))
        self.assertGreater(len(files), 8)
        for path in files:
            text = path.read_text(encoding="utf-8")
            with self.subTest(file=path.name):
                self.assertIsNone(folder.search(text))
                self.assertIsNone(DEV_REALM_NAME.search(text))

    # ----- the overlay rule itself, with red cases

    @staticmethod
    def occurrences_problems(text):
        """Every occurrence of the import folder is the exact production file target, then a character
        that cannot continue a path (end, quote, whitespace, colon)."""
        problems = []
        target_tail = "/" + guards.REALM_FILE_NAME
        for match in re.finditer(re.escape(guards.IMPORT_DIR), text, re.IGNORECASE):
            rest = text[match.end():]
            if not rest.startswith(target_tail):
                problems.append("not the production file")
            elif len(rest) > len(target_tail) and rest[len(target_tail)] not in "\n\r\"' \t:":
                problems.append("continues the path")
        return problems

    def test_the_overlay_rule_can_fail(self):
        folder, tail = guards.IMPORT_DIR, "/" + guards.REALM_FILE_NAME
        good = "target: " + folder + tail + "\n"
        self.assertEqual(self.occurrences_problems(good), [])
        for label, text in (
            ("the bare folder", "target: " + folder + "\n"),
            ("the folder with a slash", "target: " + folder + "/\n"),
            ("another file", "target: " + folder + "/other.json\n"),
            ("a longer name", "target: " + folder + tail + ".d/x\n"),
            ("a case variant of the folder", "TARGET: " + folder.upper() + "\n"),
            ("one good, one bare", good + "target: " + folder + "\n"),
        ):
            with self.subTest(case=label):
                self.assertNotEqual(self.occurrences_problems(text), [])


if __name__ == "__main__":
    unittest.main()
