"""D10 guard 8: the Caddyfile (issue #120, G3 G4-120-02, S-120-01; reworked by issue #178, G2 D1 and D4,
G3 G4-178-01 and G4-178-03).

`caddy adapt` runs in the pinned Caddy image (the digest from ContainerImages.cs) with
`--network none`, over documentation-range values for the three DECISYA_* host names, and the adapted
JSON is compared with the edge table. The red cases then edit the adapted JSON and the text one rule
at a time. A Testcontainers behaviour test and the stack smoke cover how Caddy actually answers.

The #178 red cases run twice: over the real adapted JSON (needs Docker) and over a hand-built JSON of the
same shape (`CaddyModelRules`, no Docker), so the rules are exercised on every machine.
"""
from __future__ import annotations

import unittest
from unittest import mock

from deploy_common import FIXTURE_VALUES, ROOT, DockerCase, deep, guards, stackctl

CADDYFILE = ROOT / "deploy" / "caddy" / "Caddyfile"
RUNBOOK = ROOT / "docs" / "runbooks" / "deployable-stack.md"

APP = FIXTURE_VALUES["DECISYA_APP_HOST"]
ID = FIXTURE_VALUES["DECISYA_ID_HOST"]
API = FIXTURE_VALUES["DECISYA_API_HOST"]
# A documentation range, standing in for "a Home subnet" in the red cases.
HOME_RANGE = "192.0.2.0/24"


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

    def test_the_retired_allow_list_variables_are_gone_from_the_code(self):
        # #178: a source allow-list in Caddy would refuse every client behind the userland proxy.
        code = guards.strip_caddy_comments(CADDYFILE.read_text(encoding="utf-8"))
        for word in ("DECISYA_LAN_SUBNET", "DECISYA_WORKSTATION_ADDRESS"):
            self.assertNotIn(word, code)
        # Re-adding one is a variable with no validator, flagged by the text guard.
        problems = guards.caddy_text_problems(CADDYFILE.read_text(encoding="utf-8") + "\n@lan remote_ip {$DECISYA_LAN_SUBNET}\n")
        self.assertTrue(any("no validator" in p for p in problems), problems)

    def test_every_site_address_is_a_host_variable_and_the_ports_are_literals(self):
        code = guards.strip_caddy_comments(CADDYFILE.read_text(encoding="utf-8"))
        self.assertIn("{$DECISYA_ID_HOST}:8444 {", code)
        self.assertNotIn("DECISYA_ADMIN_PORT", code)
        self.assertNotIn("DECISYA_HTTPS_PORT", code)


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


class CaddyPathMatcherModel(unittest.TestCase):
    """G-E4: `caddy_path_matches` is the guards' model of Caddy's `path` matcher. It is faithful only for
    the patterns the id sites may hold (no `%`, no `//`, no inner `*`), which a guard enforces."""

    def test_rows(self):
        rows = (
            ("/admin", "/admin", True), ("/admin", "/Admin", True), ("/ADMIN", "/admin", True), ("/admin", "/admin/", False),
            ("/admin", "/administrator", False), ("/admin/*", "/admin/", True), ("/admin/*", "/ADMIN/x/y", True),
            ("/admin/*", "/admin", False), ("/realms/master/*", "/realms/master/protocol/openid-connect/token", True),
            ("/realms/master", "/realms/master/", False), ("*", "/anything", True), ("*.js", "/a/b.js", True),
            ("*.js", "/a/b.jsx", False), ("*a*", "/xax", True), ("*a*", "/xyz", False),
            ("/a/*/c*", "/a/b/cd", True), ("/a/*/c*", "/a/b/x/cd", False), ("/a/*/c", "/a/b/C", True),
        )
        for pattern, path, expected in rows:
            with self.subTest(pattern=pattern, path=path):
                self.assertIs(guards.caddy_path_matches(pattern, path), expected)

    def test_the_admin_probes_are_all_matched_by_the_refusal_patterns_and_none_by_the_published_ones(self):
        for probe in guards.ADMIN_PROBES:
            with self.subTest(probe=probe):
                self.assertTrue(any(guards.caddy_path_matches(p, probe) for p in guards.ADMIN_PATHS))
                self.assertFalse(any(guards.caddy_path_matches(p, probe) for p in guards.PUBLISHED_ID_PATHS))

    def test_the_admin_listener_refuses_the_realm_the_bff_and_the_api(self):
        for probe in guards.ADMIN_LISTENER_REFUSED:
            with self.subTest(probe=probe):
                self.assertFalse(any(guards.caddy_path_matches(p, probe) for p in guards.ADMIN_LISTENER_PATHS))


