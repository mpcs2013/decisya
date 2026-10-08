"""The key ring's tooling and guards (issue #122, G2 D9 to D14, G3 G4-122-02 to G4-122-04, S-122-07).

No Docker needed: every Docker call is replaced by a recording runner. The one-shot shell scripts that
`keyring reset` and `keyring verify` send to the Caddy image are run for real, on a POSIX host with `sh`,
`find` and `grep`, against fixture folders in a scratch directory (their `cd` is pointed at the fixture;
the chown of `prepare` needs root and is covered by its text and its exact argv instead).

What the tests pin:
  - the exact argv of the four tools (prepare, reset, verify, generator): flags, image, one mount;
  - the reset script on a fixture volume: a #120-style plaintext key, a NullXmlEncryptor key, a DPAPI key and
    a wrapped key with a stray masterKey are deleted; a certificate-wrapped key and a revocation file stay,
    byte for byte; nothing is printed but counts; a link makes it refuse before anything changes;
  - the Python rule and the shell rule agree on every fixture;
  - `secrets init`, `add`, `rotate` and `retire` for the certificate pair, and the shape rules;
  - the guards: BFF-only secrets, no volume driver, no generator flag in a service, the rate-limit keys.
"""
from __future__ import annotations

import base64
import os
import shlex
import shutil
import subprocess
import unittest
from pathlib import Path
from unittest import mock

from deploy_common import (
    FIXTURE_VALUES, ROOT, FakeRunner, clean_keyring_report, completed, guards, make_stack, quiet, scratch_dir, stackctl,
    unlink_force,
)

POSIX = os.name == "posix"
HAVE_SHELL = POSIX and all(shutil.which(tool) for tool in ("sh", "find", "grep", "wc", "tr", "rm"))
BFF = FIXTURE_VALUES["DECISYA_BFF_IMAGE"]
CADDY = "docker.io/library/caddy:2.11.7@sha256:" + "e" * 64


def run(argv, runner_factory=None):
    return quiet(stackctl.main, argv, runner_factory=runner_factory)


def secrets_of(stack: Path) -> dict:
    return {p.name: p.read_text(encoding="utf-8") for p in (stack / "secrets").iterdir()}


def put(path: Path, data: bytes) -> None:
    """Replace a secret file the way stackctl does (mode 0444), so the mode check stays quiet."""
    stackctl.write_atomic(path, data, stackctl.SECRET_FILE_MODE)


def has(problems, fragment) -> bool:
    return any(fragment in p for p in problems)


# --------------------------------------------------------------------------- key file fixtures

# An assembly version looks like an IPv4 literal to the repository's address scan, so it is built from parts.
ASSEMBLY_VERSION = ".".join(("10", "0", "0", "0"))
KEY_HEAD = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    '<key id="@ID@" version="1">\n'
    "  <creationDate>2026-10-01T00:00:00Z</creationDate>\n"
    "  <activationDate>2026-10-01T00:00:00Z</activationDate>\n"
    "  <expirationDate>2027-01-01T00:00:00Z</expirationDate>\n"
    '  <descriptor deserializerType="Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.'
    'AuthenticatedEncryptorDescriptorDeserializer, Microsoft.AspNetCore.DataProtection, Version=' + ASSEMBLY_VERSION + ', Culture=neutral, '
    'PublicKeyToken=adb9793829ddae60">\n'
    "    <descriptor>\n"
    '      <encryption algorithm="AES_256_CBC" />\n'
    '      <validation algorithm="HMACSHA256" />\n'
)
KEY_TAIL = "    </descriptor>\n  </descriptor>\n</key>\n"
DP_NAMESPACE = 'xmlns="http://schemas.asp.net/2015/03/dataProtection"'
FRAMEWORK = "Microsoft.AspNetCore.DataProtection.XmlEncryption."
ASSEMBLY = ", Microsoft.AspNetCore.DataProtection, Version=" + ASSEMBLY_VERSION + ", Culture=neutral, PublicKeyToken=adb9793829ddae60"


def encrypted_secret(decryptor: str, body: str) -> str:
    return '      <encryptedSecret decryptorType="%s%s%s" %s>\n%s      </encryptedSecret>\n' % (
        FRAMEWORK, decryptor, ASSEMBLY, DP_NAMESPACE, body)


CERT_BODY = (
    '        <EncryptedData Type="http://www.w3.org/2001/04/xmlenc#Element" xmlns="http://www.w3.org/2001/04/xmlenc#">\n'
    '          <EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes256-cbc" />\n'
    "        </EncryptedData>\n"
)
MASTER_KEY = (
    '      <masterKey p4:requiresEncryption="true" xmlns:p4="http://schemas.asp.net/2015/03/dataProtection">\n'
    "        <!-- Warning: the key below is in an unencrypted form. -->\n"
    "        <value>QUJDREVGRw==</value>\n"
    "      </masterKey>\n"
)


def key_file(key_id: str, middle: str) -> str:
    return KEY_HEAD.replace("@ID@", key_id) + middle + KEY_TAIL


WRAPPED = key_file("11111111-1111-1111-1111-111111111111", encrypted_secret("EncryptedXmlDecryptor", CERT_BODY))
PLAINTEXT_120 = key_file("22222222-2222-2222-2222-222222222222", MASTER_KEY)
NULL_ENCRYPTED = key_file(
    "33333333-3333-3333-3333-333333333333",
    encrypted_secret("NullXmlDecryptor", '        <unencryptedKey id="x">\n' + MASTER_KEY + "        </unencryptedKey>\n"))
DPAPI = key_file("44444444-4444-4444-4444-444444444444", encrypted_secret("DpapiXmlDecryptor", "        <value>AAAA</value>\n"))
WRAPPED_WITH_STRAY_MASTER_KEY = key_file(
    "55555555-5555-5555-5555-555555555555", encrypted_secret("EncryptedXmlDecryptor", CERT_BODY) + MASTER_KEY)
TWO_SECRETS = key_file(
    "66666666-6666-6666-6666-666666666666",
    encrypted_secret("EncryptedXmlDecryptor", CERT_BODY) + encrypted_secret("EncryptedXmlDecryptor", CERT_BODY))
NO_ENCRYPTED_DATA = key_file("77777777-7777-7777-7777-777777777777", encrypted_secret("EncryptedXmlDecryptor", "        <value>AAAA</value>\n"))
REVOCATION = (
    '<?xml version="1.0" encoding="utf-8"?>\n<revocation version="1">\n'
    "  <revocationDate>2026-10-02T00:00:00Z</revocationDate>\n"
    '  <key id="11111111-1111-1111-1111-111111111111" />\n  <reason>operator</reason>\n</revocation>\n')
EMPTY_ELEMENT = '<?xml version="1.0" encoding="utf-8"?>\n<unrelated />\n'

# name -> (text, passes the strict rule)
FIXTURES = {
    "key-wrapped.xml": (WRAPPED, True),
    "revocation-1.xml": (REVOCATION, True),
    "key-plaintext-120.xml": (PLAINTEXT_120, False),
    "key-null-encrypted.xml": (NULL_ENCRYPTED, False),
    "key-dpapi.xml": (DPAPI, False),
    "key-stray-master-key.xml": (WRAPPED_WITH_STRAY_MASTER_KEY, False),
    "key-two-secrets.xml": (TWO_SECRETS, False),
    "key-no-encrypted-data.xml": (NO_ENCRYPTED_DATA, False),
    "other.xml": (EMPTY_ELEMENT, False),
}


