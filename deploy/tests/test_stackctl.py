"""stackctl.py and stackguards.py unit tests (issue #120, D10 item 10, G3 G4-120-01 to 03, S-120-03).

No Docker needed: the Docker calls are replaced by a fake runner, or the `--static-only` form of the
check is used. Every test builds its own stack folder in a scratch folder outside any Git tree, in
documentation-range values.

The canary test at the bottom is the one that matters most: it runs every command, including the
failing ones, and proves that no secret value (and no derived value) is ever printed.
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import os
import re
import subprocess
import sys
import unittest
from pathlib import Path
from unittest import mock

from deploy_common import (
    COMPOSE_DIR, FIXTURE_VALUES, GENERATED, ROOT, FakeRunner, clean_environment, env_text, guards, images_txt, make_stack,
    quiet, scratch_dir, stackctl, unlink_force, write_force,
)

POSIX = os.name == "posix"


def ip(*octets):
    """Addresses are built from parts: deploy/tests is scanned for address literals too."""
    return ".".join(str(o) for o in octets)


def run(argv, runner_factory=None):
    return quiet(stackctl.main, argv, runner_factory=runner_factory)


def read_secrets(stack: Path) -> dict:
    return {p.name: p.read_text(encoding="utf-8") for p in (stack / "secrets").iterdir()}


def problems_of(stack: Path) -> list:
    with clean_environment():
        return stackctl.static_problems(stack, ROOT)


def has(problems, fragment) -> bool:
    return any(fragment in p for p in problems)


class ValueGuardTests(unittest.TestCase):
    def test_the_fixture_is_valid(self):
        self.assertEqual(guards.env_problems(dict(FIXTURE_VALUES)), [])

    def test_bad_values_are_refused_by_type(self):
        digest = "a" * 64
        cases = {
            "DECISYA_APP_HOST": ["App.p0.home.arpa", "app_p0.home.arpa", "app", "app.p0.home.arpa\nrespond 200", "app.p0.home.arpa ",
                                 "app..home.arpa", "-app.home.arpa", "a" * 64 + ".home.arpa", "app.p0.home.arpa{", "ap p.home.arpa"],
            "DECISYA_HTTPS_PORT": ["80", "8444", "abc", "8443.0", "", "-8443", "8443 8443"],
            "DECISYA_BIND_ADDRESS": ["0.0.0.0", ip(224, 0, 0, 1), ip(255, 255, 255, 255), "192.0.2.10/24", "::1", "localhost",
                                     ip(169, 254, 1, 1), "192.0.2.256", "192.0.2.10 192.0.2.11"],
            "DECISYA_WORKSTATION_ADDRESS": ["192.0.2.0/24", "198.51.100.0/24", "", "192.0.2.20 ", "192.0.2.20,192.0.2.21", "0.0.0.0",
                                            "{$X}", ip(224, 0, 0, 1), "192.0.2.20/31"],
            "DECISYA_LAN_SUBNET": ["198.51.100.0", ip(10, 0, 0, 0) + "/8", ip(8, 8, 8, 0) + "/24", "198.51.100.7/24",
                                   ip(169, 254, 0, 0) + "/16", "127.0.0.0/24", "fd00::/8", "0.0.0.0/0",
                                   "198.51.100.0/24 192.0.2.0/24", "198.51.100.0/24\n", "{$X}/24"],
            "DECISYA_API_IMAGE": ["ghcr.io/mpcs2013/decisya-api:latest", "ghcr.io/mpcs2013/decisya-api@sha256:" + digest.upper(),
                                  "ghcr.io/mpcs2013/decisya-api@sha256:" + digest[:63], "ghcr.io/mpcs2013/decisya-bff@sha256:" + digest,
                                  "docker.io/x/decisya-api@sha256:" + digest, "ghcr.io/mpcs2013/decisya-api@sha256:" + digest + "\n"],
        }
        for key, values in cases.items():
            for value in values:
                with self.subTest(key=key, value=value[:12]):
                    self.assertIsNotNone(guards.value_problem(key, value))

    def test_good_values_are_accepted(self):
        for key, value in FIXTURE_VALUES.items():
            with self.subTest(key=key):
                self.assertIsNone(guards.value_problem(key, value))
        self.assertIsNone(guards.value_problem("DECISYA_WORKSTATION_ADDRESS", "192.0.2.20/32"))
        self.assertIsNone(guards.value_problem("DECISYA_LAN_SUBNET", "192.0.2.0/24"))

    def test_a_missing_empty_or_foreign_key_is_refused(self):
        values = dict(FIXTURE_VALUES)
        del values["DECISYA_ID_HOST"]
        values["DECISYA_API_HOST"] = ""
        values["COMPOSE_FILE"] = "x"
        values["COMPOSE_PROFILES"] = "x"
        problems = guards.env_problems(values)
        self.assertTrue(has(problems, "DECISYA_ID_HOST: missing"))
        self.assertTrue(has(problems, "DECISYA_API_HOST: empty"))
        self.assertTrue(has(problems, "COMPOSE_FILE: not allowed"))
        self.assertTrue(has(problems, "COMPOSE_PROFILES: not allowed"))

    def test_the_three_hosts_must_differ(self):
        values = dict(FIXTURE_VALUES, DECISYA_API_HOST=FIXTURE_VALUES["DECISYA_APP_HOST"])
        self.assertTrue(has(guards.env_problems(values), "must all be different"))

    def test_an_allow_list_may_not_overlap_a_committed_docker_subnet(self):
        for key, value in (("DECISYA_LAN_SUBNET", "10.120.0.0/24"), ("DECISYA_LAN_SUBNET", "10.120.1.0/24"),
                           ("DECISYA_WORKSTATION_ADDRESS", "10.120.0.2"), ("DECISYA_WORKSTATION_ADDRESS", "10.120.1.2/32")):
            with self.subTest(key=key, value=value):
                problems = guards.env_problems(dict(FIXTURE_VALUES, **{key: value}))
                self.assertTrue(has(problems, "overlaps the Docker network"))

    def test_the_bind_address_may_not_sit_inside_a_committed_subnet(self):
        problems = guards.env_problems(dict(FIXTURE_VALUES, DECISYA_BIND_ADDRESS="10.120.0.2"))
        self.assertTrue(has(problems, "committed"))

    def test_overlap_with_the_hosts_docker_networks_names_the_network_only(self):
        values = dict(FIXTURE_VALUES)
        problems = guards.overlap_problems(values, [("bridge", "198.51.100.0/25"), ("other", "203.0.113.0/24")])
        self.assertEqual(problems, ["DECISYA_LAN_SUBNET overlaps the Docker network bridge"])
        self.assertEqual(guards.overlap_problems(values, [("v6", "2001:db8::/32")]), [])

    def test_no_problem_text_repeats_a_value(self):
        values = dict(FIXTURE_VALUES, DECISYA_LAN_SUBNET=ip(8, 8, 8, 0) + "/24", DECISYA_BIND_ADDRESS="0.0.0.0")
        text = "\n".join(guards.env_problems(values))
        self.assertNotIn(ip(8, 8, 8, 0), text)

    def test_env_file_parsing(self):
        values, problems = guards.parse_env_text("# c\n\nA=1\nA=2\nB='x'\nnot a pair\n1BAD=x\nC=3\n")
        self.assertEqual(values, {"A": "1", "C": "3"})
        self.assertEqual(len(problems), 4)
        self.assertTrue(has(problems, "duplicate key A"))
        self.assertTrue(has(problems, "quotes"))


class ScramTests(unittest.TestCase):
    def test_verifier_shape_and_determinism(self):
        salt = b"0123456789abcdef"
        verifier = stackctl.scram_verifier("pencil", salt=salt)
        self.assertRegex(verifier, r"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+$")
        self.assertEqual(verifier, stackctl.scram_verifier("pencil", salt=salt))
        self.assertNotEqual(verifier, stackctl.scram_verifier("pencil2", salt=salt))
        self.assertNotEqual(stackctl.scram_verifier("pencil"), stackctl.scram_verifier("pencil"))  # random salt

    def test_verifier_checks_out_the_scram_proof_flow(self):
        salt, password = b"0123456789abcdef", "pencil"
        verifier = stackctl.scram_verifier(password, salt=salt)
        _, rest = verifier.split("$", 1)
        iterations, rest = rest.split(":", 1)
        salt_b64, keys = rest.split("$", 1)
        stored_b64, server_b64 = keys.split(":", 1)
        self.assertEqual(base64.b64decode(salt_b64), salt)
        salted = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, int(iterations))
        client_key = hmac.new(salted, b"Client Key", hashlib.sha256).digest()
        auth_message = b"n=user,r=nonce,r=nonce+server,s=salt,i=4096,c=biws,r=nonce+server"
        signature = hmac.new(base64.b64decode(stored_b64), auth_message, hashlib.sha256).digest()
        proof = bytes(a ^ b for a, b in zip(client_key, signature))
        recovered = bytes(a ^ b for a, b in zip(proof, signature))
        self.assertEqual(hashlib.sha256(recovered).digest(), base64.b64decode(stored_b64))
        self.assertEqual(hmac.new(salted, b"Server Key", hashlib.sha256).digest(), base64.b64decode(server_b64))

    def test_sql_is_built_only_from_a_known_role_and_a_verifier(self):
        verifier = stackctl.scram_verifier("x" * 40, salt=b"0123456789abcdef")
        sql = stackctl.alter_role_sql("keycloak", verifier)
        self.assertIn("SET log_statement = 'none';", sql)
        self.assertIn("SET log_min_error_statement = 'panic';", sql)
        self.assertIn('ALTER ROLE "keycloak" PASSWORD \'SCRAM-SHA-256$4096:', sql)
        for bad_role in ('keycloak"; DROP ROLE x; --', "Keycloak", "a b", ""):
            with self.assertRaises(stackctl.StackError):
                stackctl.alter_role_sql(bad_role, verifier)
        with self.assertRaises(stackctl.StackError):
            stackctl.alter_role_sql("keycloak", verifier + "'; DROP")


class SecretGenerationTests(unittest.TestCase):
    def test_every_secret_file_is_produced_by_exactly_one_credential(self):
        produced = []
        for logical in stackctl.LOGICAL_SECRETS:
            produced += list(stackctl.files_for(logical, stackctl.generate_credential(logical)))
        # #122: the key ring's certificate pair comes from the BFF image's generator, one credential, four files.
        produced += list(stackctl.files_for(stackctl.DP_LOGICAL, ("QUJD", "p" * 48)))
        self.assertEqual(sorted(produced), sorted(set(produced)), "a file has two generators")
        self.assertEqual(set(produced), set(stackctl.SECRET_FILE_NAMES))
        self.assertEqual(set(stackctl.SECRET_FILE_NAMES), set(guards.ALL_SECRETS))
        self.assertEqual(len(produced), 17)

    def test_credential_forms(self):
        for logical in stackctl.LOGICAL_SECRETS:
            value = stackctl.generate_credential(logical)
            if logical == "user_id_hash_key":
                self.assertEqual(len(base64.b64decode(value, validate=True)), 32)
            else:
                self.assertRegex(value, r"^[A-Za-z0-9]{48}$")
        self.assertNotEqual(stackctl.generate_password(), stackctl.generate_password())

    def test_connection_strings_never_carry_error_detail_or_persisted_security_info(self):
        for logical in ("migrator_db", "tenancy_db", "entitlements_db", "redis"):
            for name, content in stackctl.files_for(logical, "x" * 48).items():
                if name.startswith("ConnectionStrings__"):
                    self.assertNotRegex(content.lower(), r"include error detail|persist security info")
                    self.assertFalse(content.endswith("\n"))

    def test_redis_acl_comes_from_the_one_policy_and_holds_only_a_hash(self):
        password = "p" * 48
        files = stackctl.files_for("redis", password)
        acl = files["redis_acl"]
        self.assertNotIn(password, acl)
        self.assertFalse(acl.endswith("\n"))
        digest = hashlib.sha256(password.encode()).hexdigest()
        lines = acl.split("\n")
        self.assertEqual(lines, [line.format(bff_hash=digest) for line in stackctl.REDIS_ACL_POLICY])
        self.assertTrue(lines[0].startswith("user default off resetpass"))
        self.assertIn("user health on nopass -@all +ping", lines)
        self.assertEqual(files["ConnectionStrings__redis"], "redis:6379,user=decisya-bff,password=" + password)


class AssembleTests(unittest.TestCase):
    def test_assemble_lays_out_the_stack_with_non_default_names_and_a_manifest(self):
        with scratch_dir() as parent:
            stack = make_stack(parent, secrets_init=False)
            self.assertEqual(sorted(p.name for p in stack.iterdir()),
                             sorted([".env", "MANIFEST.sha256", "config", "images.txt", "secrets", "stack.generated.yaml", "stack.overlay.yaml", "trust"]))
            self.assertFalse(any(stackctl.COMPOSE_DEFAULT_NAME.fullmatch(p.name) for p in stack.iterdir()))
            self.assertEqual((stack / "stack.generated.yaml").read_bytes(), GENERATED.read_bytes())
            manifest = stackctl.parse_manifest((stack / "MANIFEST.sha256").read_text(encoding="utf-8"))
            self.assertEqual(set(manifest), {dest for _, dest in stackctl.ASSEMBLE_MAP})
            for dest, digest in manifest.items():
                self.assertEqual(digest, hashlib.sha256((stack / dest).read_bytes()).hexdigest())
            self.assertTrue((stack / "config" / "caddy" / "Caddyfile").is_file())
            # #121: exactly the production realm, its password list and the identity check join the wrapper
            # in config/keycloak. The dev realm and the SQL migrations are never copied.
            self.assertEqual(sorted(p.name for p in (stack / "config" / "keycloak").iterdir()),
                             ["common-passwords.txt", "entrypoint-stack.sh", "identity-check.sql", "realm-decisya.json"])
            self.assertEqual([p.name for p in (stack / "config").rglob("*") if "realm" in p.name], ["realm-decisya.json"])
            self.assertEqual((stack / "config" / "keycloak" / "realm-decisya.json").read_bytes(),
                             (ROOT / "deploy" / "keycloak" / "production" / "realm-decisya.json").read_bytes())

    def test_the_environment_template_is_copied_once_and_never_overwritten(self):
        with scratch_dir() as parent:
            stack = make_stack(parent, secrets_init=False)
            (stack / ".env").write_text("MARKER=1\n", encoding="utf-8")
            code, _ = run(["assemble", "--stack", str(stack), "--published", str(GENERATED), "--repo", str(ROOT)])
            self.assertEqual(code, 0)
            self.assertEqual((stack / ".env").read_text(encoding="utf-8"), "MARKER=1\n")

    def test_a_second_assemble_keeps_the_secrets(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            before = read_secrets(stack)
            code, _ = run(["assemble", "--stack", str(stack), "--published", str(GENERATED), "--repo", str(ROOT)])
            self.assertEqual(code, 0)
            self.assertEqual(read_secrets(stack), before)

    def test_drift_between_published_and_committed_stops_assemble(self):
        with scratch_dir() as parent:
            drifted = parent / "published.yaml"
            drifted.write_bytes(GENERATED.read_bytes() + b"\n# drift\n")
            stack = parent / "stack"
            code, output = run(["assemble", "--stack", str(stack), "--published", str(drifted), "--repo", str(ROOT)])
            self.assertEqual(code, 2)
            self.assertIn("drift", output)
            self.assertFalse(stack.exists())

    @unittest.skipUnless((ROOT / ".git").exists(), "needs a Git working tree")
    def test_a_stack_folder_inside_a_git_tree_is_refused(self):
        inside = ROOT / "deploy" / "tests" / "_never_created_stack"
        self.assertTrue(stackctl.location_problems(inside))
        code, output = run(["assemble", "--stack", str(inside), "--published", str(GENERATED), "--repo", str(ROOT)])
        self.assertEqual(code, 2)
        self.assertIn("Git working tree", output)
        self.assertFalse(inside.exists())

    @unittest.skipUnless(POSIX, "POSIX modes")
    def test_modes_on_a_posix_host(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            self.assertEqual(os.stat(stack / "secrets").st_mode & 0o777, 0o700)
            self.assertEqual(os.stat(stack / ".env").st_mode & 0o777, 0o600)
            for name in stackctl.SECRET_FILE_NAMES:
                self.assertEqual(os.stat(stack / "secrets" / name).st_mode & 0o777, 0o444)
            # Postgres runs an executable init script instead of sourcing it (an `exit` would end the entrypoint).
            self.assertTrue(os.stat(stack / "config" / "postgres" / "init" / "20-decisya-db.sh").st_mode & 0o111)

    def test_every_init_script_and_mounted_file_is_in_the_assemble_map(self):
        sources = {source for source, _ in stackctl.ASSEMBLE_MAP}
        for path in (ROOT / "deploy" / "postgres" / "init").glob("*"):
            self.assertIn(path.relative_to(ROOT).as_posix(), sources, "an init script that assemble would not copy")
        for source in sources:
            self.assertTrue((ROOT / source).is_file(), source)
        # #121: the one production realm file, and no other realm file, is assembled.
        self.assertEqual([s for s in sources if "realm" in s], ["deploy/keycloak/production/realm-decisya.json"])
        for source in sources:
            self.assertFalse(source.startswith("deploy/keycloak/") and source.count("/") == 2 and source.endswith(".json"),
                             "a file straight in deploy/keycloak (the dev realm folder) is never assembled")


class SecretsCommandTests(unittest.TestCase):
    def test_init_writes_every_file_in_the_right_shape(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            secrets = read_secrets(stack)
            self.assertEqual(set(secrets), set(stackctl.SECRET_FILE_NAMES))
            for name, text in secrets.items():
                self.assertFalse(text.endswith(("\n", "\r")), name)
            self.assertEqual(stackctl.secret_problems(stack / "secrets"), [])

    def test_init_refuses_when_any_target_exists_and_changes_nothing(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            before = read_secrets(stack)
            code, output = run(["secrets", "init", "--stack", str(stack)])
            self.assertEqual(code, 2)
            self.assertIn("refusing to overwrite", output)
            self.assertEqual(read_secrets(stack), before)

    def test_init_refuses_when_one_target_exists(self):
        with scratch_dir() as parent:
            stack = make_stack(parent, secrets_init=False)
            (stack / "secrets" / "redis_acl").write_text("x", encoding="utf-8")
            code, _ = run(["secrets", "init", "--stack", str(stack)])
            self.assertEqual(code, 2)
            self.assertEqual([p.name for p in (stack / "secrets").iterdir()], ["redis_acl"])

    def test_rotate_replaces_one_credential_and_its_derived_files_only(self):
        for logical, changed in (
            ("tenancy_db", {"Migrator__TenancyRolePassword", "ConnectionStrings__tenancy"}),
            ("entitlements_db", {"Migrator__EntitlementsRolePassword", "ConnectionStrings__entitlements"}),
            ("redis", {"redis_acl", "ConnectionStrings__redis"}),
            ("bff_client", {"Bff__Oidc__ClientSecret"}),
            ("user_id_hash_key", {"Decisya__Observability__UserIdHashKey"}),
            ("keycloak_bootstrap", {"keycloak_bootstrap_admin_password"}),
        ):
            with self.subTest(logical=logical), scratch_dir() as parent:
                stack = make_stack(parent)
                before = read_secrets(stack)
                code, _ = run(["secrets", "rotate", logical, "--stack", str(stack)])
                self.assertEqual(code, 0)
                after = read_secrets(stack)
                self.assertEqual({n for n in after if after[n] != before[n]}, changed)
                self.assertEqual(stackctl.secret_problems(stack / "secrets"), [], "derived files must stay in step")
                self.assertEqual(sorted(p.name for p in (stack / "secrets").iterdir()), sorted(stackctl.SECRET_FILE_NAMES), "no .old or temporary copy")

    def test_a_database_role_needs_an_explicit_choice(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            before = read_secrets(stack)
            for logical in ("postgres_superuser", "migrator_db", "keycloak_db"):
                code, output = run(["secrets", "rotate", logical, "--stack", str(stack)])
                self.assertEqual(code, 2, logical)
                self.assertIn("--apply-db", output)
            code, _ = run(["secrets", "rotate", "tenancy_db", "--apply-db", "--stack", str(stack)])
            self.assertEqual(code, 2)
            self.assertEqual(read_secrets(stack), before)

    def test_apply_db_sends_a_verifier_over_stdin_and_never_the_password_in_argv(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            fake = FakeRunner()
            code, _ = run(["secrets", "rotate", "keycloak_db", "--apply-db", "--stack", str(stack)], runner_factory=lambda s: fake)
            self.assertEqual(code, 0)
            args, sql = fake.calls[0]
            self.assertEqual(args[:4], ["exec", "-T", "postgres", "psql"])
            password = read_secrets(stack)["keycloak_db_password"]
            self.assertNotIn(password, " ".join(args))
            self.assertNotIn(password, sql)
            self.assertIn('ALTER ROLE "keycloak" PASSWORD \'SCRAM-SHA-256$4096:', sql)
            self.assertIn("SET log_min_error_statement = 'panic';", sql)

    def test_a_failed_alter_role_leaves_every_file_unchanged(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            before = read_secrets(stack)
            code, output = run(["secrets", "rotate", "migrator_db", "--apply-db", "--stack", str(stack)], runner_factory=lambda s: FakeRunner(returncode=1))
            self.assertEqual(code, 2)
            self.assertIn("no file was changed", output)
            self.assertEqual(read_secrets(stack), before)

    def test_rotate_only_replaces_and_never_creates(self):
        with scratch_dir() as parent:
            stack = make_stack(parent, secrets_init=False)
            code, output = run(["secrets", "rotate", "redis", "--stack", str(stack)])
            self.assertEqual(code, 2)
            self.assertEqual(list((stack / "secrets").iterdir()), [])

    def test_retire_empties_the_bootstrap_password_and_the_check_accepts_it(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            code, _ = run(["secrets", "retire", "keycloak_bootstrap", "--stack", str(stack)])
            self.assertEqual(code, 0)
            self.assertEqual((stack / "secrets" / "keycloak_bootstrap_admin_password").read_bytes(), b"")
            self.assertEqual(stackctl.secret_problems(stack / "secrets"), [])

    def test_a_missing_or_malformed_secret_file_is_flagged(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            secrets = stack / "secrets"
            unlink_force(secrets / "redis_acl")
            write_force(secrets / "keycloak_db_password", b"short")
            write_force(secrets / "Decisya__Observability__UserIdHashKey", b"not base64!")
            write_force(secrets / "ConnectionStrings__tenancy", ("Host=postgres;Port=5432;Database=decisya;Username=decisya_tenancy;Password=" + "a" * 48 + ";Include Error Detail=true").encode())
            write_force(secrets / "Bff__Oidc__ClientSecret", ("a" * 48 + "\n").encode())
            write_force(secrets / "stray", b"x")
            problems = stackctl.secret_problems(secrets)
            for fragment in ("secret redis_acl: missing", "secret keycloak_db_password: must be at least 32",
                             "secret Decisya__Observability__UserIdHashKey: must be base64", "secret ConnectionStrings__tenancy: must be the generated",
                             "secret Bff__Oidc__ClientSecret: ends with a newline", "unexpected entry stray"):
                self.assertTrue(has(problems, fragment), fragment)

    def test_a_half_finished_rotation_is_detected(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            secrets = stack / "secrets"
            write_force(secrets / "ConnectionStrings__tenancy", stackctl.connection_string("decisya_tenancy", "z" * 48).encode())
            write_force(secrets / "ConnectionStrings__redis", stackctl.redis_connection_string("y" * 48).encode())
            problems = stackctl.secret_problems(secrets)
            self.assertTrue(has(problems, "ConnectionStrings__tenancy does not carry"))
            self.assertTrue(has(problems, "redis_acl does not carry the hash"))

    @unittest.skipUnless(POSIX, "POSIX modes")
    def test_a_world_writable_secrets_folder_or_file_is_flagged(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            os.chmod(stack / "secrets", 0o777)
            self.assertTrue(has(stackctl.secret_problems(stack / "secrets"), "mode 0700"))
            os.chmod(stack / "secrets", 0o700)
            os.chmod(stack / "secrets" / "redis_acl", 0o666)
            self.assertTrue(has(stackctl.secret_problems(stack / "secrets"), "writable by group or others"))


class StaticCheckTests(unittest.TestCase):
    def rewrite_env(self, stack, **changes):
        values = dict(FIXTURE_VALUES)
        for key, value in changes.items():
            if value is None:
                values.pop(key, None)
            else:
                values[key] = value
        (stack / ".env").write_text(env_text(values), encoding="utf-8")

    def test_a_fresh_stack_has_no_static_problem(self):
        with scratch_dir() as parent:
            self.assertEqual(problems_of(make_stack(parent)), [])

    def test_each_defect_is_named(self):
        def tamper_overlay(stack):
            (stack / "stack.overlay.yaml").write_text((stack / "stack.overlay.yaml").read_text(encoding="utf-8") + "\n# edited\n", encoding="utf-8")

        def tamper_caddyfile(stack):
            path = stack / "config" / "caddy" / "Caddyfile"
            path.write_text(path.read_text(encoding="utf-8") + "\n# edited\n", encoding="utf-8")

        def default_name(stack):
            (stack / "docker-compose.yaml").write_text("services: {}\n", encoding="utf-8")

        def extra_override(stack):
            (stack / "compose.override.yaml").write_text("services: {}\n", encoding="utf-8")

        def compose_file_key(stack):
            with open(stack / ".env", "a", encoding="utf-8") as handle:
                handle.write("COMPOSE_FILE=docker-compose.yaml\n")

        def other_digest(stack):
            values = dict(FIXTURE_VALUES, DECISYA_BFF_IMAGE="ghcr.io/mpcs2013/decisya-bff@sha256:" + "d" * 64)
            (stack / "images.txt").write_text(images_txt(values), encoding="utf-8")

        def extra_config(stack):
            (stack / "config" / "extra.sh").write_text("echo\n", encoding="utf-8")

        def key_in_trust(stack):
            (stack / "trust" / "root.key").write_text("-----BEGIN PRIVATE KEY-----\n", encoding="utf-8")

        def two_certs(stack):
            (stack / "trust" / "caddy-root.crt").write_text("-----BEGIN CERTIFICATE-----\nAA\n-----END CERTIFICATE-----\n" * 2, encoding="ascii")

        cases = [
            ("overlay edited after assemble", tamper_overlay, "was changed after assemble"),
            ("Caddyfile edited after assemble", tamper_caddyfile, "was changed after assemble"),
            ("a Compose default name", default_name, "Compose default name"),
            ("a compose override file", extra_override, "Compose default name"),
            ("COMPOSE_FILE in the environment file", compose_file_key, "COMPOSE_FILE: not allowed"),
            ("an image that differs from images.txt", other_digest, "DECISYA_BFF_IMAGE differs from the digest"),
            ("images.txt missing", lambda s: (s / "images.txt").unlink(), "images.txt is missing"),
            ("environment file missing", lambda s: (s / ".env").unlink(), "environment file is missing"),
            ("a port other than 8443", lambda s: self.rewrite_env(s, DECISYA_HTTPS_PORT="8444"), "must be 8443"),
            ("an empty value", lambda s: self.rewrite_env(s, DECISYA_API_HOST=""), "DECISYA_API_HOST: empty"),
            ("a missing key", lambda s: self.rewrite_env(s, DECISYA_ID_HOST=None), "DECISYA_ID_HOST: missing"),
            ("the LAN overlapping a committed Docker network", lambda s: self.rewrite_env(s, DECISYA_LAN_SUBNET="10.120.0.0/24"), "overlaps the Docker network"),
            ("a non-digest image", lambda s: self.rewrite_env(s, DECISYA_API_IMAGE="ghcr.io/mpcs2013/decisya-api:latest"), "DECISYA_API_IMAGE: must be"),
            ("a wide workstation", lambda s: self.rewrite_env(s, DECISYA_WORKSTATION_ADDRESS="192.0.2.0/24"), "single host"),
            ("an unexpected file", lambda s: (s / "notes.txt").write_text("x", encoding="utf-8"), "unexpected file notes.txt"),
            ("an extra file under config", extra_config, "unexpected file config/extra.sh"),
            ("a key file in trust", key_in_trust, "unexpected entry root.key"),
            ("two certificates in trust", two_certs, "exactly one certificate"),
            ("a missing secret", lambda s: unlink_force(s / "secrets" / "redis_acl"), "secret redis_acl: missing"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label), scratch_dir() as parent:
                stack = make_stack(parent)
                mutate(stack)
                problems = problems_of(stack)
                self.assertTrue(has(problems, fragment), "%s: expected %r in %s" % (label, fragment, problems))

    def test_a_compose_variable_in_the_shell_is_refused(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            with mock.patch.dict(os.environ, {"COMPOSE_PROFILES": "x"}):
                problems = stackctl.static_problems(stack, ROOT)
            self.assertTrue(has(problems, "COMPOSE_* variables are set in the shell"))

    def test_the_runner_strips_compose_and_stack_variables_from_the_environment(self):
        keep = {"PATH": os.environ.get("PATH", ""), "COMPOSE_FILE": "x", "COMPOSE_PROFILES": "y", "DECISYA_APP_HOST": "z", "OTHER": "kept"}
        with mock.patch.dict(os.environ, keep):
            environment = stackctl.Runner(Path(".")).environment()
        self.assertEqual({k for k in environment if k.startswith(("COMPOSE_", "DECISYA_"))}, set())
        self.assertEqual(environment["OTHER"], "kept")

    def test_the_runner_always_passes_both_files_a_project_directory_and_the_environment_file(self):
        argv = stackctl.Runner(Path("/s")).base()
        self.assertEqual(argv.count("-f"), 2)
        self.assertEqual(argv[argv.index("-f") + 1].replace("\\", "/"), "/s/" + stackctl.GENERATED_NAME)
        self.assertIn("--project-directory", argv)
        self.assertIn("--env-file", argv)

    def test_docker_checks_are_skipped_with_a_clear_message_when_the_environment_file_is_wrong(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            self.rewrite_env(stack, DECISYA_API_HOST="")
            problems = stackctl.docker_problems(stack, ROOT, FakeRunner())
            self.assertEqual(problems, ["Docker checks skipped: fix the environment file first"])


class _VerifyRunner(FakeRunner):
    """A FakeRunner with no running containers and a given list of Docker networks."""

    def __init__(self, subnets):
        super().__init__()
        self.subnets = subnets

    def inspect_project(self):
        return []

    def docker_subnets(self):
        return self.subnets


class VerifyOverlapTests(unittest.TestCase):
    def test_verify_rechecks_the_docker_network_overlap_and_names_only_the_network(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            # The LAN fixture value is 198.51.100.0/24 (documentation range); the network "edge" sits inside it.
            overlapping = _VerifyRunner([("edge", "198.51.100.0/25")])
            problems = stackctl.verify_problems(stack, overlapping)
            self.assertTrue(has(problems, "DECISYA_LAN_SUBNET overlaps the Docker network edge"), problems)
            self.assertFalse(has(problems, "198.51.100"), problems)
            clear = stackctl.verify_problems(stack, _VerifyRunner([("edge", "203.0.113.0/24")]))
            self.assertFalse(has(clear, "overlaps the Docker network"), clear)


class FileContentTests(unittest.TestCase):
    def test_redis_conf_documents_the_one_acl_policy(self):
        text = (ROOT / "deploy" / "redis" / "redis.conf").read_text(encoding="utf-8")
        comment = " ".join(re.sub(r"^#\s?", "", line).strip() for line in text.splitlines() if line.startswith("#"))
        comment = re.sub(r"\s+", " ", comment)
        for line in stackctl.REDIS_ACL_POLICY:
            documented = line.replace("{bff_hash}", "<sha256 of the password>")
            self.assertIn(documented, comment, "redis.conf and REDIS_ACL_POLICY disagree")

    def test_redis_conf_directives(self):
        code = [l.strip() for l in (ROOT / "deploy" / "redis" / "redis.conf").read_text(encoding="utf-8").splitlines()
                if l.strip() and not l.strip().startswith("#")]
        for directive in ("aclfile /run/secrets/redis_acl", "protected-mode yes", 'save ""', "appendonly no", "maxmemory 64mb", "maxmemory-policy noeviction"):
            self.assertIn(directive, code)
        self.assertFalse([l for l in code if l.startswith(("requirepass", "user ", "masterauth"))])

    def test_the_environment_template_is_all_keys_with_empty_values(self):
        path = COMPOSE_DIR / "stack.env.example"
        keys = []
        for line in path.read_text(encoding="utf-8").splitlines():
            if not line.strip() or line.startswith("#"):
                continue
            self.assertRegex(line, r"^[A-Z0-9_]+=$", "every value must be empty")
            keys.append(line.rstrip("="))
        self.assertEqual(sorted(keys), sorted(guards.ENV_KEYS))
        self.assertEqual(len(keys), len(set(keys)))

    def test_no_file_in_the_compose_folder_has_an_env_style_name(self):
        self.assertEqual([p.name for p in COMPOSE_DIR.iterdir() if p.name.startswith(".env")], [])

    def test_both_init_scripts_keep_the_password_out_of_argv_and_the_log(self):
        init_dir = ROOT / "deploy" / "postgres" / "init"
        expected_scripts = (
            "20-decisya-db.sh",
            "10-keycloak-db.sh",
        )
        present = sorted(p.name for p in init_dir.glob("*.sh"))
        for name in expected_scripts:
            self.assertIn(name, present)
        for name in sorted(expected_scripts):
            text = (init_dir / name).read_text(encoding="utf-8")
            with self.subTest(script=name):
                self.assertIn("\\getenv", text)
                self.assertIn("SET log_statement = 'none';", text)
                self.assertIn("SET log_min_error_statement = 'panic';", text)
                code = [l for l in text.splitlines() if not l.strip().startswith(("#", "--"))]
                self.assertFalse([l for l in code if re.search(r"\b(echo|printf)\b|set -x", l)], "the script must print nothing")

    def test_the_collector_exports_nowhere_and_stays_at_basic_verbosity(self):
        text = (ROOT / "deploy" / "otel-collector" / "config.yaml").read_text(encoding="utf-8")
        block = re.search(r"^exporters:\n((?:[ \t]+.*\n|\n)*)", text, re.MULTILINE).group(1)
        self.assertEqual(re.findall(r"^  (\w[\w/-]*):", block, re.MULTILINE), ["debug"])
        self.assertEqual(set(re.findall(r"verbosity:\s*(\w+)", text)), {"basic"})
        pipelines = re.findall(r"processors:\s*\[([^\]]*)\]", text)
        self.assertEqual(len(pipelines), 3)
        for processors in pipelines:
            self.assertTrue(processors.split(",")[0].strip() == "memory_limiter", "memory_limiter must be the first processor")

    def test_the_image_scan_covers_caddy_and_the_collector(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location("_scan", ROOT / ".github" / "scripts" / "image_scan.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertEqual(module.ALIASES, ("postgres", "keycloak", "redis", "caddy", "otelcollector"))
        self.assertEqual(set(module.EXCEPTION_IMAGES) - set(module.ALIASES), {"api", "bff", "migrator"})
        refs = module.parse_targets((ROOT / "src" / "Decisya.AppHost" / "ContainerImages.cs").read_text(encoding="utf-8"))
        self.assertEqual(set(refs), set(module.ALIASES))

    def test_the_libc6_exceptions_on_the_request_path_expire_in_30_days(self):
        import json
        data = json.loads((ROOT / ".github" / "image-scan" / "exceptions.json").read_text(encoding="utf-8"))
        by_image = {e["image"]: e for e in data["exceptions"] if e["id"] == "CVE-2016-20013"}
        self.assertEqual(by_image["api"]["expires"], "2026-11-04")
        self.assertEqual(by_image["bff"]["expires"], "2026-11-04")
        self.assertEqual(by_image["migrator"]["expires"], "2026-12-31")
        for entry in data["exceptions"]:
            if entry["image"] in ("postgres", "redis", "keycloak"):
                self.assertLessEqual(entry["expires"], "2026-11-02")
                self.assertNotIn("dev-only", entry["justification"], "the justification must describe the deployed context")


class CanaryTests(unittest.TestCase):
    """No secret value, and no value derived from one, is ever printed by any command."""

    @staticmethod
    def sensitive(stack: Path) -> set:
        found = set()
        for text in read_secrets(stack).values():
            if len(text) >= 16:
                found.add(text)
            match = re.search(r"[Pp]assword=([A-Za-z0-9]+)$", text)
            if match:
                found.add(match.group(1))
            found.update(re.findall(r"#([0-9a-f]{64})", text))
            for line in text.split("\n"):
                if len(line) >= 16:
                    found.add(line)
        return found

    def test_no_command_prints_a_secret_value(self):
        canary = "CANARY-NOT-A-REAL-SECRET-0123456789abcdef"
        output = []
        sensitive = set()
        with scratch_dir() as parent, clean_environment():
            stack = parent / "stack"
            code, text = run(["assemble", "--stack", str(stack), "--published", str(GENERATED), "--repo", str(ROOT)])
            output.append(text)
            # The certificate generator needs the BFF image from the environment file; Docker is faked.
            (stack / "images.txt").write_text(images_txt(FIXTURE_VALUES), encoding="utf-8")
            (stack / ".env").write_text(env_text(FIXTURE_VALUES), encoding="utf-8")
            for argv, factory in (
                (["secrets", "init", "--stack", str(stack)], lambda s: FakeRunner()),
                (["secrets", "init", "--stack", str(stack)], lambda s: FakeRunner()),  # refused
                (["check", "--stack", str(stack), "--static-only", "--repo", str(ROOT)], None),
            ):
                code, text = run(argv, runner_factory=factory)
                output.append(text)
                if argv[0] == "secrets" and "init" in argv and (stack / "secrets").is_dir():
                    sensitive |= self.sensitive(stack)
            for argv, factory in (
                (["secrets", "rotate", "tenancy_db", "--stack", str(stack)], None),
                (["secrets", "rotate", "redis", "--stack", str(stack)], None),
                (["secrets", "rotate", "user_id_hash_key", "--stack", str(stack)], None),
                (["secrets", "rotate", "keycloak_db", "--files-only", "--stack", str(stack)], None),
                (["secrets", "rotate", "keycloak_db", "--apply-db", "--stack", str(stack)], lambda s: FakeRunner()),
                (["secrets", "rotate", "migrator_db", "--apply-db", "--stack", str(stack)], lambda s: FakeRunner(returncode=1)),
                (["secrets", "rotate", "migrator_db", "--stack", str(stack)], None),  # refused: no flag
                (["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], lambda s: FakeRunner()),
                (["secrets", "rotate", "dataprotection_cert", "--stack", str(stack)], lambda s: FakeRunner()),  # refused: previous pair not empty
                (["secrets", "add", "dataprotection_cert", "--stack", str(stack)], lambda s: FakeRunner()),  # refused: files exist
                (["secrets", "retire", "dataprotection_previous", "--stack", str(stack)], None),
                (["secrets", "retire", "keycloak_bootstrap", "--stack", str(stack)], None),
                (["check", "--stack", str(stack), "--static-only", "--repo", str(ROOT)], None),
            ):
                before = self.sensitive(stack)
                code, text = run(argv, runner_factory=factory)
                output.append(text)
                sensitive |= before | self.sensitive(stack)
            # A malformed file whose content is a canary: the problem names the file, never the content.
            write_force(stack / "secrets" / "Bff__Oidc__ClientSecret", canary.encode())
            write_force(stack / "secrets" / "keycloak_db_password", (canary + "\n").encode())
            code, text = run(["check", "--stack", str(stack), "--static-only", "--repo", str(ROOT)])
            self.assertEqual(code, 1)
            output.append(text)
            sensitive.add(canary)
            # And through a real process, as an operator runs it.
            proc = subprocess.run([sys.executable, str(COMPOSE_DIR / "stackctl.py"), "check", "--stack", str(stack), "--static-only", "--repo", str(ROOT)],
                                  capture_output=True, text=True, encoding="utf-8", errors="replace")
            output.append(proc.stdout + proc.stderr)
        joined = "\n".join(output)
        self.assertGreater(len(sensitive), 10, "the canary must have real values to look for")
        for value in sensitive:
            self.assertNotIn(value, joined, "a secret value reached the output")

    def test_problem_messages_name_files_never_values(self):
        with scratch_dir() as parent:
            stack = make_stack(parent)
            write_force(stack / "secrets" / "Bff__Oidc__ClientSecret", b"CANARY-VALUE-xyz")
            problems = stackctl.secret_problems(stack / "secrets")
            self.assertTrue(problems)
            self.assertNotIn("CANARY-VALUE-xyz", "\n".join(problems))


if __name__ == "__main__":
    unittest.main()
