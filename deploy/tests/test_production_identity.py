"""The identity check, `stackctl.py verify` and `stackctl.py realm rebuild` (issue #121, G2 D2 and D5,
G3 G4-121-03 d, G4-121-05).

No Docker needed: Docker is replaced by a scripted runner that records every call, SQL on stdin
included. The decisions under test are the ones that keep a real account from appearing unnoticed
(the C-02 trip-wire), keep a rebuild from destroying one, and keep the output free of names and secrets.

Every rule has a red case: a check that cannot fail is not a check.
"""
from __future__ import annotations

import re
import subprocess
import unittest

from deploy_common import (
    ROOT, ScriptedRunner, clean_environment, completed, guards, make_stack, quiet, scratch_dir, stackctl, unlink_force,
)

SQL_PATH = ROOT / "deploy" / "keycloak" / "production" / "identity-check.sql"

# A healthy Phase 0 realm: imported, hardened, no users, both C-02 switches off.
GOOD = {
    "realm_present": True, "ssl_required_all": True, "events_enabled": True, "admin_events_enabled": True,
    "admin_events_details_off": True, "events_expiration_ok": True, "admin_events_expiration_ok": True,
    "jboss_logging_listener": False, "browser_flow_ok": True, "level_2_admin_conditional": True,
    "level_2_tenant_conditional": False, "password_policy_ok": True, "breached_list_in_policy": False,
    "users_total": 0, "users_without_synthetic": 0, "master_users_without_otp": 0,
    "bff_secret_unresolved": False, "bff_secret_short": False, "bff_redirect_unresolved": False,
    "bff_logout_uris_unresolved": False,
}


def facts(**changes) -> dict:
    values = dict(GOOD)
    values.update(changes)
    return values


def output(values: dict, *, tags: bool = False, newline: str = "\n") -> str:
    """What psql prints for these facts, in the SQL's order."""
    lines = []
    for key in guards.IDENTITY_KEYS:
        value = values[key]
        lines.append("%s=%s" % (key, ("true" if value else "false") if isinstance(value, bool) else value))
    if tags:
        lines = ["BEGIN"] + lines + ["ROLLBACK"]
    return newline.join(lines) + newline


def problems_text(problems) -> str:
    return "\n".join(problems)


def has(problems, fragment) -> bool:
    return any(fragment in p for p in problems)


def is_identity_call(sql) -> bool:
    return sql is not None and "SELECT t.line" in sql


def answering(identity, *, rebuild_code: int = 0, stop_code: int = 0, up_code: int = 0):
    """A scripted Docker. `identity` is a CompletedProcess or an exception for the identity check."""
    def answer(args, sql):
        if "psql" in args:
            if is_identity_call(sql):
                return identity
            return completed("", rebuild_code)
        if args[:1] == ["stop"]:
            return completed("", stop_code)
        if args[:1] == ["up"]:
            return completed("", up_code)
        return completed("", 0)
    return answer


def secret_values(stack) -> set:
    found = set()
    for path in (stack / "secrets").iterdir():
        for part in [path.read_text(encoding="utf-8")] + path.read_text(encoding="utf-8").split("\n"):
            if len(part) >= 16:
                found.add(part)
    return found


