"""D10 guards 1 to 7 over the merged Compose configuration (issue #120, G3 G4-120-01 to 03).

Two forms of the same committed files are checked:
  - `--no-interpolate`, which is what CI can see (references stay `${KEY}` / `${KEY:?}`), and
  - the interpolated form of an assembled stack folder with documentation-range values, which is the
    form `stackctl.py check` runs on the real host.
The red cases then break a copy of the good configuration one rule at a time and expect the guard to
name that rule: a guard that cannot fail is not a guard.
"""
from __future__ import annotations

import unittest

from deploy_common import (
    FIXTURE_VALUES, GENERATED, OVERLAY, ROOT, DockerCase, as_list, clean_environment, compose_json, deep,
    make_stack, networks_dict, scratch_dir, stackctl, guards,
)


def set_env(cfg, service, key, value):
    """Set (or, with None, remove) an environment entry, whether Compose printed a dict or a KEY=VALUE list."""
    svc = cfg["services"][service]
    env = svc.get("environment")
    if isinstance(env, list):
        env = [e for e in env if str(e).partition("=")[0] != key]
        if value is not None:
            env.append("%s=%s" % (key, value))
        svc["environment"] = env
    else:
        env = dict(env or {})
        if value is None:
            env.pop(key, None)
        else:
            env[key] = value
        svc["environment"] = env