class RunbookFirewallRules(unittest.TestCase):
    """G-E3 and G4-178-03, static (no Docker): the runbook carries the rules, the refusal row and STOP."""

    GOOD = (
        "| NAS-FW-8443 | LAN 1 | TCP 8443 | the Home subnet | Allow |\n"
        "| NAS-FW-ADMIN | LAN 1 | TCP 8444 | the workstation | Allow |\n"
        "| a second Home device | 8444, any path | **no connection** |\n"
        "Any failed row is a STOP.\n"
    )

    def test_the_committed_runbook_passes(self):
        self.assertEqual(guards.runbook_firewall_problems(RUNBOOK.read_text(encoding="utf-8")), [])

    def test_a_good_text_passes(self):
        self.assertEqual(guards.runbook_firewall_problems(self.GOOD), [])

    def test_each_rule_can_fail(self):
        cases = (
            ("the 8443 rule id missing", self.GOOD.replace("NAS-FW-8443", "NAS-FW-X"), "NAS-FW-8443"),
            ("the admin rule id missing", self.GOOD.replace("NAS-FW-ADMIN", "NAS-FW-X"), "NAS-FW-ADMIN"),
            ("the admin rule without its port", self.GOOD.replace("TCP 8444", "TCP"), "NAS-FW-ADMIN"),
            ("the 8443 rule without its port", self.GOOD.replace("TCP 8443", "TCP"), "NAS-FW-8443"),
            ("the refusal row missing", self.GOOD.replace("no connection", "refused"), "no connection"),
            ("the refusal row without the port", self.GOOD.replace("8444, any path", "any path"), "no connection"),
            ("STOP missing", self.GOOD.replace("STOP", "stop"), "STOP"),
        )
        for label, text, fragment in cases:
            with self.subTest(case=label):
                problems = guards.runbook_firewall_problems(text)
                self.assertTrue(any(fragment in p for p in problems), problems)