class ParserTests(unittest.TestCase):
    def test_the_table_has_the_twenty_keys_of_the_check(self):
        self.assertEqual(len(guards.IDENTITY_KEYS), 20)
        self.assertEqual(set(guards.IDENTITY_KEYS), set(GOOD))

    def test_good_output_parses_with_or_without_the_transaction_tags(self):
        for text in (output(GOOD), output(GOOD, tags=True), output(GOOD, newline="\r\n"), "\n" + output(GOOD) + "\n\n"):
            with self.subTest(text=text[:12]):
                parsed, reason = guards.parse_identity_output(text)
                self.assertIsNone(reason)
                self.assertEqual(parsed, GOOD)

    def test_flags_are_booleans_and_counts_are_integers(self):
        parsed, _ = guards.parse_identity_output(output(facts(users_total=7, users_without_synthetic=3, realm_present=False)))
        self.assertIs(parsed["realm_present"], False)
        self.assertEqual(parsed["users_total"], 7)
        self.assertEqual(parsed["users_without_synthetic"], 3)

    def test_unreadable_output_is_refused_without_repeating_it(self):
        canary = "CANARY-alice@example.test"
        good = output(GOOD)
        cases = {
            "empty": "",
            "a missing key": good.replace("users_total=0\n", ""),
            "an unknown key": good + "users_secret=1\n",
            "a duplicate key": good + "users_total=0\n",
            "a line that is not key=value": good + canary + "\n",
            "a name as a value": good.replace("users_total=0", "users_total=" + canary),
            "a password pair": good + "password=" + canary + "\n",
            "a flag where a count belongs": good.replace("users_total=0", "users_total=true"),
            "a number where a flag belongs": good.replace("realm_present=true", "realm_present=1"),
            "a negative count": good.replace("users_total=0", "users_total=-1"),
            "a leading zero": good.replace("users_total=0", "users_total=00"),
            "a huge count": good.replace("users_total=0", "users_total=" + "9" * 12),
            "a psql error": "ERROR:  relation \"x\" does not exist\n",
            "key with spaces": good.replace("realm_present=true", "realm_present = true"),
            "upper case": good.replace("realm_present=true", "realm_present=TRUE"),
        }
        for label, text in cases.items():
            with self.subTest(case=label):
                parsed, reason = guards.parse_identity_output(text)
                self.assertIsNone(parsed)
                self.assertTrue(reason)
                self.assertNotIn("CANARY", reason)
                self.assertNotIn("alice", reason)
                self.assertNotIn("relation", reason)