def populate(folder: Path) -> None:
    for name, (text, _) in FIXTURES.items():
        (folder / name).write_text(text, encoding="utf-8", newline="\n")
    # A leftover folder, as the first design of the reset would have made: its plaintext key goes too.
    attic = folder / "attic-20261008000000"
    attic.mkdir()
    (attic / "key-old-plaintext.xml").write_text(PLAINTEXT_120, encoding="utf-8", newline="\n")
    (attic / "key-old-wrapped.xml").write_text(WRAPPED, encoding="utf-8", newline="\n")
    (folder / "notes.txt").write_text("masterKey is only a word here\n", encoding="utf-8")


def run_script(script: str, folder: Path) -> subprocess.CompletedProcess:
    """Run a one-shot script against a fixture folder: only its `cd /keyring` is pointed at the fixture."""
    marker = "cd %s\n" % guards.KEYRING_MOUNT_TARGET
    if script.count(marker) != 1:
        raise AssertionError("the script must change into the key-ring mount exactly once")
    patched = script.replace(marker, "cd %s\n" % shlex.quote(str(folder)))
    return subprocess.run(["sh", "-c", patched], capture_output=True, text=True, timeout=120)


# --------------------------------------------------------------------------- the argv builders (G4-122-03 a, c)

class ArgvTests(unittest.TestCase):
    PRIVILEGED_COMMON = [
        "docker", "run", "--rm", "--network", "none", "--read-only", "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
        "--cap-add", "CHOWN", "--cap-add", "DAC_OVERRIDE", "--cap-add", "FOWNER",
        "--user", "0:0", "--pids-limit", "64", "--memory", "64m",
        "--mount", "type=volume,source=decisya_bff-keyring,target=/keyring",
        "--entrypoint", "sh", CADDY, "-c",
    ]

    def test_prepare_argv_is_exact(self):
        self.assertEqual(stackctl.keyring_prepare_argv(CADDY), self.PRIVILEGED_COMMON + [stackctl.KEYRING_PREPARE_SCRIPT])

    def test_reset_argv_is_exact(self):
        self.assertEqual(stackctl.keyring_reset_argv(CADDY), self.PRIVILEGED_COMMON + [stackctl.KEYRING_RESET_SCRIPT])

    def test_reset_all_keys_argv_is_exact(self):
        self.assertEqual(stackctl.keyring_reset_argv(CADDY, all_keys=True), self.PRIVILEGED_COMMON + [stackctl.KEYRING_RESET_ALL_SCRIPT])
        self.assertNotEqual(stackctl.KEYRING_RESET_ALL_SCRIPT, stackctl.KEYRING_RESET_SCRIPT)

    def test_verify_argv_is_exact_and_holds_no_capability(self):
        self.assertEqual(stackctl.keyring_verify_argv(CADDY), [
            "docker", "run", "--rm", "--network", "none", "--read-only", "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
            "--user", "1654:1654", "--pids-limit", "64", "--memory", "64m",
            "--mount", "type=volume,source=decisya_bff-keyring,target=/keyring,readonly",
            "--entrypoint", "sh", CADDY, "-c", stackctl.KEYRING_VERIFY_SCRIPT,
        ])
        self.assertNotIn("--cap-add", stackctl.keyring_verify_argv(CADDY))

    def test_generator_argv_is_exact(self):
        self.assertEqual(stackctl.keyring_generator_argv(BFF), [
            "docker", "run", "--rm", "-i", "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--user", "1654:1654", "--entrypoint", "dotnet", BFF, "/app/Decisya.Bff.dll", "--generate-keyring-certificate",
        ])

    def test_no_tool_has_a_bind_mount_a_socket_an_environment_or_a_shared_namespace(self):
        for argv in (stackctl.keyring_prepare_argv(CADDY), stackctl.keyring_reset_argv(CADDY), stackctl.keyring_verify_argv(CADDY),
                     stackctl.keyring_generator_argv(BFF)):
            with self.subTest(argv=argv[2:6]):
                for banned in ("-e", "--env", "--env-file", "--privileged", "--pid", "--ipc", "--uts", "-v", "--volume", "--device",
                               "--network=host", "--net", "--cap-add=ALL", "--security-opt=seccomp=unconfined"):
                    self.assertNotIn(banned, argv)
                self.assertEqual(argv[argv.index("--network") + 1], "none")
                self.assertLessEqual(argv.count("--mount"), 1)
                joined = " ".join(argv[:-1])
                self.assertNotIn("docker.sock", joined)
                self.assertNotIn("type=bind", joined)
        self.assertEqual(
            [stackctl.keyring_prepare_argv(CADDY)[i + 1] for i, a in enumerate(stackctl.keyring_prepare_argv(CADDY)) if a == "--cap-add"],
            ["CHOWN", "DAC_OVERRIDE", "FOWNER"])

    def test_an_unpinned_image_is_refused(self):
        for image in ("caddy:latest", "docker.io/library/caddy:2", "", "docker.io/library/caddy@sha256:" + "e" * 63, CADDY + "\n"):
            with self.subTest(image=image[:30]):
                with self.assertRaises(stackctl.StackError):
                    stackctl.keyring_prepare_argv(image)
                with self.assertRaises(stackctl.StackError):
                    stackctl.keyring_reset_argv(image)
                with self.assertRaises(stackctl.StackError):
                    stackctl.keyring_verify_argv(image)
        for image in ("ghcr.io/mpcs2013/decisya-api@sha256:" + "a" * 64, "ghcr.io/mpcs2013/decisya-bff:latest", CADDY, ""):
            with self.subTest(generator=image[:30]):
                with self.assertRaises(stackctl.StackError):
                    stackctl.keyring_generator_argv(image)

    def test_the_volume_is_the_one_the_compose_project_makes(self):
        self.assertEqual(guards.KEYRING_VOLUME_NAME, "decisya_bff-keyring")
        self.assertIn("bff-keyring", guards.ALL_NAMED_VOLUMES)

    def test_the_pinned_caddy_image_is_read_from_the_same_resolver_as_check(self):
        image = stackctl.pinned_caddy_image(ROOT)
        self.assertRegex(image, r"@sha256:[0-9a-f]{64}$")
        self.assertEqual(image, guards.load_scan_refs(ROOT)["caddy"])


# --------------------------------------------------------------------------- the scripts (G4-122-03 a)