class MergedConfigGuards(DockerCase):
    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        cls._scratch = scratch_dir()
        parent = cls._scratch.__enter__()
        with clean_environment():
            cls.stack = make_stack(parent)
        gen, overlay = cls.stack / stackctl.GENERATED_NAME, cls.stack / stackctl.OVERLAY_NAME
        cls.scan_refs = guards.load_scan_refs(ROOT)
        cls.release_refs = {"api": FIXTURE_VALUES["DECISYA_API_IMAGE"], "bff": FIXTURE_VALUES["DECISYA_BFF_IMAGE"],
                            "migrator": FIXTURE_VALUES["DECISYA_MIGRATOR_IMAGE"]}
        # The assembled files are byte-identical to the committed ones (assemble checks the hash).
        cls.committed = compose_json([gen, overlay], project_dir=cls.stack, interpolate=False)
        cls.generated = compose_json([gen], project_dir=cls.stack, interpolate=False)
        cls.interpolated = compose_json([gen, overlay], env_file=cls.stack / stackctl.ENV_NAME, project_dir=cls.stack, interpolate=True)

    @classmethod
    def tearDownClass(cls):
        cls._scratch.__exit__(None, None, None)

    # ----- the good configuration

    def problems(self, cfg=None):
        return guards.config_problems(cfg if cfg is not None else deep(self.committed), interpolated=False, scan_refs=self.scan_refs)

    def test_committed_files_pass_every_guard_without_interpolation(self):
        self.assertEqual(self.problems(), [])

    def test_assembled_stack_passes_every_guard_with_fixture_values(self):
        problems = guards.config_problems(
            deep(self.interpolated), interpolated=True, values=FIXTURE_VALUES,
            scan_refs=self.scan_refs, release_refs=self.release_refs)
        self.assertEqual(problems, [])

    def test_generated_file_carries_no_overlay_key(self):
        self.assertEqual(guards.generated_problems(deep(self.generated)), [])

    def test_committed_yaml_text_has_no_banned_key_or_socket(self):
        for path in (GENERATED, OVERLAY):
            with self.subTest(file=path.name):
                self.assertEqual(guards.yaml_text_problems(path.name, path.read_text(encoding="utf-8")), [])

    def test_the_overlay_publishes_exactly_one_port_with_required_references(self):
        # Spot check of the raw form the guards compare against (D10 item 4).
        ports = as_list(self.committed["services"]["caddy"].get("ports"))
        self.assertEqual(len(ports), 1)

    # ----- red cases: each breaks one rule

    def assert_flagged(self, label, mutate, fragment):
        cfg = deep(self.committed)
        mutate(cfg)
        problems = self.problems(cfg)
        self.assertTrue(any(fragment in p for p in problems), "%s was not flagged (%s); got %s" % (label, fragment, problems))

    def test_each_isolation_rule_can_fail(self):
        svc = lambda cfg, name: cfg["services"][name]
        cases = [
            ("privileged", lambda c: svc(c, "api").update(privileged=True), "banned"),
            ("cap_add", lambda c: svc(c, "bff").update(cap_add=["NET_ADMIN"]), "banned"),
            ("host network", lambda c: svc(c, "redis").update(network_mode="host"), "banned"),
            ("host pid", lambda c: svc(c, "redis").update(pid="host"), "banned"),
            ("devices", lambda c: svc(c, "bff").update(devices=["/dev/kmsg"]), "banned"),
            ("env_file", lambda c: svc(c, "api").update(env_file=["x"]), "banned"),
            ("build", lambda c: svc(c, "api").update(build="."), "banned"),
            ("no no-new-privileges", lambda c: svc(c, "api").update(security_opt=[]), "no-new-privileges"),
            ("no cap_drop", lambda c: svc(c, "api").update(cap_drop=[]), "cap_drop"),
            ("writable root", lambda c: svc(c, "bff").update(read_only=False), "read_only"),
            ("root user", lambda c: svc(c, "api").update(user="0:0"), "non-root"),
            ("no memory limit", lambda c: svc(c, "api").pop("mem_limit"), "mem_limit"),
            ("memory ceiling", lambda c: svc(c, "bff").update(mem_limit=4 * 1024 ** 3), "ceiling"),
            ("no pids limit", lambda c: svc(c, "api").pop("pids_limit"), "pids_limit"),
            ("no log rotation", lambda c: svc(c, "api").update(logging={"driver": "json-file"}), "logging"),
            ("unknown key", lambda c: svc(c, "api").update(sysctls={"a": "b"}), "not allowed"),
            ("extra service", lambda c: c["services"].update(extra={"image": "x"}), "unexpected service"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_each_mount_rule_can_fail(self):
        def add_volume(name, **entry):
            return lambda c: c["services"][name].setdefault("volumes", []).append(entry)

        cases = [
            ("docker socket", add_volume("api", type="bind", source="/var/run/docker.sock", target="/var/run/docker.sock"), "Docker socket"),
            ("personal mount", add_volume("api", type="bind", source="./config/anything", target="/x", read_only=True), "unlisted"),
            ("caddy-data in the bff", add_volume("bff", type="volume", source="caddy-data", target="/data"), "not allowed here"),
            ("keyring in the api", add_volume("api", type="volume", source="bff-keyring", target="/k"), "not allowed here"),
            ("dev realm in keycloak", add_volume("keycloak", type="bind", source="./deploy/keycloak/dev-realm.json", target="/srv/import/r.json", read_only=True), "unlisted"),
            ("short syntax", lambda c: c["services"]["api"].setdefault("volumes", []).append("/a:/b"), "short syntax"),
            ("writable config bind", lambda c: [v.update(read_only=False) for v in c["services"]["caddy"]["volumes"] if v.get("type") == "bind"], "read-only"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_each_network_and_port_rule_can_fail(self):
        def api_on_edge(c):
            networks_dict(c["services"]["api"])["edge"] = None

        def bff_alias(c):
            networks_dict(c["services"]["bff"])["backchannel"] = {"aliases": ["x.example.test"]}

        def api_port(c):
            c["services"]["api"]["ports"] = ["8080:8080"]

        def caddy_second_port(c):
            c["services"]["caddy"]["ports"] = as_list(c["services"]["caddy"].get("ports")) + ["80:80"]

        def external_net(c):
            c["networks"]["pg-app"]["external"] = True

        cases = [
            ("api on edge", api_on_edge, "networks are"),
            ("alias outside the D2 table", bff_alias, "aliases"),
            ("port on the api", api_port, "publishes a port"),
            ("second caddy port", caddy_second_port, "exactly one port"),
            ("pg-app not internal", lambda c: c["networks"]["pg-app"].update(internal=False), "internal must be true"),
            ("edge internal", lambda c: c["networks"]["edge"].update(internal=True), "internal must be false"),
            ("external network", external_net, "external"),
            ("subnet pinned on a store network", lambda c: c["networks"]["cache"].update(ipam={"config": [{"subnet": "203.0.113.0/24"}]}), "must not pin"),
            ("committed subnet changed", lambda c: c["networks"]["backchannel"].update(ipam={"config": [{"subnet": "203.0.113.0/24"}]}), "committed subnet"),
            ("network missing", lambda c: c["networks"].pop("telemetry"), "top-level networks"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_each_secret_rule_can_fail(self):
        def give(name, secret):
            return lambda c: c["services"][name].setdefault("secrets", []).append({"source": secret, "target": secret})

        def env(name, key, value):
            return lambda c: set_env(c, name, key, value)

        cases = [
            ("api gets the owner connection string", give("api", "ConnectionStrings__decisya"), "differ from the D3 table"),
            ("api gets the superuser password", give("api", "postgres_superuser_password"), "differ from the D3 table"),
            ("bff gets a tenancy string", give("bff", "ConnectionStrings__tenancy"), "differ from the D3 table"),
            ("migrator loses a secret", lambda c: c["services"]["migrator"].update(secrets=c["services"]["migrator"]["secrets"][:-1]), "differ from the D3 table"),
            ("secret from outside ./secrets", lambda c: c["secrets"]["redis_acl"].update(file="/etc/redis_acl"), "must be the file"),
            ("secret from the environment", lambda c: c["secrets"]["redis_acl"].update(environment="X"), "must be the file"),
            ("password variable", env("api", "Db__Password", "x"), "looks like a secret"),
            ("token variable", env("api", "Some__Token", "x"), "looks like a secret"),
            ("otlp headers", env("api", "OTEL_EXPORTER_OTLP_HEADERS", "k=v"), "looks like a secret"),
            ("file form to an unmounted secret", env("api", "X_PASSWORD_FILE", "/run/secrets/redis_acl"), "does not mount"),
            ("password pair in a value", env("api", "Conn", "Host=x;Password=y"), "password="),
            ("url user info", env("api", "Conn", "https://user:pw@host/"), "URL user info"),
            ("host auth method", env("postgres", "POSTGRES_HOST_AUTH_METHOD", "trust"), "POSTGRES_HOST_AUTH_METHOD"),
            ("healthcheck password", lambda c: c["services"]["redis"]["healthcheck"].update(test=["CMD", "redis-cli", "-a", "x", "ping"]), "healthcheck"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_each_keycloak_production_and_image_rule_can_fail(self):
        cases = [
            ("start-dev", lambda c: c["services"]["keycloak"].update(command=["start-dev"]), "keycloak"),
            ("import realm", lambda c: c["services"]["keycloak"].update(command=["--import-realm"]), "keycloak"),
            ("other entrypoint", lambda c: c["services"]["keycloak"].update(entrypoint=["/opt/keycloak/bin/kc.sh"]), "entrypoint"),
            ("development environment", lambda c: set_env(c, "api", "ASPNETCORE_ENVIRONMENT", "Development"), "ASPNETCORE_ENVIRONMENT"),
            ("wildcard host", lambda c: set_env(c, "bff", "AllowedHosts", "*"), "AllowedHosts"),
            ("missing host", lambda c: set_env(c, "api", "AllowedHosts", None), "AllowedHosts"),
            ("caddy value not required", lambda c: set_env(c, "caddy", "DECISYA_LAN_SUBNET", "${DECISYA_LAN_SUBNET}"), "required reference"),
            ("tag only image", lambda c: c["services"]["caddy"].update(image="docker.io/library/caddy:2"), "ContainerImages"),
            ("release by tag", lambda c: c["services"]["api"].update(image="ghcr.io/mpcs2013/decisya-api:latest"), "placeholder"),
            ("other digest", lambda c: c["services"]["redis"].update(image="docker.io/library/redis:8@sha256:" + "0" * 64), "ContainerImages"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_generated_file_guard_can_fail(self):
        cfg = deep(self.generated)
        cfg["services"]["api"]["ports"] = ["1:1"]
        cfg["services"]["api"]["cap_add"] = ["ALL"]
        problems = guards.generated_problems(cfg)
        self.assertTrue(any("overlay key" in p for p in problems), problems)

    def test_interpolated_form_flags_a_wrong_published_address(self):
        cfg = deep(self.interpolated)
        cfg["services"]["caddy"]["ports"] = ["0.0.0.0:8443:8443"]
        problems = guards.config_problems(cfg, interpolated=True, values=FIXTURE_VALUES, scan_refs=self.scan_refs, release_refs=self.release_refs)
        self.assertTrue(any("published port" in p for p in problems), problems)

    def test_yaml_text_guard_can_fail(self):
        self.assertTrue(guards.yaml_text_problems("x", "services:\n  a:\n    privileged: true\n"))
        self.assertTrue(guards.yaml_text_problems("x", "    env_file: .env\n"))
        self.assertTrue(guards.yaml_text_problems("x", "    - /var/run/docker.sock:/var/run/docker.sock\n"))
        self.assertEqual(guards.yaml_text_problems("x", "# privileged: true is banned\n"), [])


if __name__ == "__main__":
    unittest.main()