class DecisionTests(unittest.TestCase):
    def decide(self, retired: bool = False, **changes):
        return guards.identity_problems(facts(**changes), bootstrap_retired=retired)

    def test_a_healthy_empty_realm_has_no_problem(self):
        self.assertEqual(self.decide(), [])
        self.assertEqual(self.decide(retired=True), [])

    def test_each_hardening_flag_can_fail(self):
        cases = (
            ("ssl_required_all", False, "require TLS"),
            ("events_enabled", False, "login events are off"),
            ("admin_events_enabled", False, "admin events are off"),
            ("admin_events_details_off", False, "admin event details are on"),
            ("events_expiration_ok", False, "login event retention"),
            ("admin_events_expiration_ok", False, "admin event retention"),
            ("jboss_logging_listener", True, "jboss-logging"),
            ("browser_flow_ok", False, "browser flow"),
            ("level_2_admin_conditional", False, "admin MFA is off"),
            ("password_policy_ok", False, "password policy"),
        )
        for key, value, fragment in cases:
            with self.subTest(key=key):
                self.assertTrue(has(self.decide(**{key: value}), fragment))

    def test_each_placeholder_flag_can_fail_and_names_the_field_only(self):
        cases = (
            ("bff_secret_unresolved", "unresolved placeholder"),
            ("bff_secret_short", "shorter than 32"),
            ("bff_redirect_unresolved", "redirect URI"),
            ("bff_logout_uris_unresolved", "logout URI"),
        )
        for key, fragment in cases:
            with self.subTest(key=key):
                problems = self.decide(**{key: True})
                self.assertTrue(has(problems, fragment), problems)
                self.assertEqual(len(problems), 1)

    def test_a_missing_realm_is_one_problem_not_twenty(self):
        flags = {k: False for k, kind in guards.IDENTITY_KEYS.items() if kind == "bool"}
        problems = guards.identity_problems(facts(**flags), bootstrap_retired=False)
        self.assertEqual(len(problems), 1)
        self.assertIn("realm decisya is missing", problems[0])

    def test_the_c02_trip_wire(self):
        # A user without synthetic=true is a failure while either switch is off ...
        for tenant, breached in ((False, False), (True, False), (False, True)):
            with self.subTest(tenant=tenant, breached=breached):
                problems = self.decide(users_total=1, users_without_synthetic=1,
                                       level_2_tenant_conditional=tenant, breached_list_in_policy=breached)
                self.assertTrue(has(problems, "1 user(s) without synthetic=true"), problems)
        # ... the count is reported, never a name ...
        problems = self.decide(users_total=5, users_without_synthetic=4)
        self.assertTrue(has(problems, "4 user(s) without synthetic=true"))
        # ... and both switches on, or no such user, passes.
        self.assertEqual(self.decide(users_total=1, users_without_synthetic=1,
                                     level_2_tenant_conditional=True, breached_list_in_policy=True), [])
        self.assertEqual(self.decide(users_total=3, users_without_synthetic=0), [])

    def test_the_master_realm_otp_check_starts_when_the_bootstrap_secret_is_retired(self):
        self.assertEqual(self.decide(retired=False, master_users_without_otp=2), [])
        problems = self.decide(retired=True, master_users_without_otp=2)
        self.assertTrue(has(problems, "2 master-realm user(s) or service account(s) without an OTP credential"), problems)
        self.assertEqual(self.decide(retired=True, master_users_without_otp=0), [])
        # It applies even when the decisya realm is missing.
        flags = {k: False for k, kind in guards.IDENTITY_KEYS.items() if kind == "bool"}
        problems = guards.identity_problems(facts(master_users_without_otp=1, **flags), bootstrap_retired=True)
        self.assertEqual(len(problems), 2)

    def test_the_master_realm_count_covers_service_accounts_and_prints_the_count_only(self):
        # F-01: the SQL counts every master-realm principal without an OTP credential, service accounts
        # included, so one such account alone is a problem once the bootstrap secret is retired. The
        # message says so and carries the count and nothing else.
        problems = self.decide(retired=True, master_users_without_otp=1)
        self.assertEqual(len(problems), 1, problems)
        self.assertEqual(problems[0], "identity check: 1 master-realm user(s) or service account(s) without an OTP "
                                      "credential after the bootstrap secret was retired")
        self.assertEqual(self.decide(retired=False, master_users_without_otp=1), [])

    def test_problem_text_holds_counts_and_names_of_settings_only(self):
        every = facts(users_total=9, users_without_synthetic=9, master_users_without_otp=9, bff_secret_unresolved=True,
                      bff_secret_short=True, bff_redirect_unresolved=True, bff_logout_uris_unresolved=True)
        text = problems_text(guards.identity_problems(every, bootstrap_retired=True))
        for forbidden in ("@", "://", "password=", "${"):
            self.assertNotIn(forbidden, text)


