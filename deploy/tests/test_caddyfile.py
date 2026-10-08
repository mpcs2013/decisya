"""D10 guard 8: the Caddyfile (issue #120, G3 G4-120-02, S-120-01).

`caddy adapt` runs in the pinned Caddy image (the digest from ContainerImages.cs) with
`--network none`, over documentation-range values for the five DECISYA_* variables, and the adapted
JSON is compared with the edge table. The red cases then edit the adapted JSON and the text one rule
at a time. A Testcontainers behaviour test and the stack smoke cover how Caddy actually answers.
"""
from __future__ import annotations

import unittest

from deploy_common import FIXTURE_VALUES, ROOT, DockerCase, deep, guards, stackctl

CADDYFILE = ROOT / "deploy" / "caddy" / "Caddyfile"


class CaddyTextRules(unittest.TestCase):
    """No Docker needed: rules over the Caddyfile text."""

    def test_the_committed_text_passes(self):
        self.assertEqual(guards.caddy_text_problems(CADDYFILE.read_text(encoding="utf-8")), [])

    def test_comments_may_mention_the_banned_words(self):
        text = CADDYFILE.read_text(encoding="utf-8")
        self.assertIn("trusted_proxies", text, "the explanatory comment is expected to name it")
        self.assertEqual(guards.caddy_text_problems(text), [])

    def test_banned_directives_and_unknown_variables_are_flagged(self):
        base = CADDYFILE.read_text(encoding="utf-8")
        for label, extra, fragment in (
            ("trusted_proxies", "\nx.example.test {\n\ttrusted_proxies private_ranges\n}\n", "trusted_proxies"),
            ("log_credentials", "\n{\n\tservers {\n\t\tlog_credentials\n\t}\n}\n", "log_credentials"),
            ("unvalidated variable", "\n{$DECISYA_EXTRA} {\n\trespond 403\n}\n", "no validator"),
            ("management port", "\nx.example.test {\n\treverse_proxy keycloak:9000\n}\n", "9000"),
            ("health listener", "\nx.example.test {\n\treverse_proxy api:8081\n}\n", "8081"),
        ):
            with self.subTest(case=label):
                problems = guards.caddy_text_problems(base + extra)
                self.assertTrue(any(fragment in p for p in problems), problems)


class CaddyForwardingAndPassThroughTextRules(unittest.TestCase):
    """#122 (G2 D8, G3 G4-122-01): the BFF's rate limiter partitions on the client address that stock Caddy
    writes into X-Forwarded-For, and a 429 with its Retry-After must pass through the edge untouched. These
    two text guards need no Docker; the adapted-JSON forms are in CaddyAdaptRules."""

    BASE = CADDYFILE.read_text(encoding="utf-8")

    def with_block(self, line: str) -> str:
        return self.BASE + "\nx.example.test {\n\treverse_proxy bff:8080 {\n\t\t%s\n\t}\n}\n" % line

    def test_the_committed_file_has_neither(self):
        self.assertEqual(guards.caddy_text_problems(self.BASE), [])

    def test_no_header_up_on_a_forwarding_header(self):
        for line in (
            "header_up X-Forwarded-For {remote_host}", "header_up -X-Forwarded-For", "header_up +X-Forwarded-For 1",
            "header_up X-Forwarded-Proto https", "header_up x-forwarded-host evil.example.test", "header_up Forwarded for=1",
            'header_up "X-Forwarded-For" 1', "request_header X-Forwarded-For 1", "request_header -Forwarded",
        ):
            with self.subTest(line=line):
                problems = guards.caddy_text_problems(self.with_block(line))
                self.assertTrue(any("X-Forwarded" in p for p in problems), problems)

    def test_other_header_up_directives_and_comments_are_fine(self):
        for line in ("header_up Host {upstream_hostport}", "header_up X-Request-Id 1", "# header_up X-Forwarded-For {remote_host}"):
            with self.subTest(line=line):
                self.assertEqual(guards.caddy_text_problems(self.with_block(line)), [])

    def test_no_handle_response_no_intercept_no_retry_after(self):
        for line, fragment in (
            ("handle_response { respond 503 }", "handle_response"),
            ("intercept { respond 503 }", "intercept"),
            ("header_down Retry-After 1", "Retry-After"),
            ("header_down -Retry-After", "Retry-After"),
            ("header_down retry-after 1", "Retry-After"),
        ):
            with self.subTest(line=line):
                problems = guards.caddy_text_problems(self.with_block(line))
                self.assertTrue(any(fragment in p for p in problems), problems)
        problems = guards.caddy_text_problems(self.BASE + "\n{$DECISYA_APP_HOST}\nheader Retry-After 5\n")
        self.assertTrue(any("Retry-After" in p for p in problems), problems)

    def test_a_comment_may_name_them(self):
        text = self.BASE + "\n# handle_response and intercept would hide a 429; Retry-After must pass through\n"
        self.assertEqual(guards.caddy_text_problems(text), [])