class EdgeRedCases:
    """The #178 red cases, one per rule (G2 D4, G3 G4-178-01). The host class provides `adapted` and
    `problems`. Mixed into a Docker class (real adapted JSON) and a no-Docker class (hand-built JSON)."""

    def servers(self, adapted):
        return adapted["apps"]["http"]["servers"]

    def site(self, adapted, port, host):
        for server in self.servers(adapted).values():
            if server.get("listen") == [":%d" % port]:
                for route in server.get("routes") or []:
                    if any(host in m.get("host", []) for m in route.get("match") or []):
                        return route
        self.fail("no site %s on %d in the adapted JSON; the guard's assumptions need review" % (host, port))

    def routes(self, adapted, port, host):
        return guards._proxy_route_list(self.site(adapted, port, host))

    def match(self, adapted, port, host, index):
        return self.routes(adapted, port, host)[index]["match"][0]

    def assert_flagged(self, label, mutate, fragment):
        adapted = deep(self.adapted)
        mutate(adapted)
        problems = self.problems(adapted)
        self.assertTrue(any(fragment in p for p in problems), "%s was not flagged (%s); got %s" % (label, fragment, problems))

    def test_the_adapted_config_equals_the_edge_table(self):
        self.assertEqual(self.problems(), [])

    def test_each_published_port_rule_can_fail(self):
        m = self.match
        cases = [
            ("/admin/* added to the published id route on 8443", lambda a: m(a, 8443, ID, 3)["path"].append("/admin/*"), "admits the admin path"),
            ("a case variant of an admin pattern on 8443", lambda a: m(a, 8443, ID, 3)["path"].append("/ADMIN/*"), "admits the admin path"),
            ("/realms/master added to the published id route on 8443", lambda a: m(a, 8443, ID, 3)["path"].append("/realms/master"), "admits the admin path"),
            ("the explicit 403 for the admin paths removed", lambda a: self.routes(a, 8443, ID).pop(1), "second route must be an explicit 403"),
            ("the 403 narrowed to /admin", lambda a: m(a, 8443, ID, 1).update(path=["/admin"]), "second route must be an explicit 403"),
            ("/realms/decisya/* added to the 8444 route", lambda a: m(a, 8444, ID, 2)["path"].append("/realms/decisya/*"), "must admit exactly the admin paths"),
            ("the 8444 site moved to the app host", lambda a: self.site(a, 8444, ID)["match"][0].update(host=[APP]), "8444 listener must hold exactly the id site"),
            ("the 8443 id site moved to another host", lambda a: self.site(a, 8443, ID)["match"][0].update(host=["x.example.test"]), "8443 listener must hold exactly"),
            ("the 8444 catch-all removed", lambda a: self.routes(a, 8444, ID).pop(), "must end with the catch-all"),
            ("not remote_ip dropped from a published route", lambda a: m(a, 8443, ID, 3).pop("not"), "published route must carry"),
            ("not remote_ip dropped from the published app route", lambda a: m(a, 8443, APP, 3).pop("not"), "published route must carry"),
            ("not remote_ip over one subnet only", lambda a: m(a, 8444, ID, 2)["not"][0]["remote_ip"].update(ranges=["10.120.0.0/24"]), "published route must carry"),
            ("a Home range as a positive remote_ip on a published route", lambda a: m(a, 8443, ID, 3).update(remote_ip={"ranges": [HOME_RANGE]}), "source allow-list"),
            ("a Home range as a positive remote_ip on the admin port", lambda a: m(a, 8444, ID, 2).update(remote_ip={"ranges": [HOME_RANGE]}), "source allow-list"),
            ("a back-channel remote_ip widened to a Home range", lambda a: m(a, 8443, ID, 4)["remote_ip"].update(ranges=[HOME_RANGE]), "source allow-list"),
            ("the logout route widened to a Home range", lambda a: m(a, 8443, APP, 1)["remote_ip"].update(ranges=[HOME_RANGE]), "source allow-list"),
            ("a third listener", lambda a: self.servers(a).update(srv9={"listen": [":9443"], "routes": []}), "exactly two HTTP servers"),
            ("a second listen address on a server", lambda a: next(iter(self.servers(a).values()))["listen"].append(":9443"), "single :8443 or :8444"),
            ("two servers on one port", lambda a: self.servers(a).update(srv9={"listen": [":8444"], "routes": []}), "exactly two HTTP servers"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_a_published_route_missing_from_the_table_is_flagged_both_ways(self):
        listed = guards.PUBLISHED_ROUTES
        with mock.patch.object(guards, "PUBLISHED_ROUTES", listed[:2]):
            problems = self.problems()
        self.assertTrue(any("is not in PUBLISHED_ROUTES" in p for p in problems), problems)
        extra = listed + ((8443, "api", None, "api:8080", guards.FW_8443),)
        with mock.patch.object(guards, "PUBLISHED_ROUTES", extra):
            problems = self.problems()
        self.assertTrue(any("no such published route exists" in p for p in problems), problems)

    def test_a_table_entry_naming_the_wrong_firewall_rule_is_flagged(self):
        wrong = tuple((p, s, paths, u, guards.FW_8443 if p == guards.ADMIN_PORT else r) for p, s, paths, u, r in guards.PUBLISHED_ROUTES)
        with mock.patch.object(guards, "PUBLISHED_ROUTES", wrong):
            problems = self.problems()
        self.assertTrue(any("must name the rule" in p for p in problems), problems)

    def test_each_path_matching_rule_of_the_id_sites_can_fail(self):
        m = self.match
        cases = []
        for port, index in ((8443, 3), (8444, 2)):
            cases += [
                ("a %% pattern on %d" % port, lambda a, p=port, i=index: m(a, p, ID, i)["path"].append("/realms/de%63isya"), "holds %"),
                ("a // pattern on %d" % port, lambda a, p=port, i=index: m(a, p, ID, i)["path"].append("/realms//decisya"), "holds //"),
                ("an inner * pattern on %d" % port, lambda a, p=port, i=index: m(a, p, ID, i)["path"].append("/realms/*/x"), "inner *"),
                ("path_regexp on %d" % port, lambda a, p=port, i=index: m(a, p, ID, i).update(path_regexp={"pattern": "x"}), "path_regexp, expression, header or vars"),
                ("expression on %d" % port, lambda a, p=port, i=index: m(a, p, ID, i).update(expression="true"), "path_regexp, expression, header or vars"),
                ("header on %d" % port, lambda a, p=port, i=index: m(a, p, ID, i).update(header={"X-Y": ["z"]}), "path_regexp, expression, header or vars"),
                ("vars on %d" % port, lambda a, p=port, i=index: m(a, p, ID, i).update(vars={"a": ["b"]}), "path_regexp, expression, header or vars"),
            ]
        # The refusal route itself may not hold such a pattern either (it is an id-site pattern too).
        cases.append(("a % pattern in the 403 route", lambda a: m(a, 8443, ID, 1)["path"].append("/%61dmin"), "holds %"))
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_the_encoded_path_refusal_is_part_of_the_edge_table(self):
        # S-178-02: dropping it from either id site is a difference from the table.
        for port, index in ((8443, 2), (8444, 1)):
            with self.subTest(port=port):
                self.assert_flagged("encoded refusal removed on %d" % port, lambda a, p=port, i=index: self.routes(a, p, ID).pop(i), "(the edge table)")


class CaddyAdaptRules(EdgeRedCases, DockerCase):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        refs = guards.load_scan_refs(ROOT)
        cls.runner = stackctl.Runner(ROOT)
        cls.adapted = cls.runner.caddy_adapt(refs["caddy"], CADDYFILE, FIXTURE_VALUES)

    def problems(self, adapted=None):
        return guards.caddy_problems(adapted if adapted is not None else deep(self.adapted), FIXTURE_VALUES)

    # ----- red cases on the adapted JSON

    def first(self, adapted, predicate):
        for node in guards._iter_dicts(adapted):
            if predicate(node):
                return node
        self.fail("the adapted JSON has no node of the expected shape; the guard's assumptions need review")

    def test_each_edge_rule_can_fail(self):
        def logout_from_a_home_range(adapted):
            node = self.first(adapted, lambda n: n.get("method") == ["POST"])
            node["remote_ip"] = {"ranges": [HOME_RANGE]}

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
            ("back-channel logout opened to a Home range", logout_from_a_home_range, "route"),
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


# --------------------------------------------------------------------------- the same rules, no Docker

def _respond(match=None, status="403"):
    route = {"handle": [{"handler": "static_response", "status_code": status}]}
    if match:
        route["match"] = [match]
    return route


def _proxy(match, dial):
    return {"match": [match], "handle": [{"handler": "reverse_proxy", "upstreams": [{"dial": dial}]}]}


def _site(host, routes):
    return {
        "match": [{"host": [host]}],
        "handle": [{"handler": "subroute", "routes": [
            {"handle": [{"handler": "request_body", "max_size": 1048576}]},
            {"handle": [{"handler": "subroute", "routes": routes}]},
        ]}],
        "terminal": True,
    }


def hand_built_adapted() -> dict:
    """The adapted JSON the guards expect, built by hand in Caddy's shape (a `not` is a list of matcher
    sets). It documents the assumption the Docker-backed class checks against the real `caddy adapt`."""
    backchannel, idp = "10.120.0.0/24", "10.120.1.0/24"

    def dotdot():
        return _respond({"path_regexp": {"pattern": guards.DOTDOT_PATTERN}})

    def encoded():
        return _respond({"path_regexp": {"pattern": guards.ENCODED_PATH_PATTERN}})

    def outside(**more):
        return dict({"not": [{"remote_ip": {"ranges": [backchannel, idp]}}]}, **more)

    app = [
        dotdot(),
        _proxy({"remote_ip": {"ranges": [idp]}, "method": ["POST"], "path": ["/bff/backchannel-logout"]}, "bff:8080"),
        _respond({"path": ["/bff/backchannel-logout*"]}),
        _proxy(outside(), "bff:8080"),
        _respond(),
    ]
    id_8443 = [
        dotdot(),
        _respond({"path": list(guards.ADMIN_PATHS)}),
        encoded(),
        _proxy(outside(path=list(guards.PUBLISHED_ID_PATHS)), "keycloak:8080"),
        _proxy({"remote_ip": {"ranges": [backchannel]}, "path": list(guards.REALM_PATHS)}, "keycloak:8080"),
        _respond(),
    ]
    api = [
        dotdot(),
        _proxy({"remote_ip": {"ranges": [backchannel]}, "path": ["/api/*"]}, "api:8080"),
        _respond(),
    ]
    id_8444 = [
        dotdot(),
        encoded(),
        _proxy(outside(path=list(guards.ADMIN_LISTENER_PATHS)), "keycloak:8080"),
        _respond(),
    ]
    return {
        "admin": {"disabled": True},
        "apps": {
            "http": {"servers": {
                "srv0": {"listen": [":8443"], "routes": [_site(APP, app), _site(ID, id_8443), _site(API, api)]},
                "srv1": {"listen": [":8444"], "routes": [_site(ID, id_8444)]},
            }},
            "tls": {"automation": {"policies": [{"subjects": [APP, ID, API], "issuers": [{"module": "internal"}]}]}},
            "pki": {"certificate_authorities": {"local": {"install_trust": False}}},
        },
    }


class CaddyModelRules(EdgeRedCases, unittest.TestCase):
    """The #178 rules over a hand-built adapted JSON: runs without Docker."""

    @classmethod
    def setUpClass(cls):
        cls.adapted = hand_built_adapted()

    def problems(self, adapted=None):
        return guards.caddy_problems(adapted if adapted is not None else deep(self.adapted), FIXTURE_VALUES)


if __name__ == "__main__":
    unittest.main()