class SqlStaticTests(unittest.TestCase):
    def setUp(self):
        self.sql = SQL_PATH.read_text(encoding="utf-8")

    def test_the_committed_file_passes_and_prints_exactly_the_parsers_keys(self):
        self.assertEqual(guards.identity_sql_problems(self.sql), [])
        keys = guards._SQL_KEY_RE.findall(self.sql)
        self.assertEqual(keys, list(guards.IDENTITY_KEYS))

    def test_a_comment_may_hold_any_word(self):
        self.assertEqual(guards.identity_sql_problems(self.sql + "\n-- DROP TABLE x; \\copy y\n"), [])

    def test_each_rule_can_fail(self):
        anchor = "realm_present=' ||"
        self.assertIn(anchor, self.sql)

        def inject(expression):
            return self.sql.replace(anchor, anchor + " " + expression + " ||", 1)

        cases = [
            ("a psql meta-command", self.sql.replace("ROLLBACK;", "\\gexec\nROLLBACK;"), "backslash"),
            ("a shell escape on one line", self.sql.replace("ROLLBACK;", "SELECT 1 \\! id;\nROLLBACK;"), "backslash"),
            ("a block comment", self.sql.replace("ROLLBACK;", "/* x */ ROLLBACK;"), "block comments"),
            ("COMMIT instead of ROLLBACK", self.sql.replace("ROLLBACK;", "COMMIT;"), "must be BEGIN TRANSACTION READ ONLY"),
            ("no read-only transaction", self.sql.replace("BEGIN TRANSACTION READ ONLY;", "BEGIN;"), "must be BEGIN TRANSACTION READ ONLY"),
            ("a second statement", self.sql.replace("ROLLBACK;", "SELECT 1;\nROLLBACK;"), "must be BEGIN TRANSACTION READ ONLY"),
            ("a different output column", self.sql.replace("SELECT t.line", "SELECT t.n", 1), "must be BEGIN TRANSACTION READ ONLY"),
            ("a key renamed", self.sql.replace("(20, 'bff_logout_uris_unresolved='", "(20, 'bff_logout_uris_unresolvedX='"), "printed keys differ"),
            ("a key dropped", self.sql.replace("(2, 'ssl_required_all='", "(2, 'x'", 1), "printed keys differ"),
            ("COPY ... PROGRAM", self.sql.replace("ROLLBACK;", "COPY t TO PROGRAM 'x';\nROLLBACK;"), "word copy"),
            ("PROGRAM", self.sql.replace("ROLLBACK;", "COPY t TO PROGRAM 'x';\nROLLBACK;"), "word program"),
            ("DROP", self.sql.replace("ROLLBACK;", "DROP TABLE x;\nROLLBACK;"), "word drop"),
            ("set_config", inject("set_config('a', 'b', false)"), "word set_config"),
            ("lo_export", inject("lo_export(1, 'x')"), "word lo_export"),
            ("pg_read_file", inject("pg_read_file('x')"), "word pg_read_file"),
            ("pg_terminate_backend", inject("pg_terminate_backend(1)"), "word pg_terminate_backend"),
            ("pg_reload_conf", inject("pg_reload_conf()"), "word pg_reload_conf"),
            ("dblink", inject("dblink('x', 'y')"), "word dblink"),
            ("a user name in the output", inject("u.username"), "word username"),
            ("an email in the output", inject("u.email"), "word email"),
        ]
        for label, mutated, fragment in cases:
            with self.subTest(case=label):
                problems = guards.identity_sql_problems(mutated)
                self.assertTrue(has(problems, fragment), "%s: expected %r in %s" % (label, fragment, problems))

    def test_the_sql_text_is_not_a_secret_carrier(self):
        # The file is sent over stdin; nothing in it may look like a credential.
        lowered = self.sql.lower()
        for word in ("password=", "secret=", "token="):
            self.assertNotIn(word, lowered)