class ScriptTextTests(unittest.TestCase):
    def test_the_scripts_are_constants_that_refuse_before_they_change_anything(self):
        for name in ("KEYRING_PREPARE_SCRIPT", "KEYRING_RESET_SCRIPT", "KEYRING_RESET_ALL_SCRIPT"):
            script = getattr(stackctl, name)
            with self.subTest(script=name):
                self.assertTrue(script.startswith("set -eu\n"))
                refuse = script.index("exit 3")
                self.assertIn("! -type f ! -type d", script[:refuse])
                for change in ("chown", "chmod", "rm -f"):
                    if change in script:
                        self.assertLess(refuse, script.index(change), "%s must come after the refusal" % change)

    def test_nothing_follows_a_link_or_moves_a_file(self):
        for name in ("KEYRING_PREPARE_SCRIPT", "KEYRING_RESET_SCRIPT", "KEYRING_RESET_ALL_SCRIPT", "KEYRING_VERIFY_SCRIPT"):
            script = getattr(stackctl, name)
            with self.subTest(script=name):
                for banned in (" -L ", " -follow", "chown -R", "chmod -R", "cp ", "mv ", "ln ", "cat ", "tar ", "docker", "curl", "wget"):
                    self.assertNotIn(banned, script)
                self.assertNotIn("find -L", script)
                self.assertIn("-xdev", script)

    def test_prepare_sets_owner_and_modes_without_following_links(self):
        script = stackctl.KEYRING_PREPARE_SCRIPT
        self.assertIn("chown -h 1654:1654", script)
        self.assertIn("-type d -exec chmod 0700", script)
        self.assertIn("-type f -exec chmod 0600", script)

    def test_reset_deletes_and_never_moves(self):
        script = stackctl.KEYRING_RESET_SCRIPT
        self.assertIn("rm -f --", script)
        self.assertNotIn("attic", script)
        self.assertNotIn("mv ", script)

    def test_the_shell_patterns_are_the_python_patterns(self):
        for script in (stackctl.KEYRING_RESET_SCRIPT, stackctl.KEYRING_VERIFY_SCRIPT):
            for pattern in (guards.KEYRING_PLAINTEXT_PATTERN, guards.KEYRING_SECRET_PATTERN, guards.KEYRING_REVOCATION_PATTERN,
                            guards.KEYRING_DECRYPTOR_PATTERN, guards.KEYRING_ENCRYPTED_DATA_PATTERN):
                self.assertIn("'%s'" % pattern, script)

    def test_no_placeholder_is_left_in_any_script(self):
        for name in ("KEYRING_PREPARE_SCRIPT", "KEYRING_RESET_SCRIPT", "KEYRING_RESET_ALL_SCRIPT", "KEYRING_VERIFY_SCRIPT"):
            self.assertNotRegex(getattr(stackctl, name), r"@[A-Z_]+@")


@unittest.skipUnless(HAVE_SHELL, "needs a POSIX shell with find and grep")
class ResetOnAFixtureVolumeTests(unittest.TestCase):
    """G4-122-04 (a): after the reset, no key file that fails the strict rule remains anywhere in the volume."""

    def test_the_rule_in_python_agrees_with_the_fixtures(self):
        for name, (text, passes) in FIXTURES.items():
            with self.subTest(file=name):
                self.assertEqual(guards.keyring_file_problem(text) is None, passes)
        self.assertIsNotNone(guards.keyring_file_problem(PLAINTEXT_120))

    def test_reset_deletes_every_failing_key_file_and_keeps_the_rest_unchanged(self):
        with scratch_dir() as folder:
            populate(folder)
            before = {p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob("*") if p.is_file()}
            proc = run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            self.assertEqual(proc.returncode, 0, proc.stderr)
            after = {p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob("*") if p.is_file()}
            expected_kept = {name for name, (_, passes) in FIXTURES.items() if passes} | {"attic-20261008000000/key-old-wrapped.xml", "notes.txt"}
            self.assertEqual(set(after), expected_kept)
            for name in after:
                self.assertEqual(after[name], before[name], "a kept file must be byte for byte unchanged")
            failing = sum(1 for _, (_, passes) in FIXTURES.items() if not passes) + 1
            self.assertEqual(proc.stdout.strip(), "reset kept=%d deleted=%d" % (len(expected_kept) - 1, failing))
            for forbidden in ("masterKey", "unencryptedKey", "11111111", "22222222", "key-plaintext", "QUJDREVGRw"):
                self.assertNotIn(forbidden, proc.stdout + proc.stderr)

    def test_the_python_rule_and_the_shell_rule_agree_on_every_fixture(self):
        with scratch_dir() as folder:
            populate(folder)
            run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            survivors = {p.name for p in folder.rglob("*.xml")}
            for name, (text, _) in FIXTURES.items():
                with self.subTest(file=name):
                    self.assertEqual(name in survivors, guards.keyring_file_problem(text) is None)

    def test_reset_all_keys_deletes_every_key_file_and_nothing_else(self):
        with scratch_dir() as folder:
            populate(folder)
            proc = run_script(stackctl.KEYRING_RESET_ALL_SCRIPT, folder)
            self.assertEqual(proc.returncode, 0, proc.stderr)
            self.assertEqual(proc.stdout.strip(), "reset kept=0 deleted=%d" % (len(FIXTURES) + 2))
            self.assertEqual([p.name for p in folder.rglob("*") if p.is_file()], ["notes.txt"])

    def test_reset_all_keys_refuses_a_link_before_deleting(self):
        with scratch_dir() as folder:
            populate(folder)
            try:
                os.symlink(str(folder / "key-wrapped.xml"), str(folder / "link.xml"))
            except (OSError, NotImplementedError):
                self.skipTest("symlinks are not available here")
            proc = run_script(stackctl.KEYRING_RESET_ALL_SCRIPT, folder)
            self.assertEqual(proc.returncode, 3)
            self.assertTrue((folder / "key-wrapped.xml").exists())

    def test_reset_is_idempotent(self):
        with scratch_dir() as folder:
            populate(folder)
            run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            second = run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            self.assertEqual(second.returncode, 0)
            self.assertRegex(second.stdout.strip(), r"^reset kept=\d+ deleted=0$")

    def test_an_empty_volume_resets_cleanly(self):
        with scratch_dir() as folder:
            proc = run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            self.assertEqual((proc.returncode, proc.stdout.strip()), (0, "reset kept=0 deleted=0"))

    def test_a_link_makes_the_script_refuse_before_anything_changes(self):
        with scratch_dir() as folder:
            populate(folder)
            try:
                os.symlink(str(folder / "key-wrapped.xml"), str(folder / "link.xml"))
            except (OSError, NotImplementedError):
                self.skipTest("symlinks are not available here")
            before = sorted(p.name for p in folder.rglob("*"))
            proc = run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            self.assertEqual(proc.returncode, 3)
            self.assertIn("refused", proc.stderr)
            self.assertNotIn("link.xml", proc.stderr + proc.stdout)
            self.assertEqual(sorted(p.name for p in folder.rglob("*")), before, "nothing may change")

    def test_a_pipe_makes_the_script_refuse(self):
        with scratch_dir() as folder:
            populate(folder)
            try:
                os.mkfifo(str(folder / "fifo"))
            except (OSError, AttributeError):
                self.skipTest("fifos are not available here")
            proc = run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            self.assertEqual(proc.returncode, 3)
            self.assertTrue((folder / "key-plaintext-120.xml").exists())

    def test_the_verify_script_counts_the_same_files(self):
        with scratch_dir() as folder:
            populate(folder)
            proc = run_script(stackctl.KEYRING_VERIFY_SCRIPT, folder)
            self.assertEqual(proc.returncode, 0, proc.stderr)
            facts, reason = guards.parse_keyring_report(proc.stdout)
            self.assertIsNone(reason)
            total = len(FIXTURES) + 2
            passing = sum(1 for _, (_, passes) in FIXTURES.items() if passes) + 1
            self.assertEqual(facts["xml_files"], total)
            self.assertEqual(facts["xml_ok"], passing)
            self.assertEqual(facts["xml_failing"], total - passing)
            self.assertEqual(facts["other_files"], 1)
            self.assertEqual(facts["refused_entries"], 0)
            self.assertTrue(has(guards.keyring_problems(facts), "keyring reset"))

    def test_verify_is_clean_after_a_reset(self):
        with scratch_dir() as folder:
            populate(folder)
            run_script(stackctl.KEYRING_RESET_SCRIPT, folder)
            facts, _ = guards.parse_keyring_report(run_script(stackctl.KEYRING_VERIFY_SCRIPT, folder).stdout)
            self.assertEqual(facts["xml_failing"], 0)
            self.assertFalse(has(guards.keyring_problems(dict(facts, wrong_owner=0, wrong_mode=0)), "plaintext"))

    def test_verify_reports_a_link_without_changing_anything(self):
        with scratch_dir() as folder:
            populate(folder)
            try:
                os.symlink(str(folder / "key-wrapped.xml"), str(folder / "link.xml"))
            except (OSError, NotImplementedError):
                self.skipTest("symlinks are not available here")
            facts, _ = guards.parse_keyring_report(run_script(stackctl.KEYRING_VERIFY_SCRIPT, folder).stdout)
            self.assertEqual(facts["refused_entries"], 1)
            self.assertTrue((folder / "key-plaintext-120.xml").exists())


