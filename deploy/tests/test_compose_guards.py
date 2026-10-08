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

    # ----- #122: the key ring (G3 G4-122-03 b, c; G2 D10) and the BFF's rate-limit keys

    def test_the_bff_alone_gets_the_four_certificate_files(self):
        for service, svc in self.committed["services"].items():
            sources = {s["source"] for s in as_list(svc.get("secrets"))}
            self.assertEqual(set(guards.DP_SECRETS) <= sources, service == "bff", service)

    def test_each_key_ring_rule_can_fail(self):
        def volume(**extra):
            def mutate(c):
                c["volumes"]["bff-keyring"] = dict(c["volumes"].get("bff-keyring") or {}, **extra)
            return mutate

        flag = guards.GENERATOR_FLAG

        def give(name, secret):
            return lambda c: c["services"][name].setdefault("secrets", []).append({"source": secret, "target": secret})

        cases = [
            ("a volume driver", volume(driver="local"), "driver is not allowed"),
            ("volume driver options (a host path)", volume(driver_opts={"type": "none", "o": "bind", "device": "/srv/elsewhere"}), "driver_opts is not allowed"),
            ("a volume name override", volume(name="another_volume"), "name override"),
            ("the generator flag in a command", lambda c: c["services"]["bff"].update(command=["dotnet", "/app/Decisya.Bff.dll", flag]), "generator flag"),
            ("the generator flag in an entrypoint", lambda c: c["services"]["api"].update(entrypoint=["dotnet", "x.dll", flag]), "generator flag"),
            ("the generator flag in a healthcheck", lambda c: c["services"]["bff"].setdefault("healthcheck", {}).update(test=["CMD", "dotnet", "x.dll", flag]), "generator flag"),
            ("the certificate to the api", give("api", guards.DP_CERT), "differ from the D3 table"),
            ("the certificate password to keycloak", give("keycloak", guards.DP_CERT_PASSWORD), "differ from the D3 table"),
            ("the previous pair to the migrator", give("migrator", guards.DP_PREVIOUS), "differ from the D3 table"),
            ("the bff loses the previous password", lambda c: c["services"]["bff"].update(
                secrets=[s for s in c["services"]["bff"]["secrets"] if s["source"] != guards.DP_PREVIOUS_PASSWORD]), "differ from the D3 table"),
            ("a rate limit out of range", lambda c: set_env(c, "bff", "Bff__RateLimits__login__PermitLimit", "0"), "must be an integer from 1 to 100000"),
            ("a rate limit that is not a number", lambda c: set_env(c, "bff", "Bff__RateLimits__api__WindowSeconds", "${W}"), "must be an integer from 1 to 3600"),
            ("a misspelt rate-limit key", lambda c: set_env(c, "bff", "Bff__RateLimits__logins__PermitLimit", "5"), "not a rate-limit setting"),
            ("a rate limit on the api", lambda c: set_env(c, "api", "Bff__RateLimits__login__PermitLimit", "5"), "is a BFF setting"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_valid_rate_limit_keys_on_the_bff_pass(self):
        cfg = deep(self.committed)
        set_env(cfg, "bff", "Bff__RateLimits__login__PermitLimit", "20")
        set_env(cfg, "bff", "Bff__RateLimits__api__AnonymousPermitLimit", "90")
        self.assertEqual(self.problems(cfg), [])

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
            ("postgres ssl on (S-120-12)", lambda c: c["services"]["postgres"].update(command=["postgres", "-c", "ssl=on"]), "ssl=on"),
            ("redis tls port (S-120-12)", lambda c: c["services"]["redis"].update(command=["redis-server", "--tls-port", "6379"]), "tls- option"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    # ----- #121: the production realm (G2 D1 a, b, e, g; G3 G4-121-01 d, G4-121-02, G4-121-03 b)

    def test_keycloak_has_exactly_the_wrapper_the_realm_and_the_list_read_only(self):
        mounts = {v["target"]: v for v in as_list(self.committed["services"]["keycloak"].get("volumes"))}
        self.assertEqual(
            set(mounts),
            {"/opt/decisya/entrypoint-stack.sh", guards.REALM_TARGET, guards.PASSWORD_LIST_TARGET})
        for target, mount in mounts.items():
            self.assertEqual(mount.get("type"), "bind", target)
            self.assertIs(mount.get("read_only"), True, target)
        self.assertEqual(mounts[guards.REALM_TARGET]["source"], guards.REALM_SOURCE)
        self.assertEqual(mounts[guards.PASSWORD_LIST_TARGET]["source"], guards.PASSWORD_LIST_SOURCE)
        secrets = {s["source"] for s in as_list(self.committed["services"]["keycloak"].get("secrets"))}
        self.assertIn("Bff__Oidc__ClientSecret", secrets)

    def test_each_realm_mount_rule_can_fail(self):
        def kc(cfg):
            return cfg["services"]["keycloak"]

        def mount(cfg, target):
            return next(v for v in kc(cfg)["volumes"] if v.get("target") == target)

        def add(cfg, **entry):
            kc(cfg)["volumes"].append(entry)

        def drop(target):
            return lambda c: kc(c).update(volumes=[v for v in kc(c)["volumes"] if v.get("target") != target])

        folder = guards.IMPORT_DIR
        cases = [
            # The dev realm, or the folder that holds it, behind the import target: not in the bind table.
            ("a dev realm file at the target", lambda c: mount(c, guards.REALM_TARGET).update(source="./deploy/keycloak/dev-realm.json"), "unlisted"),
            ("the deploy/keycloak folder as the source", lambda c: mount(c, guards.REALM_TARGET).update(source="./deploy/keycloak"), "unlisted"),
            ("the folder as the target", lambda c: mount(c, guards.REALM_TARGET).update(target=folder), "import folder itself"),
            ("the folder with a trailing slash", lambda c: mount(c, guards.REALM_TARGET).update(target=folder + "/"), "import folder itself"),
            ("the folder with a dot segment", lambda c: mount(c, guards.REALM_TARGET).update(target=folder + "/."), "import folder itself"),
            ("a named volume as the folder", lambda c: add(c, type="volume", source="caddy-data", target=folder), "import folder itself"),
            ("a named volume at the realm target", lambda c: mount(c, guards.REALM_TARGET).update(type="volume", source="caddy-data"), "must be a bind mount"),
            ("a second file in the folder", lambda c: add(c, type="bind", source=guards.REALM_SOURCE, target=folder + "/other.json", read_only=True), "exactly one mount"),
            ("a tmpfs on the folder", lambda c: kc(c).setdefault("tmpfs", []).append(folder + ":size=1m"), "tmpfs may not target"),
            ("a writable realm file", lambda c: mount(c, guards.REALM_TARGET).update(read_only=False), "must be read-only"),
            ("a writable password list", lambda c: mount(c, guards.PASSWORD_LIST_TARGET).update(read_only=False), "must be read-only"),
            ("no realm file", drop(guards.REALM_TARGET), "exactly one mount"),
            ("no realm file (table)", drop(guards.REALM_TARGET), "expected bind mount"),
            ("no password list", drop(guards.PASSWORD_LIST_TARGET), "expected bind mount"),
            ("the list at another target", lambda c: mount(c, guards.PASSWORD_LIST_TARGET).update(target="/opt/keycloak/data/other.txt"), "unlisted"),
            ("the realm file under another name", lambda c: mount(c, guards.REALM_TARGET).update(source="./config/keycloak/other.json"), "unlisted"),
            ("the folder, anywhere above it", lambda c: add(c, type="bind", source="./config/keycloak", target="/opt/keycloak/data", read_only=True), "unlisted"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_every_launch_argument_stays_banned_for_keycloak(self):
        for bad in guards.KEYCLOAK_BANNED_ARGS:
            with self.subTest(argument=bad):
                self.assert_flagged(bad, lambda c, b=bad: c["services"]["keycloak"].update(command=[b]), "argument %s is not allowed" % bad)
                self.assert_flagged(bad, lambda c, b=bad: c["services"]["keycloak"].update(entrypoint=["/bin/sh", "/opt/decisya/entrypoint-stack.sh", b]), "entrypoint")
        self.assertEqual(len(guards.KEYCLOAK_BANNED_ARGS), 5)

    def test_the_keycloak_values_are_required_and_the_derived_ones_are_never_set(self):
        def env(name, key, value):
            return lambda c: set_env(c, name, key, value)

        cases = [
            ("app host not required", env("keycloak", "DECISYA_APP_HOST", "${DECISYA_APP_HOST}"), "DECISYA_APP_HOST must be a required reference"),
            ("app host missing", env("keycloak", "DECISYA_APP_HOST", None), "DECISYA_APP_HOST must be a required reference"),
            ("port not required", env("keycloak", "DECISYA_HTTPS_PORT", "8443"), "DECISYA_HTTPS_PORT must be a required reference"),
            ("port missing", env("keycloak", "DECISYA_HTTPS_PORT", None), "DECISYA_HTTPS_PORT must be a required reference"),
            ("the client secret set in Compose", env("keycloak", "DECISYA_BFF_CLIENT_SECRET", "x"), "wrapper derives it"),
            ("the origin set in Compose", env("keycloak", "DECISYA_REALM_APP_ORIGIN", "https://x.example.test"), "wrapper derives it"),
            ("the client secret on another service", env("bff", "DECISYA_BFF_CLIENT_SECRET", "x"), "wrapper derives it"),
            ("the client secret looks like a secret too", env("keycloak", "DECISYA_BFF_CLIENT_SECRET", "x"), "looks like a secret"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_the_keycloak_secret_table_can_fail(self):
        def without(cfg):
            kc = cfg["services"]["keycloak"]
            kc["secrets"] = [s for s in kc["secrets"] if s["source"] != "Bff__Oidc__ClientSecret"]

        def plus(name):
            return lambda c: c["services"]["keycloak"]["secrets"].append({"source": name, "target": name})

        def under_another_name(cfg):
            for s in cfg["services"]["keycloak"]["secrets"]:
                if s["source"] == "Bff__Oidc__ClientSecret":
                    s["target"] = "client_secret"

        cases = [
            ("the client secret missing", without, "differ from the D3 table"),
            ("the Redis password as well", plus("redis_acl"), "differ from the D3 table"),
            ("the migrator connection string as well", plus("ConnectionStrings__decisya"), "differ from the D3 table"),
            ("the client secret under another name", under_another_name, "under its own name"),
        ]
        for label, mutate, fragment in cases:
            with self.subTest(case=label):
                self.assert_flagged(label, mutate, fragment)

    def test_the_api_admin_mfa_switch_is_never_set_in_the_stack(self):
        # Even a value that keeps the check on is refused: the stack relies on the default, and a
        # false value would fail the Api's own start-up outside Development anyway (G4-121-01 d).
        for key in ("Authentication__RequireAdminMfa", "authentication__requireadminmfa", "AUTHENTICATION__REQUIREADMINMFA",
                    "Authentication:RequireAdminMfa",
                    # F-02: the host-prefixed forms that WebApplication.CreateBuilder loads with the prefix removed.
                    "ASPNETCORE_Authentication__RequireAdminMfa", "DOTNET_Authentication__RequireAdminMfa",
                    "ASPNETCORE_Authentication:RequireAdminMfa", "DOTNET_Authentication:RequireAdminMfa",
                    "aspnetcore_authentication__requireadminmfa", "DotNet_AUTHENTICATION__REQUIREADMINMFA"):
            for value in ("false", "true", "False", ""):
                with self.subTest(key=key, value=value):
                    self.assert_flagged(key, lambda c, k=key, v=value: set_env(c, "api", k, v), "must not be set in the stack")
        self.assert_flagged("on the bff", lambda c: set_env(c, "bff", "Authentication__RequireAdminMfa", "false"), "must not be set in the stack")

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


class AdminMfaKeyMatchTests(unittest.TestCase):
    """The key match behind guard g needs no Docker (F-02, G6 for #121)."""

    def test_the_match_strips_one_host_prefix_and_nothing_else(self):
        # Red rows: every form that reaches Authentication:RequireAdminMfa.
        for key in ("Authentication__RequireAdminMfa", "authentication:requireadminmfa", "ASPNETCORE_Authentication__RequireAdminMfa",
                    "DOTNET_Authentication__RequireAdminMfa", "ASPNETCORE_Authentication:RequireAdminMfa",
                    "DOTNET_Authentication:RequireAdminMfa", "aspnetcore_AUTHENTICATION__REQUIREADMINMFA"):
            with self.subTest(key=key):
                self.assertTrue(guards.is_admin_mfa_setting(key))
        # Green rows: keys that do not reach it (one prefix is removed, not two).
        for key in ("ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "DOTNET_gcServer", "Authentication__RequireAdminMfaX",
                    "XAuthentication__RequireAdminMfa", "ASPNETCORE_ASPNETCORE_Authentication__RequireAdminMfa",
                    "ASPNETCORE_DOTNET_Authentication__RequireAdminMfa", "ASPNETCOREAuthentication__RequireAdminMfa"):
            with self.subTest(key=key):
                self.assertFalse(guards.is_admin_mfa_setting(key))


if __name__ == "__main__":
    unittest.main()