class KeycloakMountInspectTests(unittest.TestCase):
    ROOT_DIR = "/srv/stack"

    def container(self, mounts=None) -> dict:
        root = self.ROOT_DIR
        base = [
            {"Type": "bind", "Source": root + "/config/keycloak/entrypoint-stack.sh", "Destination": "/opt/decisya/entrypoint-stack.sh", "RW": False},
            {"Type": "bind", "Source": root + "/config/keycloak/realm-decisya.json", "Destination": guards.REALM_TARGET, "RW": False},
            {"Type": "bind", "Source": root + "/config/keycloak/common-passwords.txt", "Destination": guards.PASSWORD_LIST_TARGET, "RW": False},
            {"Type": "bind", "Source": root + "/trust/caddy-root.crt", "Destination": guards.CADDY_ROOT_TARGET, "RW": False},
            {"Type": "bind", "Source": root + "/secrets/keycloak_db_password", "Destination": "/run/secrets/keycloak_db_password", "RW": False},
            {"Type": "bind", "Source": root + "/secrets/Bff__Oidc__ClientSecret", "Destination": "/run/secrets/Bff__Oidc__ClientSecret", "RW": False},
            {"Type": "tmpfs", "Destination": "/tmp"},
        ]
        return {
            "Config": {"Labels": {"com.docker.compose.project": "decisya", "com.docker.compose.service": "keycloak"}},
            "HostConfig": {"CapDrop": ["ALL"], "SecurityOpt": ["no-new-privileges:true"], "PortBindings": {}},
            "NetworkSettings": {"Networks": {"decisya_idp": {}, "decisya_pg-kc": {}}},
            "Mounts": base if mounts is None else mounts,
        }

    def mutate(self, edit):
        mounts = [dict(m) for m in self.container()["Mounts"]]
        edit(mounts)
        return self.container(mounts)

    @staticmethod
    def find(mounts, destination):
        return next(m for m in mounts if m["Destination"] == destination)

    def test_the_expected_mounts_pass(self):
        self.assertEqual(guards.keycloak_inspect_problems(self.container()), [])

    def test_each_hand_edit_is_caught(self):
        def dev_file(mounts):
            self.find(mounts, guards.REALM_TARGET)["Source"] = self.ROOT_DIR + "/deploy/keycloak/dev-realm.json"

        def folder_instead_of_file(mounts):
            mounts[:] = [m for m in mounts if m["Destination"] != guards.REALM_TARGET]
            mounts.append({"Type": "bind", "Source": self.ROOT_DIR + "/deploy/keycloak", "Destination": guards.IMPORT_DIR, "RW": False})

        def writable_realm(mounts):
            self.find(mounts, guards.REALM_TARGET)["RW"] = True

        def writable_list(mounts):
            self.find(mounts, guards.PASSWORD_LIST_TARGET)["RW"] = True

        def no_list(mounts):
            mounts[:] = [m for m in mounts if m["Destination"] != guards.PASSWORD_LIST_TARGET]

        def no_realm(mounts):
            mounts[:] = [m for m in mounts if m["Destination"] != guards.REALM_TARGET]

        def second_file(mounts):
            mounts.append({"Type": "bind", "Source": self.ROOT_DIR + "/config/keycloak/realm-decisya.json",
                           "Destination": guards.IMPORT_DIR + "/other.json", "RW": False})

        def extra_bind(mounts):
            mounts.append({"Type": "bind", "Source": "/etc", "Destination": "/host-etc", "RW": False})

        def volume_at_target(mounts):
            self.find(mounts, guards.REALM_TARGET)["Type"] = "volume"

        cases = [
            ("the dev realm behind the target", dev_file, "not the assembled one"),
            ("the folder instead of the file", folder_instead_of_file, "exactly one mount"),
            ("the folder instead of the file (table)", folder_instead_of_file, "differ from the mount table"),
            ("a writable realm", writable_realm, "not read-only"),
            ("a writable list", writable_list, "not read-only"),
            ("no list", no_list, "differ from the mount table"),
            ("no realm", no_realm, "exactly one mount"),
            ("a second file in the folder", second_file, "exactly one mount"),
            ("an extra bind mount", extra_bind, "differ from the mount table"),
            ("a volume at the realm target", volume_at_target, "not a bind mount"),
        ]
        for label, edit, fragment in cases:
            with self.subTest(case=label):
                problems = guards.keycloak_inspect_problems(self.mutate(edit))
                self.assertTrue(has(problems, fragment), "%s: expected %r in %s" % (label, fragment, problems))

    def test_inspect_problems_runs_the_check_for_the_keycloak_container(self):
        good = [p for p in guards.inspect_problems([self.container()]) if "keycloak container" in p]
        self.assertEqual(good, [])

        def dev_file(mounts):
            self.find(mounts, guards.REALM_TARGET)["Source"] = self.ROOT_DIR + "/deploy/keycloak/dev-realm.json"

        bad = [p for p in guards.inspect_problems([self.mutate(dev_file)]) if "keycloak container" in p]
        self.assertTrue(bad)


