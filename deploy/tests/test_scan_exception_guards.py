"""S-120-12 guards that back the image-scan exceptions (issue #120, G3 delta 2026-10-06).

The exceptions for OpenSSL in postgres and redis and for the grpc findings in Caddy hold only while
TLS stays off in postgres and redis and the Caddyfile has no `tracing` directive. Each guard here has
a red case: a guard that cannot fail is not a guard. No Docker needed.
"""
from __future__ import annotations

import unittest

from deploy_common import ROOT, guards

CADDYFILE = ROOT / "deploy" / "caddy" / "Caddyfile"
REDIS_CONF = ROOT / "deploy" / "redis" / "redis.conf"


class CaddyTracingGuard(unittest.TestCase):
    def test_the_committed_caddyfile_has_no_tracing_directive(self):
        text = CADDYFILE.read_text(encoding="utf-8")
        self.assertFalse([p for p in guards.caddy_text_problems(text) if "tracing" in p])

    def test_a_tracing_directive_is_flagged(self):
        base = CADDYFILE.read_text(encoding="utf-8")
        for label, extra in (
            ("site block", "\nx.example.test {\n\ttracing {\n\t\tspan x\n\t}\n}\n"),
            ("indented", "\nx.example.test {\n    tracing\n}\n"),
            ("inline", "\nx.example.test { tracing }\n"),
        ):
            with self.subTest(case=label):
                problems = guards.caddy_text_problems(base + extra)
                self.assertTrue(any("tracing" in p for p in problems), problems)

    def test_a_comment_may_name_tracing(self):
        base = CADDYFILE.read_text(encoding="utf-8")
        problems = guards.caddy_text_problems(base + "\n# no tracing directive here\n")
        self.assertFalse([p for p in problems if "tracing" in p], problems)


class PostgresSslGuard(unittest.TestCase):
    def problems(self, **service):
        return guards.tls_problems({"postgres": service})

    def test_the_default_is_clean(self):
        self.assertEqual(self.problems(command=["postgres"], environment={"POSTGRES_DB": "x"}), [])

    def test_ssl_on_is_flagged_in_every_form(self):
        cases = {
            "-c ssl=on": {"command": ["postgres", "-c", "ssl=on"]},
            "-c as one string": {"command": ["postgres", "-c ssl=on"]},
            "--ssl=on": {"command": ["postgres", "--ssl=on"]},
            "entrypoint": {"entrypoint": ["sh", "-c", "exec postgres -c ssl=on"]},
            "uppercase": {"command": ["postgres", "-c", "SSL=ON"]},
            "true": {"command": ["postgres", "-c", "ssl=true"]},
            "environment": {"environment": {"POSTGRES_OPTS": "-c ssl=on"}},
        }
        for label, service in cases.items():
            with self.subTest(case=label):
                self.assertTrue(any("ssl=on" in p for p in self.problems(**service)), label)

    def test_ssl_off_and_other_names_are_not_flagged(self):
        for command in (["postgres", "-c", "ssl=off"], ["postgres", "-c", "no_ssl=on"], ["postgres", "-c", "ssl_min_protocol_version=TLSv1.3"]):
            with self.subTest(command=command):
                self.assertEqual(self.problems(command=command), [])


class RedisTlsGuard(unittest.TestCase):
    def problems(self, **service):
        return guards.tls_problems({"redis": service})

    def test_the_default_is_clean(self):
        self.assertEqual(self.problems(command=["redis-server", "/usr/local/etc/redis/redis.conf"]), [])

    def test_a_tls_option_is_flagged_in_every_form(self):
        cases = {
            "--tls-port": {"command": ["redis-server", "--tls-port", "6379"]},
            "tls-cert-file": {"command": ["redis-server", "--tls-cert-file", "/x"]},
            "inside a shell string": {"entrypoint": ["sh", "-c", "exec redis-server --tls-port 6379"]},
            "bare option": {"command": ["redis-server", "tls-port", "6379"]},
            "uppercase": {"command": ["redis-server", "--TLS-PORT", "6379"]},
            "environment": {"environment": {"REDIS_ARGS": "--tls-port 6379"}},
        }
        for label, service in cases.items():
            with self.subTest(case=label):
                self.assertTrue(any("tls-" in p for p in self.problems(**service)), label)

    def test_the_committed_redis_conf_has_no_tls_directive(self):
        self.assertEqual(guards.redis_conf_problems(REDIS_CONF.read_text(encoding="utf-8")), [])

    def test_a_tls_directive_in_redis_conf_is_flagged_but_a_comment_is_not(self):
        base = REDIS_CONF.read_text(encoding="utf-8")
        for directive in ("tls-port 6379", "  tls-cert-file /x", "TLS-AUTH-CLIENTS no"):
            with self.subTest(directive=directive):
                self.assertTrue(guards.redis_conf_problems(base + "\n" + directive + "\n"))
        self.assertEqual(guards.redis_conf_problems(base + "\n# tls-port 6379 is never set\n"), [])


class MergedConfigWiring(unittest.TestCase):
    def test_config_problems_runs_the_tls_guard(self):
        cfg = {"services": {"postgres": {"command": ["postgres", "-c", "ssl=on"]}, "redis": {"command": ["redis-server", "--tls-port", "1"]}}}
        problems = guards.config_problems(cfg, interpolated=False)
        self.assertTrue(any("service postgres: ssl=on" in p for p in problems), problems)
        self.assertTrue(any("service redis: a tls- option" in p for p in problems), problems)


if __name__ == "__main__":
    unittest.main()