class CaddyAdaptRules(DockerCase):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        refs = guards.load_scan_refs(ROOT)
        cls.runner = stackctl.Runner(ROOT)
        cls.adapted = cls.runner.caddy_adapt(refs["caddy"], CADDYFILE, FIXTURE_VALUES)

    def problems(self, adapted=None):
        return guards.caddy_problems(adapted if adapted is not None else deep(self.adapted), FIXTURE_VALUES)

    def test_adapted_config_equals_the_edge_table(self):
        self.assertEqual(self.problems(), [])

    def test_fixture_allow_lists_overlap_no_docker_network(self):
        subnets = self.runner.docker_subnets()
        self.assertEqual(guards.overlap_problems(FIXTURE_VALUES, subnets), [])

    # ----- red cases on the adapted JSON

    def first(self, adapted, predicate):
        for node in guards._iter_dicts(adapted):
            if predicate(node):
                return node
        self.fail("the adapted JSON has no node of the expected shape; the guard's assumptions need review")

    def assert_flagged(self, label, mutate, fragment):
        adapted = deep(self.adapted)
        mutate(adapted)
        problems = self.problems(adapted)
        self.assertTrue(any(fragment in p for p in problems), "%s was not flagged (%s); got %s" % (label, fragment, problems))

    def test_each_edge_rule_can_fail(self):
        def widen_admin_to_the_lan(adapted):
            node = self.first(adapted, lambda n: isinstance(n.get("remote_ip"), dict)
                              and [guards._norm_net(r) for r in n["remote_ip"].get("ranges", [])] == [guards._norm_net(FIXTURE_VALUES["DECISYA_WORKSTATION_ADDRESS"])])
            node["remote_ip"]["ranges"] = [FIXTURE_VALUES["DECISYA_LAN_SUBNET"]]

        def logout_from_the_lan(adapted):
            node = self.first(adapted, lambda n: n.get("method") == ["POST"])
            node["remote_ip"] = {"ranges": [FIXTURE_VALUES["DECISYA_LAN_SUBNET"]]}

        def upstream_on_9000(adapted):
            node = self.first(adapted, lambda n: n.get("dial") == "keycloak:8080")
            node["dial"] = "keycloak:9000"

        def drop_catch_all(adapted):
            routes = self.first(adapted, lambda n: isinstance(n.get("routes"), list) and any(
                isinstance(r, dict) and any(isinstance(h, dict) and h.get("handler") == "reverse_proxy" for h in r.get("handle", []))
                for r in n["routes"]))["routes"]
            routes.pop()

        def add_trusted_proxies(adapted):
            next(iter(adapted["apps"]["http"]["servers"].values()))["trusted_proxies"] = {"source": "static"}

        def log_credentials(adapted):
            next(iter(adapted["apps"]["http"]["servers"].values())).setdefault("logs", {})["should_log_credentials"] = True

        def no_tls_policy(adapted):
            adapted["apps"]["tls"]["automation"]["policies"] = []

        def acme_issuer(adapted):
            adapted["apps"]["tls"]["automation"]["policies"][0]["issuers"] = [{"module": "acme"}]

        def admin_on(adapted):
            adapted["admin"] = {}

        def install_trust(adapted):
            adapted["apps"]["pki"]["certificate_authorities"]["local"]["install_trust"] = True

        cases = [
            ("admin paths opened to the LAN", widen_admin_to_the_lan, "route"),
            ("back-channel logout opened to the LAN", logout_from_the_lan, "route"),
            ("upstream on the management port", upstream_on_9000, "9000"),
            ("catch-all removed", drop_catch_all, "routes"),
            ("trusted_proxies", add_trusted_proxies, "trusted_proxies"),
            ("log credentials", log_credentials, "credentials"),
            ("no tls internal", no_tls_policy, "tls internal"),
            ("acme issuer", acme_issuer, "issuer other than internal"),
            ("admin API on", admin_on, "admin API"),
            ("root installed into the OS store", install_trust, "skip_install_trust"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    # ----- #122: the two edge duties of the rate limiter, on the adapted JSON

    def bff_proxy(self, adapted):
        return self.first(adapted, lambda n: n.get("handler") == "reverse_proxy"
                          and any(u.get("dial") == "bff:8080" for u in (n.get("upstreams") or [])))

    def test_forwarding_headers_are_never_set_copied_or_deleted(self):
        def set_xff(adapted):
            self.bff_proxy(adapted)["headers"] = {"request": {"set": {"X-Forwarded-For": ["1"]}}}

        def delete_proto(adapted):
            self.bff_proxy(adapted)["headers"] = {"request": {"delete": ["X-Forwarded-Proto"]}}

        def add_forwarded(adapted):
            self.bff_proxy(adapted)["headers"] = {"request": {"add": {"Forwarded": ["for=1"]}}}

        def request_header_handler(adapted):
            node = self.bff_proxy(adapted)
            node["handler"] = "headers"
            node["request"] = {"set": {"x-forwarded-host": ["evil.example.test"]}}

        for label, mutate in (("header_up set", set_xff), ("header_up delete", delete_proto), ("header_up add Forwarded", add_forwarded),
                              ("request_header", request_header_handler)):
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, "request header")

    def test_an_unrelated_header_up_is_allowed(self):
        adapted = deep(self.adapted)
        self.bff_proxy(adapted)["headers"] = {"request": {"set": {"X-Request-Id": ["1"]}}}
        self.assertEqual(guards.caddy_forwarding_problems(adapted), [])

    def test_a_429_and_its_retry_after_pass_through_the_app_host(self):
        def handle_response(adapted):
            self.bff_proxy(adapted)["handle_response"] = [{"match": {"status_code": [429]}}]

        def intercept(adapted):
            self.bff_proxy(adapted)["handler"] = "intercept"

        def retry_after_set(adapted):
            self.bff_proxy(adapted)["headers"] = {"response": {"set": {"Retry-After": ["1"]}}}

        def retry_after_delete(adapted):
            self.bff_proxy(adapted)["headers"] = {"response": {"delete": ["retry-after"]}}

        for label, mutate, fragment in (("handle_response", handle_response, "handle_response"), ("intercept", intercept, "intercept handler"),
                                        ("header_down set", retry_after_set, "Retry-After"), ("header_down delete", retry_after_delete, "Retry-After")):
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)


if __name__ == "__main__":
    unittest.main()