class VerifyTests(unittest.TestCase):
    def test_verify_reports_the_identity_check_and_names_no_value(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            runner = ScriptedRunner(answering(completed(output(facts(users_total=2, users_without_synthetic=2)))))
            problems = stackctl.verify_problems(stack, runner)
            identity = [p for p in problems if p.startswith("identity check")]
            self.assertEqual(len(identity), 1)
            self.assertIn("2 user(s) without synthetic=true", identity[0])
            sent = [(args, sql) for args, sql in runner.calls if "psql" in args]
            self.assertEqual(len(sent), 1)
            args, sql = sent[0]
            self.assertEqual(args[:5], ["exec", "-T", "postgres", "psql", "-q"])
            self.assertIn("-At", args)
            self.assertIn("ON_ERROR_STOP=1", args)
            self.assertEqual(args[args.index("-d") + 1], "keycloak")
            self.assertEqual(sql, (stack / "config" / "keycloak" / "identity-check.sql").read_text(encoding="utf-8"))

    def test_a_healthy_realm_adds_no_identity_problem(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            problems = stackctl.verify_problems(stack, ScriptedRunner(answering(completed(output(GOOD)))))
            self.assertEqual([p for p in problems if p.startswith("identity check")], [])

    def test_verify_prints_counts_and_the_c02_switch_states_only(self):
        for tenant, breached, wanted in (
            (False, False, "tenant_mfa=off breached_list=off"),
            (True, False, "tenant_mfa=on breached_list=off"),
            (True, True, "tenant_mfa=on breached_list=on"),
        ):
            with self.subTest(wanted=wanted), scratch_dir() as parent, clean_environment():
                stack = make_stack(parent)
                runner = ScriptedRunner(answering(completed(output(facts(
                    users_total=3, level_2_tenant_conditional=tenant, breached_list_in_policy=breached)))))
                code, text = quiet(stackctl.main, ["verify", "--stack", str(stack)], runner_factory=lambda s, r=runner: r)
                notes = [line for line in text.splitlines() if line.startswith("note: identity:")]
                self.assertEqual(notes, ["note: identity: users_total=3 users_without_synthetic=0 " + wanted])

    def test_the_master_realm_check_waits_for_the_retirement_of_the_bootstrap_secret(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            runner = ScriptedRunner(answering(completed(output(facts(master_users_without_otp=1)))))
            self.assertEqual(stackctl.identity_check_problems(stack, runner), [])
            self.assertFalse(stackctl.bootstrap_retired(stack))
            code, _ = quiet(stackctl.main, ["secrets", "retire", "keycloak_bootstrap", "--stack", str(stack)])
            self.assertEqual(code, 0)
            self.assertTrue(stackctl.bootstrap_retired(stack))
            problems = stackctl.identity_check_problems(stack, runner)
            self.assertEqual(len(problems), 1)
            self.assertIn("1 master-realm user(s) or service account(s) without an OTP credential", problems[0])
            unlink_force(stack / "secrets" / "keycloak_bootstrap_admin_password")
            self.assertTrue(stackctl.bootstrap_retired(stack), "a missing file counts as retired")

    def test_an_unreadable_or_failed_check_is_a_problem_never_a_pass(self):
        canary = "CANARY-alice@example.test"
        cases = {
            "psql failed": completed("ERROR: " + canary, 3),
            "garbage": completed(canary + "\n"),
            "empty": completed(""),
            "timeout": subprocess.TimeoutExpired(["docker"], 120),
            "no docker": OSError(canary),
        }
        for label, result in cases.items():
            with self.subTest(case=label), scratch_dir() as parent, clean_environment():
                stack = make_stack(parent)
                problems = stackctl.identity_check_problems(stack, ScriptedRunner(answering(result)))
                self.assertEqual(len(problems), 1)
                self.assertTrue(problems[0].startswith("identity check: "))
                self.assertNotIn("CANARY", problems[0])

    def test_a_missing_or_edited_sql_file_is_never_sent(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            path = stack / "config" / "keycloak" / "identity-check.sql"
            runner = ScriptedRunner(answering(completed(output(GOOD))))
            text = path.read_text(encoding="utf-8")
            path.write_text(text.replace("ROLLBACK;", "DROP DATABASE keycloak;\nROLLBACK;"), encoding="utf-8")
            problems = stackctl.identity_check_problems(stack, runner)
            self.assertEqual(len(problems), 1)
            self.assertEqual(runner.calls, [], "an edited file must not reach psql")
            path.unlink()
            problems = stackctl.identity_check_problems(stack, runner)
            self.assertIn("missing from the stack folder", problems[0])
            self.assertEqual(runner.calls, [])


class RealmRebuildTests(unittest.TestCase):
    def rebuild(self, stack, runner, *flags):
        return quiet(stackctl.main, ["realm", "rebuild", "--stack", str(stack), *flags], runner_factory=lambda s: runner)

    @staticmethod
    def destructive(runner):
        return [args for args, _ in runner.calls if args[:1] in (["stop"], ["up"]) or ("psql" in args and "postgres" == args[args.index("-d") + 1])]

    def test_it_does_nothing_without_confirm(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            runner = ScriptedRunner(answering(completed(output(GOOD))))
            code, text = self.rebuild(stack, runner)
            self.assertEqual(code, 2)
            self.assertIn("--confirm", text)
            self.assertEqual(runner.calls, [], "no call at all, not even the read-only check")

    def test_it_refuses_a_real_account_and_touches_nothing(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            runner = ScriptedRunner(answering(completed(output(facts(users_total=3, users_without_synthetic=2)))))
            code, text = self.rebuild(stack, runner, "--confirm")
            self.assertEqual(code, 2)
            self.assertIn("2 user(s) without synthetic=true", text)
            self.assertEqual(self.destructive(runner), [])
            self.assertEqual(len(runner.calls), 1, "only the read-only check ran")

    def test_it_refuses_when_the_check_cannot_run_or_is_unreadable_fail_closed(self):
        canary = "CANARY-bob@example.test"
        cases = {
            "psql exits non-zero": completed("", 2),
            "unparsable output": completed(canary + "\n"),
            "empty output": completed(""),
            "a key missing": completed(output(GOOD).replace("users_total=0\n", "")),
            "timeout": subprocess.TimeoutExpired(["docker"], 120),
        }
        for label, result in cases.items():
            with self.subTest(case=label), scratch_dir() as parent, clean_environment():
                stack = make_stack(parent)
                runner = ScriptedRunner(answering(result))
                code, text = self.rebuild(stack, runner, "--confirm")
                self.assertEqual(code, 2)
                self.assertIn("refusing to rebuild", text)
                self.assertNotIn("CANARY", text)
                self.assertNotIn("bob", text)
                self.assertEqual(self.destructive(runner), [])

    def test_it_refuses_a_missing_check_file(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            (stack / "config" / "keycloak" / "identity-check.sql").unlink()
            runner = ScriptedRunner(answering(completed(output(GOOD))))
            code, text = self.rebuild(stack, runner, "--confirm")
            self.assertEqual(code, 2)
            self.assertEqual(runner.calls, [])

    def test_it_refuses_when_the_bootstrap_secret_is_retired(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            quiet(stackctl.main, ["secrets", "retire", "keycloak_bootstrap", "--stack", str(stack)])
            runner = ScriptedRunner(answering(completed(output(GOOD))))
            code, text = self.rebuild(stack, runner, "--confirm")
            self.assertEqual(code, 2)
            self.assertIn("secrets rotate keycloak_bootstrap", text)
            self.assertEqual(self.destructive(runner), [])

    def test_it_rebuilds_while_every_user_is_synthetic(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            before = secret_values(stack)
            runner = ScriptedRunner(answering(completed(output(facts(users_total=4, users_without_synthetic=0)))))
            code, text = self.rebuild(stack, runner, "--confirm")
            self.assertEqual(code, 0, text)
            names = [args[:2] if args[:1] in (["stop"], ["up"]) else ("psql", args[args.index("-d") + 1]) for args, _ in runner.calls]
            self.assertEqual(names, [("psql", "keycloak"), ["stop", "keycloak"], ("psql", "postgres"), ["up", "-d"]])
            drop = runner.calls[2][1]
            self.assertEqual(drop, stackctl.REBUILD_SQL)
            self.assertEqual(runner.calls[3][0], ["up", "-d", "keycloak"])
            self.assertIn("DROP DATABASE IF EXISTS keycloak WITH (FORCE);", drop)
            self.assertEqual(drop.count("DATABASE"), 3, "one database, named in a constant")
            # No secret value in any argument, any SQL or any printed line.
            sent = "\n".join(" ".join(args) + "\n" + (sql or "") for args, sql in runner.calls)
            for value in before:
                self.assertNotIn(value, sent)
                self.assertNotIn(value, text)
            self.assertEqual(secret_values(stack), before, "rebuild never rewrites a secret")

    def test_a_failed_step_stops_the_sequence(self):
        for label, kwargs, expected_calls in (
            ("stop fails", {"stop_code": 1}, 2),
            ("drop fails", {"rebuild_code": 3}, 3),
            ("start fails", {"up_code": 1}, 4),
        ):
            with self.subTest(case=label), scratch_dir() as parent, clean_environment():
                stack = make_stack(parent)
                runner = ScriptedRunner(answering(completed(output(GOOD)), **kwargs))
                code, text = self.rebuild(stack, runner, "--confirm")
                self.assertEqual(code, 2)
                self.assertIn("failed (exit", text)
                self.assertEqual(len(runner.calls), expected_calls)

    def test_the_command_exists_in_the_parser_and_needs_the_flag_spelled_out(self):
        parser = stackctl.build_parser()
        args = parser.parse_args(["realm", "rebuild", "--stack", "x"])
        self.assertFalse(args.confirm)
        args = parser.parse_args(["realm", "rebuild", "--stack", "x", "--confirm"])
        self.assertTrue(args.confirm)
        self.assertIs(args.func, stackctl.cmd_realm_rebuild)

    def test_the_rebuild_sql_names_one_constant_database(self):
        self.assertEqual(set(re.findall(r"DATABASE (?:IF EXISTS )?(\w+)", stackctl.REBUILD_SQL)), {"keycloak"})
        self.assertEqual(stackctl.REBUILD_SQL.count(";"), 3, "three statements and nothing else")
        self.assertNotIn("%", stackctl.REBUILD_SQL)
        self.assertNotIn("{", stackctl.REBUILD_SQL)

    def test_the_bff_client_rotation_points_at_the_rebuild(self):
        self.assertIn("realm rebuild --confirm", stackctl.NEXT_STEPS["bff_client"])


class NoSecretCanaryTests(unittest.TestCase):
    """No command of this issue prints a secret, a name or a derived value (G4-121-05 b)."""

    def test_verify_and_rebuild_print_no_secret_value_in_any_outcome(self):
        with scratch_dir() as parent, clean_environment():
            stack = make_stack(parent)
            sensitive = secret_values(stack)
            self.assertGreater(len(sensitive), 10)
            printed = []
            outcomes = (
                completed(output(GOOD)),
                completed(output(facts(users_total=2, users_without_synthetic=2))),
                completed("password=" + sorted(sensitive)[0] + "\n"),
                completed("ERROR: " + sorted(sensitive)[1], 3),
            )
            for result in outcomes:
                runner = ScriptedRunner(answering(result))
                _, text = quiet(stackctl.main, ["verify", "--stack", str(stack)], runner_factory=lambda s, r=runner: r)
                printed.append(text)
                _, text = quiet(stackctl.main, ["realm", "rebuild", "--stack", str(stack), "--confirm"], runner_factory=lambda s, r=runner: r)
                printed.append(text)
            joined = "\n".join(printed)
            self.assertIn("verify:", joined)
            for value in sensitive:
                self.assertNotIn(value, joined)


if __name__ == "__main__":
    unittest.main()