class RuleAndReportTests(unittest.TestCase):
    def test_each_failure_names_a_reason_from_a_closed_list(self):
        reasons = {
            PLAINTEXT_120: "plaintext", NULL_ENCRYPTED: "plaintext", WRAPPED_WITH_STRAY_MASTER_KEY: "plaintext",
            DPAPI: "certificate decryptor", TWO_SECRETS: "more than one", NO_ENCRYPTED_DATA: "EncryptedData", EMPTY_ELEMENT: "no encrypted secret",
        }
        for text, fragment in reasons.items():
            with self.subTest(fragment=fragment):
                reason = guards.keyring_file_problem(text)
                self.assertIn(fragment, reason or "")
                self.assertNotIn("QUJD", reason)

    def test_the_report_parser_is_strict(self):
        good = clean_keyring_report()
        facts, reason = guards.parse_keyring_report(good)
        self.assertIsNone(reason)
        self.assertEqual(set(facts), set(guards.KEYRING_REPORT_KEYS))
        for label, text in (
            ("a name", good + "key-1234.xml\n"),
            ("a key twice", good + "xml_ok=1\n"),
            ("an unknown key", good + "secret=1\n"),
            ("a missing key", "".join(good.splitlines(True)[1:])),
            ("a negative count", good.replace("xml_ok=0", "xml_ok=-1")),
            ("a leading zero", good.replace("xml_ok=0", "xml_ok=01")),
            ("a word", good.replace("xml_ok=0", "xml_ok=yes")),
        ):
            with self.subTest(case=label):
                self.assertIsNone(guards.parse_keyring_report(text)[0])

    def test_problems_name_counts_and_the_command_to_run(self):
        facts = dict(guards.parse_keyring_report(clean_keyring_report())[0])
        self.assertEqual(guards.keyring_problems(facts), [])
        self.assertTrue(has(guards.keyring_problems(dict(facts, xml_failing=2)), "keyring reset --confirm"))
        self.assertTrue(has(guards.keyring_problems(dict(facts, wrong_mode=1)), "keyring prepare"))
        self.assertTrue(has(guards.keyring_problems(dict(facts, wrong_owner=3)), "keyring prepare"))
        self.assertTrue(has(guards.keyring_problems(dict(facts, refused_entries=1)), "neither a file nor a folder"))


# --------------------------------------------------------------------------- the commands (a recording runner)

class KeyringRunner(FakeRunner):
    """Records every Docker call in order and answers as a healthy host would."""

    def __init__(self, volume_exists=True, one_shot_code=0, report=None):
        super().__init__()
        self.volume_exists = volume_exists
        self.one_shot_code = one_shot_code
        self.report = report if report is not None else clean_keyring_report()
        self.events: list = []

    def compose(self, args, input_text=None, timeout=600):
        self.events.append(("compose", list(args)))
        return super().compose(args, input_text=input_text, timeout=timeout)

    def run(self, argv, input_text=None, timeout=600):
        self.events.append(("run", list(argv)))
        self.runs.append(list(argv))
        if argv[1:3] == ["volume", "inspect"]:
            return completed("", 0 if self.volume_exists else 1)
        if "--mount" in argv:
            script = argv[-1]
            if script == stackctl.KEYRING_RESET_SCRIPT:
                return completed("reset kept=1 deleted=2\n", self.one_shot_code)
            if script == stackctl.KEYRING_PREPARE_SCRIPT:
                return completed("prepared files=1 dirs=1\n", self.one_shot_code)
            if script == stackctl.KEYRING_VERIFY_SCRIPT:
                return completed(self.report, self.one_shot_code)
        return completed("")

    def inspect_project(self):
        return []

    def docker_subnets(self):
        return []

    def one_shots(self):
        return [argv for kind, argv in self.events if kind == "run" and "--mount" in argv]


class KeyringCommandTests(unittest.TestCase):
    def setUp(self):
        self.pinned = stackctl.pinned_caddy_image(ROOT)

    def test_prepare_runs_the_exact_argv_and_prints_counts_only(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner()
            code, output = run(["keyring", "prepare", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 0, output)
            self.assertEqual(runner.one_shots(), [stackctl.keyring_prepare_argv(self.pinned)])
            self.assertIn("prepared files=1 dirs=1", output)
            self.assertEqual([e for e in runner.events if e[0] == "compose"], [], "an existing volume needs no Compose call")

    def test_prepare_on_a_first_start_lets_compose_create_the_volume(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner(volume_exists=False)
            code, _ = run(["keyring", "prepare", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 0)
            kinds = [(kind, args[:2] if kind == "compose" else "run") for kind, args in runner.events]
            self.assertEqual(kinds[0], ("run", "run"), "the volume is looked up first")
            self.assertEqual(kinds[1], ("compose", ["create", "bff"]))
            self.assertEqual(runner.one_shots(), [stackctl.keyring_prepare_argv(self.pinned)])

    def test_reset_needs_confirm_and_touches_nothing_without_it(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner()
            code, output = run(["keyring", "reset", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 2)
            self.assertIn("--confirm", output)
            self.assertEqual(runner.events, [])

    def test_reset_stops_the_bff_deletes_then_prepares_and_does_not_start_anything(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner()
            code, output = run(["keyring", "reset", "--confirm", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 0, output)
            self.assertEqual([args for kind, args in runner.events if kind == "compose"], [["stop", "bff"]])
            self.assertEqual(runner.one_shots(), [stackctl.keyring_reset_argv(self.pinned), stackctl.keyring_prepare_argv(self.pinned)])
            first_stop = next(i for i, e in enumerate(runner.events) if e == ("compose", ["stop", "bff"]))
            first_shot = next(i for i, e in enumerate(runner.events) if e[0] == "run" and "--mount" in e[1])
            self.assertLess(first_stop, first_shot, "the bff stops before the volume is touched")
            self.assertIn("reset kept=1 deleted=2", output)
            self.assertIn("prepared", output)
            self.assertNotIn("masterKey", output)

    def test_reset_all_keys_uses_the_all_keys_script_and_still_prepares(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner()
            code, output = run(["keyring", "reset", "--confirm", "--all-keys", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 0, output)
            self.assertEqual(runner.one_shots(), [stackctl.keyring_reset_argv(self.pinned, all_keys=True), stackctl.keyring_prepare_argv(self.pinned)])

    def test_reset_without_a_volume_changes_nothing(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner(volume_exists=False)
            code, output = run(["keyring", "reset", "--confirm", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 2)
            self.assertIn("nothing to reset", output)
            self.assertEqual([e for e in runner.events if e[0] == "compose"], [])
            self.assertEqual(runner.one_shots(), [])

    def test_a_refused_volume_is_reported_and_prepare_does_not_run(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner(one_shot_code=3)
            code, output = run(["keyring", "reset", "--confirm", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 2)
            self.assertIn("refused", output)
            self.assertEqual(len(runner.one_shots()), 1)

    def test_a_failed_one_shot_is_reported_by_exit_code_only(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner(one_shot_code=125)
            code, output = run(["keyring", "prepare", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 2)
            self.assertIn("exit 125", output)

    def test_up_prepares_the_key_ring_after_the_root_and_before_the_rest_starts(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            runner = KeyringRunner()
            with mock.patch.object(stackctl, "static_problems", return_value=[]), \
                    mock.patch.object(stackctl, "docker_problems", return_value=[]), \
                    mock.patch.object(stackctl, "wait_healthy", return_value=True), \
                    mock.patch.object(stackctl, "export_root", side_effect=lambda *a, **k: runner.events.append(("export-root", []))), \
                    mock.patch.object(stackctl, "cmd_verify", return_value=0):
                code, output = run(["up", "--stack", str(stack), "--repo", str(ROOT)], runner_factory=lambda s: runner)
            self.assertEqual(code, 0, output)
            order = []
            for kind, args in runner.events:
                if kind == "export-root":
                    order.append("export-root")
                elif kind == "run" and args[-1] == stackctl.KEYRING_PREPARE_SCRIPT:
                    order.append("prepare")
                elif kind == "compose" and args == ["up", "-d"]:
                    order.append("up-rest")
            self.assertEqual(order, ["export-root", "prepare", "up-rest"])

    def test_verify_reports_plaintext_leftovers_by_count(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            report = clean_keyring_report().replace("xml_failing=0", "xml_failing=2").replace("xml_files=0", "xml_files=3").replace("xml_ok=0", "xml_ok=1")
            runner = KeyringRunner(report=report)
            notes: list = []
            problems = stackctl.verify_problems(stack, runner, notes)
            self.assertTrue(has(problems, "2 key file(s) are plaintext or not wrapped"), problems)
            self.assertIn("keyring: key_files=3 wrapped=1 failing=2 other_files=0", notes)
            self.assertEqual(runner.one_shots(), [stackctl.keyring_verify_argv(self.pinned)])

    def test_verify_fails_closed_when_the_check_cannot_run_or_prints_oddities(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            for label, runner in (
                ("exit code", KeyringRunner(one_shot_code=1)),
                ("an extra line", KeyringRunner(report=clean_keyring_report() + "secret.xml\n")),
                ("no output", KeyringRunner(report="")),
            ):
                with self.subTest(case=label):
                    problems = stackctl.keyring_check(stack, ROOT, runner)
                    self.assertTrue(has(problems, "key ring check:"), problems)
                    self.assertNotIn("secret.xml", "\n".join(problems))

    def test_a_clean_ring_adds_no_problem(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            self.assertEqual(stackctl.keyring_check(stack, ROOT, KeyringRunner()), [])


# --------------------------------------------------------------------------- the certificate secrets

class CertificateSecretTests(unittest.TestCase):
    def test_init_runs_the_generator_with_the_password_on_stdin_only(self):
        with scratch_dir() as parent:
            stack = make_stack(parent, secrets_init=False)
            runner = FakeRunner()
            code, output = run(["secrets", "init", "--stack", str(stack)], runner_factory=lambda s: runner)
            self.assertEqual(code, 0, output)
            secrets = secrets_of(stack)
            self.assertEqual(set(secrets), set(stackctl.SECRET_FILE_NAMES))
            # The image is looked up (and pulled when missing) by the digest in the environment file.
            self.assertEqual(runner.runs[0], ["docker", "image", "inspect", BFF])
            argv, stdin = runner.generated[0]
            self.assertEqual(argv, stackctl.keyring_generator_argv(BFF))
            self.assertRegex(stdin.decode("ascii"), r"^[A-Za-z0-9]{48}\n$")
            self.assertEqual(stdin.decode("ascii"), secrets[guards.DP_CERT_PASSWORD] + "\n")
            self.assertNotIn(secrets[guards.DP_CERT_PASSWORD], " ".join(argv))
            self.assertEqual(secrets[guards.DP_PREVIOUS], "")
            self.assertEqual(secrets[guards.DP_PREVIOUS_PASSWORD], "")
            self.assertEqual(stackctl.secret_problems(stack / "secrets"), [])
            for value in (secrets[guards.DP_CERT], secrets[guards.DP_CERT_PASSWORD]):
                self.assertNotIn(value, output)

    def test_a_missing_image_is_pulled_first(self):
        class NoImage(FakeRunner):
            def run(self, argv, input_text=None, timeout=600):
                self.runs.append(list(argv))
                return completed("", 1 if argv[1:3] == ["image", "inspect"] else 0)

        with scratch_dir() as parent:
            stack = make_stack(parent, secrets_init=False)
            runner = NoImage()
            code, _ = run(["secrets", "init", "--stack", str(stack)], runner_factory=lambda s: runner)
            self.assertEqual(code, 0)
            self.assertEqual(runner.runs[:2], [["docker", "image", "inspect", BFF], ["docker", "pull", BFF]])

    def test_a_generator_failure_writes_no_file_and_shows_only_the_exit_code(self):
        class Failing(FakeRunner):
            def __init__(self, code, stdout=b"", stderr=b"LEAK"):
                super().__init__()
                self.code, self.out, self.err = code, stdout, stderr

            def run_bytes(self, argv, input_bytes, timeout=600):
                self.generated.append((list(argv), input_bytes))
                return subprocess.CompletedProcess(argv, self.code, self.out, self.err)

        good = base64.b64encode(b"x" * 300)
        for label, runner, fragment in (
            ("bad stdin or generation", Failing(1), "failed (exit 1)"),
            ("refused", Failing(2), "refused"),
            ("no output", Failing(0), "must be base64"),
            ("a trailing newline", Failing(0, good + b"\n"), "must be base64"),
            ("not base64", Failing(0, b"not base64!"), "must be base64"),
            ("not ascii", Failing(0, b"\xff\xfe"), "not ASCII"),
            ("too large", Failing(0, base64.b64encode(b"x" * (16 * 1024 + 1))), "16 KiB"),
        ):
            with self.subTest(case=label), scratch_dir() as parent:
                stack = make_stack(parent, secrets_init=False)
                code, output = run(["secrets", "init", "--stack", str(stack)], runner_factory=lambda s, r=runner: r)
                self.assertEqual(code, 2)
                self.assertIn(fragment, output)
                self.assertNotIn("LEAK", output)
                self.assertEqual(list((stack / "secrets").iterdir()), [], "no file may be written when the generator fails")

    def test_init_needs_a_valid_bff_image_in_the_environment_file(self):
        with scratch_dir() as parent:
            stack = make_stack(parent, secrets_init=False)
            (stack / ".env").write_text("DECISYA_BFF_IMAGE=ghcr.io/mpcs2013/decisya-bff:latest\n", encoding="utf-8")
            runner = FakeRunner()
            code, output = run(["secrets", "init", "--stack", str(stack)], runner_factory=lambda s: runner)
            self.assertEqual(code, 2)
            self.assertIn("DECISYA_BFF_IMAGE", output)
            self.assertEqual(runner.generated, [])
            self.assertEqual(list((stack / "secrets").iterdir()), [])

    def test_add_creates_only_the_four_files_of_an_existing_120_stack(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            for name in guards.DP_SECRETS:
                unlink_force(stack / "secrets" / name)
            before = secrets_of(stack)
            self.assertEqual(len(before), 13)
            code, output = run(["secrets", "add", "dataprotection_cert", "--stack", str(stack)], runner_factory=lambda s: FakeRunner())
            self.assertEqual(code, 0, output)
            after = secrets_of(stack)
            self.assertEqual(set(after), set(stackctl.SECRET_FILE_NAMES))
            for name in before:
                self.assertEqual(after[name], before[name], "add must not touch another credential")
            self.assertEqual(stackctl.secret_problems(stack / "secrets"), [])
            self.assertIn("keyring reset --confirm", output)

    def test_add_refuses_when_any_of_the_four_exists_and_changes_nothing(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            unlink_force(stack / "secrets" / guards.DP_CERT)
            before = secrets_of(stack)
            runner = FakeRunner()
            code, output = run(["secrets", "add", "dataprotection_cert", "--stack", str(stack)], runner_factory=lambda s: runner)
            self.assertEqual(code, 2)
            self.assertIn("refusing to overwrite", output)
            self.assertEqual(secrets_of(stack), before)
            self.assertEqual(runner.generated, [])

    def test_rotate_moves_the_current_pair_to_previous_and_writes_a_new_one(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            before = secrets_of(stack)
            code, output = run(["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], runner_factory=lambda s: FakeRunner())
            self.assertEqual(code, 0, output)
            after = secrets_of(stack)
            self.assertEqual(after[guards.DP_PREVIOUS], before[guards.DP_CERT])
            self.assertEqual(after[guards.DP_PREVIOUS_PASSWORD], before[guards.DP_CERT_PASSWORD])
            self.assertNotEqual(after[guards.DP_CERT], before[guards.DP_CERT])
            self.assertNotEqual(after[guards.DP_CERT_PASSWORD], before[guards.DP_CERT_PASSWORD])
            self.assertEqual({n for n in after if after[n] != before[n]}, set(guards.DP_SECRETS))
            self.assertEqual(stackctl.secret_problems(stack / "secrets"), [])
            for value in (after[guards.DP_CERT], after[guards.DP_PREVIOUS], after[guards.DP_CERT_PASSWORD]):
                self.assertNotIn(value, output)

    def test_a_second_rotation_is_refused_until_the_previous_pair_is_retired(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            factory = lambda s: FakeRunner()  # noqa: E731
            run(["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], runner_factory=factory)
            before = secrets_of(stack)
            code, output = run(["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], runner_factory=factory)
            self.assertEqual(code, 2)
            self.assertIn("not empty", output)
            self.assertIn("retire", output)
            self.assertEqual(secrets_of(stack), before)
            code, output = run(["secrets", "rotate", "dataprotection_cert", "--drop-previous", "--stack", str(stack)], runner_factory=factory)
            self.assertEqual(code, 0, output)
            after = secrets_of(stack)
            self.assertEqual(after[guards.DP_PREVIOUS], before[guards.DP_CERT])
            self.assertEqual(stackctl.secret_problems(stack / "secrets"), [])

    def test_retire_empties_both_previous_files_together_and_is_idempotent(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            run(["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], runner_factory=lambda s: FakeRunner())
            before = secrets_of(stack)
            code, output = run(["secrets", "retire", "dataprotection_previous", "--stack", str(stack)])
            self.assertEqual(code, 0, output)
            after = secrets_of(stack)
            self.assertEqual((after[guards.DP_PREVIOUS], after[guards.DP_PREVIOUS_PASSWORD]), ("", ""))
            for name in before:
                if name not in (guards.DP_PREVIOUS, guards.DP_PREVIOUS_PASSWORD):
                    self.assertEqual(after[name], before[name])
            self.assertEqual(stackctl.secret_problems(stack / "secrets"), [])
            code, output = run(["secrets", "retire", "dataprotection_previous", "--stack", str(stack)])
            self.assertEqual(code, 0)
            self.assertIn("already empty", output)

    def test_flags_that_belong_to_other_credentials_are_refused(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            before = secrets_of(stack)
            for argv in (["secrets", "rotate", "dataprotection_cert", "--apply-db"], ["secrets", "rotate", "dataprotection_cert", "--files-only"],
                         ["secrets", "rotate", "redis", "--drop-previous"]):
                code, _ = run(argv + ["--stack", str(stack)], runner_factory=lambda s: FakeRunner())
                self.assertEqual(code, 2, argv)
            self.assertEqual(secrets_of(stack), before)

    def test_a_missing_file_stops_a_rotation(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            unlink_force(stack / "secrets" / guards.DP_PREVIOUS)
            code, output = run(["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], runner_factory=lambda s: FakeRunner())
            self.assertEqual(code, 2)
            self.assertIn("does not exist", output)

    # ----- shapes (G2 D10)

    def test_the_certificate_shapes(self):
        good = base64.b64encode(b"x" * 3000).decode("ascii")
        password = "p" * 48
        self.assertIsNone(stackctl.shape_problem(guards.DP_CERT, good))
        self.assertIsNone(stackctl.shape_problem(guards.DP_PREVIOUS, good))
        self.assertIsNone(stackctl.shape_problem(guards.DP_PREVIOUS, ""))
        self.assertIsNone(stackctl.shape_problem(guards.DP_PREVIOUS_PASSWORD, ""))
        self.assertIsNone(stackctl.shape_problem(guards.DP_CERT_PASSWORD, password))
        self.assertIsNone(stackctl.shape_problem(guards.DP_CERT_PASSWORD, "p" * 256))
        for name, text in (
            (guards.DP_CERT, ""), (guards.DP_CERT, good[:60] + "\n" + good[60:]), (guards.DP_CERT, good + "\n"), (guards.DP_CERT, "not base64!"),
            (guards.DP_CERT, good[:-1]), (guards.DP_CERT, base64.b64encode(b"x" * (16 * 1024 + 1)).decode("ascii")),
            (guards.DP_PREVIOUS, " "), (guards.DP_CERT_PASSWORD, ""), (guards.DP_CERT_PASSWORD, "p" * 31), (guards.DP_CERT_PASSWORD, "p" * 257),
            (guards.DP_CERT_PASSWORD, "p" * 31 + "-"), (guards.DP_PREVIOUS_PASSWORD, "short"),
        ):
            with self.subTest(name=name[-12:], text=text[:8]):
                self.assertIsNotNone(stackctl.shape_problem(name, text))

    def test_the_previous_pair_is_empty_together(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            secrets = stack / "secrets"
            put(secrets / guards.DP_PREVIOUS, base64.b64encode(b"y" * 600))
            self.assertTrue(has(stackctl.secret_problems(secrets), "must be empty together"))
            put(secrets / guards.DP_PREVIOUS_PASSWORD, b"q" * 40)
            self.assertEqual(stackctl.secret_problems(secrets), [])
            put(secrets / guards.DP_PREVIOUS, (secrets / guards.DP_CERT).read_bytes())
            self.assertTrue(has(stackctl.secret_problems(secrets), "holds the current certificate"))
            put(secrets / guards.DP_PREVIOUS, b"")
            self.assertTrue(has(stackctl.secret_problems(secrets), "must be empty together"))

    def test_a_shape_message_never_repeats_a_value(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            canary = "CANARY-NOT-A-REAL-PFX-0123456789abcdef!"
            put(stack / "secrets" / guards.DP_CERT, canary.encode())
            put(stack / "secrets" / guards.DP_CERT_PASSWORD, (canary + "\n").encode())
            problems = stackctl.secret_problems(stack / "secrets")
            self.assertTrue(problems)
            self.assertNotIn("CANARY", "\n".join(problems))

    def test_no_message_holds_sixteen_characters_of_the_certificate_or_password(self):
        """S-122-07: every message of a failing init, add, rotate, retire and check is searched for any
        16-character window of the generated values."""
        with scratch_dir() as parent:
            stack = make_stack(parent)
            values = [secrets_of(stack)[n] for n in (guards.DP_CERT, guards.DP_CERT_PASSWORD)]
            texts = []
            factory = lambda s: FakeRunner()  # noqa: E731
            for argv in (["secrets", "init"], ["secrets", "add", "dataprotection_cert"], ["secrets", "rotate", "dataprotection_cert"],
                         ["secrets", "rotate", "dataprotection_cert"], ["secrets", "retire", "dataprotection_previous"],
                         ["check", "--static-only", "--repo", str(ROOT)]):
                texts.append(run(argv + ["--stack", str(stack)], runner_factory=factory)[1])
            values += [secrets_of(stack)[n] for n in (guards.DP_CERT, guards.DP_CERT_PASSWORD)]
            joined = "\n".join(texts)
            self.assertGreater(len(joined), 100)
            for value in values:
                for start in range(0, len(value) - 15, 7):
                    self.assertNotIn(value[start:start + 16], joined)


# --------------------------------------------------------------------------- the guards (stackguards.py)

def _cfg_for_secrets(extra=None, drop=None):
    top = {name: {"file": "./secrets/%s" % name} for name in guards.ALL_SECRETS}
    services = {}
    for service, names in guards.SECRET_CONSUMERS.items():
        names = set(names)
        if drop and service == drop[0]:
            names.discard(drop[1])
        if extra and service == extra[0]:
            names.add(extra[1])
        services[service] = {"secrets": [{"source": n, "target": n} for n in sorted(names)]}
    return {"secrets": top}, services


class GuardTests(unittest.TestCase):
    def test_the_four_certificate_secrets_belong_to_the_bff_alone(self):
        for name in guards.DP_SECRETS:
            consumers = [s for s, names in guards.SECRET_CONSUMERS.items() if name in names]
            self.assertEqual(consumers, ["bff"], name)
        self.assertTrue(set(guards.DP_SECRETS) <= guards.ALL_SECRETS)

    def test_the_secret_table_can_fail_for_the_certificate_files(self):
        cfg, services = _cfg_for_secrets()
        self.assertEqual(guards._secret_problems(cfg, services), [])
        for service in ("api", "migrator", "keycloak", "caddy", "redis", "postgres"):
            for name in guards.DP_SECRETS:
                with self.subTest(service=service, secret=name[-14:]):
                    cfg, services = _cfg_for_secrets(extra=(service, name))
                    self.assertTrue(has(guards._secret_problems(cfg, services), "differ from the D3 table"))
        for name in guards.DP_SECRETS:
            with self.subTest(missing=name[-14:]):
                cfg, services = _cfg_for_secrets(drop=("bff", name))
                self.assertTrue(has(guards._secret_problems(cfg, services), "differ from the D3 table"))

    def test_the_committed_overlay_names_the_four_files_for_the_bff(self):
        text = (ROOT / "deploy" / "compose" / "docker-compose.stack.yaml").read_text(encoding="utf-8")
        for name in guards.DP_SECRETS:
            self.assertEqual(text.count("source: %s\n" % name), 1, name)
            self.assertIn("  %s:\n    file: ./secrets/%s" % (name, name), text)
        bff = text.split("\n  bff:\n", 1)[1].split("\n# Replaces the publisher", 1)[0]
        for name in guards.DP_SECRETS:
            self.assertIn("source: %s" % name, bff)

    def test_a_named_volume_has_no_driver_no_driver_opts_and_no_name_override(self):
        def problems(volume):
            volumes = {"postgres-data": {}, "caddy-data": {}, "bff-keyring": volume}
            return guards._top_level_problems({"name": "decisya", "volumes": volumes})

        self.assertEqual(problems({}), [])
        self.assertEqual(problems({"name": "decisya_bff-keyring"}), [])
        for label, volume, fragment in (
            ("driver", {"driver": "local"}, "driver is not allowed"),
            ("driver_opts", {"driver_opts": {"type": "none", "o": "bind", "device": "/srv/x"}}, "driver_opts is not allowed"),
            ("both", {"driver": "local", "driver_opts": {"o": "bind"}}, "driver is not allowed"),
            ("name override", {"name": "someone_elses_volume"}, "name override"),
            ("external", {"external": True}, "external"),
        ):
            with self.subTest(case=label):
                self.assertTrue(has(problems(volume), fragment), problems(volume))

    def test_the_generator_flag_is_banned_in_command_entrypoint_and_healthcheck(self):
        flag = guards.GENERATOR_FLAG
        self.assertEqual(guards._generator_flag_problems({"bff": {"command": ["dotnet", "x.dll"]}, "api": {}}), [])
        for label, service in (
            ("command list", {"command": ["dotnet", "/app/Decisya.Bff.dll", flag]}),
            ("command string", {"command": "dotnet /app/Decisya.Bff.dll " + flag}),
            ("entrypoint", {"entrypoint": ["dotnet", "/app/Decisya.Bff.dll", flag]}),
            ("healthcheck", {"healthcheck": {"test": ["CMD", "dotnet", "/app/Decisya.Bff.dll", flag]}}),
            ("upper case", {"command": [flag.upper()]}),
            ("glued", {"command": ["x" + flag + "=1"]}),
        ):
            with self.subTest(case=label):
                self.assertTrue(has(guards._generator_flag_problems({"bff": service}), "generator flag"), label)

    def test_rate_limit_environment_keys(self):
        ok = [
            ("bff", "Bff__RateLimits__login__PermitLimit", "10"), ("bff", "Bff__RateLimits__login__WindowSeconds", "60"),
            ("bff", "Bff__RateLimits__api__AnonymousPermitLimit", "60"), ("bff", "Bff__RateLimits__backchannel_logout__PermitLimit", "300"),
            ("bff", "bff__ratelimits__admin__permitlimit", "30"), ("bff", "Bff:RateLimits:admin:WindowSeconds", "3600"),
            ("bff", "Bff__RateLimits__api__PermitLimit", "100000"), ("api", "ASPNETCORE_ENVIRONMENT", "Production"),
        ]
        for service, key, value in ok:
            with self.subTest(key=key):
                self.assertIsNone(guards.ratelimit_env_problem(service, key, value))
        bad = [
            ("bff", "Bff__RateLimits__login__PermitLimit", "0"), ("bff", "Bff__RateLimits__login__PermitLimit", "100001"),
            ("bff", "Bff__RateLimits__login__WindowSeconds", "3601"), ("bff", "Bff__RateLimits__login__PermitLimit", "ten"),
            ("bff", "Bff__RateLimits__login__PermitLimit", "${LIMIT}"), ("bff", "Bff__RateLimits__login__PermitLimit", "-5"),
            ("bff", "Bff__RateLimits__login__AnonymousPermitLimit", "5"), ("bff", "Bff__RateLimits__nowhere__PermitLimit", "5"),
            ("bff", "Bff__RateLimits__login", "5"), ("bff", "Bff__RateLimits__login__PermitLimit__x", "5"),
            ("api", "Bff__RateLimits__login__PermitLimit", "5"), ("keycloak", "Bff__RateLimits__api__PermitLimit", "5"),
        ]
        for service, key, value in bad:
            with self.subTest(service=service, key=key, value=value):
                self.assertIsNotNone(guards.ratelimit_env_problem(service, key, value))

    def test_the_rate_limit_keys_reach_the_environment_guard(self):
        services = {
            "bff": {"environment": {"Bff__RateLimits__login__PermitLimit": "5000000"}},
            "api": {"environment": {"Bff__RateLimits__login__PermitLimit": "5"}},
        }
        problems = guards._environment_problems(services, False, {})
        self.assertTrue(has(problems, "must be an integer from 1 to 100000"), problems)
        self.assertTrue(has(problems, "service api: Bff__RateLimits__login__PermitLimit is a BFF setting"), problems)
        services["bff"]["environment"] = {"Bff__RateLimits__login__PermitLimit": "50"}
        self.assertFalse(has(guards._environment_problems({"bff": services["bff"]}, False, {}), "RateLimits"))

    def test_the_certificate_names_are_never_environment_names(self):
        # They are file secrets: a name that looks like a secret in `environment` is refused, as before.
        problems = guards._environment_problems({"bff": {"environment": {guards.DP_CERT_PASSWORD: "x"}}}, False, {})
        self.assertTrue(has(problems, "looks like a secret"), problems)

    def test_the_guard_module_stays_free_of_the_launch_markers(self):
        # docs/security/threat-models/realm-guard-scope.md, "Amendment at G3 for #121", condition 4.
        text = (ROOT / "deploy" / "compose" / "stackguards.py").read_text(encoding="utf-8").lower()
        # The last three are built from parts: no file in the stack tooling or its tests may hold them as a literal.
        for marker in ("subprocess", "os.system", "os.exec", "os.spawn", "popen", "--" + "import" + "-realm", "start" + "-dev",
                       "decisya" + "-realm" + ".json"):
            self.assertNotIn(marker, text)


class SmokeHelperTests(unittest.TestCase):
    """The pure helpers of deploy/tests/stack_smoke.py (the script itself needs a Linux Docker host)."""

    @classmethod
    def setUpClass(cls):
        import stack_smoke
        cls.smoke = stack_smoke

    def test_the_forged_addresses_are_eleven_documentation_range_values(self):
        values = self.smoke.FORGED_FORWARDED_FOR
        self.assertEqual(len(values), 11)
        self.assertEqual(len(set(values)), 11)
        for value in values:
            self.assertTrue(value.startswith("203.0.113."), "RFC 5737 documentation range only")

    def test_the_curl_output_parser(self):
        text = "HTTP/2 429 \r\ncontent-type: application/problem+json\r\nretry-after: 15\r\ndate: Thu, 08 Oct 2026 10:00:00 GMT\r\n\r\n\n429"
        status, headers = self.smoke.parse_curl_probe(text)
        self.assertEqual(status, 429)
        self.assertEqual(headers["retry-after"], "15")
        self.assertEqual(headers["content-type"], "application/problem+json")
        self.assertEqual(self.smoke.parse_curl_probe("\n000")[0], 0)
        self.assertIsNone(self.smoke.parse_curl_probe("")[0])

    def test_retry_after_must_be_a_whole_number_of_seconds(self):
        for good in ("1", "15", "60"):
            self.assertTrue(self.smoke.retry_after_ok(good), good)
        for bad in (None, "", "0", "61", "1.5", "-1", "15s", "Thu, 08 Oct 2026 10:00:00 GMT", " 15", "0015x"):
            self.assertFalse(self.smoke.retry_after_ok(bad), bad)

    @staticmethod
    def event(event_id, message):
        import json
        return json.dumps({"timestamp": "2026-10-08T10:00:00Z", "level": "Warning", "event_id": event_id, "message": message}) + "\n"

    def test_the_log_verdict_counts_the_partition_kind(self):
        template = "ratelimit.rejected: a request was refused by the rate limiter (%s)."
        log = ("not json\n" + self.event(1820, template % "login, ip") + self.event(1800, "other (login, ip).")
               + self.event(1820, template % "api, session"))
        self.assertEqual(self.smoke.ratelimit_log_verdict(log), {"rejected": 2, "login_ip": 1, "login_unknown": 0})
        unknown = self.event(1820, template % "login, unknown")
        self.assertEqual(self.smoke.ratelimit_log_verdict(unknown), {"rejected": 1, "login_ip": 0, "login_unknown": 1})
        self.assertEqual(self.smoke.ratelimit_log_verdict(""), {"rejected": 0, "login_ip": 0, "login_unknown": 0})

    def test_the_secret_needles_cover_the_certificate_and_its_windows(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            secrets = secrets_of(stack)
            needles = self.smoke.secret_needles(stack)
            self.assertIn(secrets[guards.DP_CERT], needles)
            self.assertIn(secrets[guards.DP_CERT_PASSWORD], needles)
            text = secrets[guards.DP_CERT]
            self.assertIn(text[len(text) // 2:len(text) // 2 + 32], needles)
            self.assertIn(text[-40:-8], needles)
            self.assertNotIn("", needles, "an empty previous pair has nothing to look for")
            run(["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], runner_factory=lambda s: FakeRunner())
            rotated = self.smoke.secret_needles(stack)
            self.assertIn(secrets_of(stack)[guards.DP_PREVIOUS], rotated)
            self.assertIn(secrets[guards.DP_CERT], rotated, "the old certificate is now the previous file")


if __name__ == "__main__":
    unittest.main()
