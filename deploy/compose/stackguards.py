#!/usr/bin/env python3
"""Guards shared by CI, `stackctl.py check` and `stackctl.py verify` (issue #120, G3 G4-120-03).

One module runs in three places, so what runs is what was guarded:
  (a) CI (deploy/tests), over `docker compose config --no-interpolate` of the committed files and
      over the interpolated configuration of an assembled folder with documentation-range values;
  (b) `stackctl.py check`, over the interpolated configuration of the real stack folder;
  (c) `stackctl.py verify`, over `docker inspect` of the running containers.

Stdlib only. Every function returns a list of problem strings. A problem names a key, a service or a
rule. It never contains a value from the stack's environment file or from its secrets: the bind
address is a home-network address, and a log line must not repeat it.

The tables below are the specification (architecture note D2, D3 and D10, threat model G4-120-01 to
05). Changing one is a security-relevant change: review it like the overlay itself.

Issue #121 (G2 D1, D5; G3 G4-121-02, 03, 05) adds the production realm: two read-only file mounts into
Keycloak, a scope guard for the realm file name, and the decision rules for the identity check that
`stackctl.py verify` and `realm rebuild` run. This module only checks and decides: it starts nothing.
The RealmGuard exemption for this exact path (docs/security/threat-models/realm-guard-scope.md,
"Amendment at G3 for #121") holds only while the text of this file carries none of the process-start
markers that amendment lists, so the Keycloak argument ban below is built from parts. Do not write
those words out here, not even in a comment.
"""
from __future__ import annotations

import importlib.util
import ipaddress
import json
import posixpath
import re
from pathlib import Path
from typing import Any

STACK_NAME = "decisya"

# #122 (G2 D10, G3 G4-122-03): the key ring's wrapping certificate is four BFF-only file secrets. The
# "previous" pair is empty when unused, and both of its files are empty together.
DP_CERT = "Bff__DataProtection__Certificate"
DP_CERT_PASSWORD = "Bff__DataProtection__CertificatePassword"
DP_PREVIOUS = "Bff__DataProtection__PreviousCertificate"
DP_PREVIOUS_PASSWORD = "Bff__DataProtection__PreviousCertificatePassword"
DP_SECRETS = (DP_CERT, DP_CERT_PASSWORD, DP_PREVIOUS, DP_PREVIOUS_PASSWORD)
# The BFF image's certificate generator mode. It prints a private key on stdout, so it is never a
# Compose command, entrypoint or healthcheck (G4-122-03 c).
GENERATOR_FLAG = "--generate-keyring-certificate"
# The key ring volume as the one-shot tools see it: the Compose volume key, the project-prefixed name
# Docker uses, the mount point inside the one-shot container, and the BFF's uid (the volume's owner).
KEYRING_VOLUME = "bff-keyring"
KEYRING_VOLUME_NAME = "%s_%s" % (STACK_NAME, KEYRING_VOLUME)
KEYRING_MOUNT_TARGET = "/keyring"
KEYRING_UID = 1654

HTTPS_PORT = 8443
# #178 (G2 D1, D2): Keycloak's admin console and the master realm have their own published port.
ADMIN_PORT = 8444
CADDY_BACKCHANNEL_ADDRESS = "10.120.0.2"
CADDY_IDP_ADDRESS = "10.120.1.2"
COMMITTED_SUBNETS = {"backchannel": "10.120.0.0/24", "idp": "10.120.1.0/24"}

HOST_KEYS = ("DECISYA_APP_HOST", "DECISYA_ID_HOST", "DECISYA_API_HOST")
IMAGE_KEYS = {
    "DECISYA_API_IMAGE": "api",
    "DECISYA_BFF_IMAGE": "bff",
    "DECISYA_MIGRATOR_IMAGE": "migrator",
}
# #178: DECISYA_LAN_SUBNET and DECISYA_WORKSTATION_ADDRESS are retired (the NAS firewall is the source
# control for published ports). A leftover key in an older .env is "not allowed" like any foreign key.
ENV_KEYS = (
    ("DECISYA_BIND_ADDRESS", "DECISYA_HTTPS_PORT", "DECISYA_ADMIN_PORT")
    + HOST_KEYS
    + tuple(IMAGE_KEYS)
)
CADDY_ENV_KEYS = HOST_KEYS

EXPECTED_SERVICES = ("api", "bff", "caddy", "keycloak", "migrator", "otel-collector", "postgres", "redis")
RELEASE_SERVICES = {"api": "DECISYA_API_IMAGE", "bff": "DECISYA_BFF_IMAGE", "migrator": "DECISYA_MIGRATOR_IMAGE"}
THIRD_PARTY_ALIAS = {
    "postgres": "postgres",
    "redis": "redis",
    "keycloak": "keycloak",
    "caddy": "caddy",
    "otel-collector": "otelcollector",
}

# D2 network table: which networks each service joins.
EXPECTED_NETWORKS = {
    "postgres": {"pg-app", "pg-kc"},
    "redis": {"cache"},
    "keycloak": {"idp", "pg-kc"},
    "caddy": {"edge", "backchannel", "idp"},
    "otel-collector": {"telemetry"},
    "migrator": {"pg-app", "telemetry"},
    "api": {"backchannel", "pg-app", "telemetry"},
    "bff": {"backchannel", "cache", "telemetry"},
}
TOP_LEVEL_NETWORKS = {"edge", "backchannel", "idp", "pg-app", "pg-kc", "cache", "telemetry"}
INTERNAL_NETWORKS = TOP_LEVEL_NETWORKS - {"edge"}

# #121 (G2 D1): the production realm reaches Keycloak as exactly one read-only file, never a folder, and
# the password list as exactly one read-only file. IMPORT_DIR is the container folder Keycloak reads
# realm files from; it is named here and nowhere else in Python (the RealmGuard exemption, see the
# module docstring).
IMPORT_DIR = "/opt/keycloak/data/import"
REALM_FILE_NAME = "realm-decisya.json"
REALM_SOURCE = "./config/keycloak/" + REALM_FILE_NAME
REALM_TARGET = IMPORT_DIR + "/" + REALM_FILE_NAME
PASSWORD_LIST_NAME = "common-passwords.txt"
PASSWORD_LIST_SOURCE = "./config/keycloak/" + PASSWORD_LIST_NAME
PASSWORD_LIST_TARGET = "/opt/keycloak/data/password-blacklists/" + PASSWORD_LIST_NAME
# The two values the wrapper validates and turns into the realm's placeholders (G4-121-03). Compose
# requires them with `:?`, as for Caddy.
KEYCLOAK_ENV_KEYS = ("DECISYA_APP_HOST", "DECISYA_HTTPS_PORT")
# The wrapper derives these two itself and refuses a preset value; no service may carry them.
WRAPPER_DERIVED_NAMES = ("DECISYA_BFF_CLIENT_SECRET", "DECISYA_REALM_APP_ORIGIN")
# The Api's own switch (`Authentication:RequireAdminMfa`) in its environment-variable form. Compared
# case-insensitively because .NET configuration is. The stack never sets it: the default is on (G4-121-01 d).
ADMIN_MFA_SETTING = "authentication__requireadminmfa"
# `WebApplication.CreateBuilder` also loads `ASPNETCORE_`- and `DOTNET_`-prefixed variables into the same
# configuration with the prefix removed, so those forms reach the same key (F-02, G6 for #121).
HOST_ENV_PREFIXES = ("aspnetcore_", "dotnet_")


def is_admin_mfa_setting(key: str) -> bool:
    """True when this environment key reaches `Authentication:RequireAdminMfa` (one host prefix is removed)."""
    name = key.lower()
    for prefix in HOST_ENV_PREFIXES:
        if name.startswith(prefix):
            name = name[len(prefix):]
            break
    return name.replace(":", "__") == ADMIN_MFA_SETTING


# Arguments the wrapper must never be given. Built from parts: see the module docstring.
KEYCLOAK_BANNED_ARGS = ("start" + "-dev", "--" + "import" + "-realm", "import", "export", "build")

# D3 secret table: the exact set of secret files each service receives (G4-120-01, T120-03).
SECRET_CONSUMERS = {
    "postgres": {"postgres_superuser_password", "migrator_db_password", "keycloak_db_password"},
    "redis": {"redis_acl"},
    "keycloak": {"keycloak_db_password", "keycloak_bootstrap_admin_password", "Bff__Oidc__ClientSecret"},
    "caddy": set(),
    "otel-collector": set(),
    "migrator": {
        "ConnectionStrings__decisya",
        "Migrator__TenancyRolePassword",
        "Migrator__EntitlementsRolePassword",
        "Decisya__Observability__UserIdHashKey",
    },
    "api": {
        "ConnectionStrings__tenancy",
        "ConnectionStrings__entitlements",
        "Decisya__Observability__UserIdHashKey",
    },
    "bff": {
        "ConnectionStrings__redis",
        "Bff__Oidc__ClientSecret",
        "Decisya__Observability__UserIdHashKey",
        # #122: the key ring's wrapping certificate pair and the previous pair. BFF only (G4-122-03).
        *DP_SECRETS,
    },
}
ALL_SECRETS = set().union(*SECRET_CONSUMERS.values())

# Bind mounts (R9): the only host paths a container may see, each into one service only.
BIND_MOUNTS = {
    "postgres": {"./config/postgres/init": "/docker-entrypoint-initdb.d"},
    "redis": {"./config/redis/redis.conf": "/usr/local/etc/redis/redis.conf"},
    # #121: the wrapper and exactly the two files above. Any other source or target, the dev realm, the
    # `deploy/keycloak` folder or a folder target, is "a bind mount of an unlisted path".
    "keycloak": {
        "./config/keycloak/entrypoint-stack.sh": "/opt/decisya/entrypoint-stack.sh",
        REALM_SOURCE: REALM_TARGET,
        PASSWORD_LIST_SOURCE: PASSWORD_LIST_TARGET,
    },
    "caddy": {"./config/caddy/Caddyfile": "/etc/caddy/Caddyfile"},
    "otel-collector": {"./config/otel-collector/config.yaml": "/etc/otelcol/config.yaml"},
}
# Named volumes: service -> volume -> target. Each volume belongs to one service and is read-write
# there. (A `backup` service, #30, may later add a read-only bff-keyring mount: update this table then.)
NAMED_VOLUMES = {
    "postgres": {"postgres-data": "/var/lib/postgresql"},
    "caddy": {"caddy-data": "/data"},
    "bff": {"bff-keyring": "/home/app/keyring"},
}
ALL_NAMED_VOLUMES = {"postgres-data", "caddy-data", "bff-keyring"}
CONFIG_CONSUMERS = {"bff": {"caddy-root"}, "api": {"caddy-root"}, "keycloak": {"caddy-root"}}
CADDY_ROOT_TARGET = "/etc/decisya/trust/caddy-root.crt"

ALLOWED_TOP_KEYS = {"name", "services", "networks", "volumes", "secrets", "configs"}
ALLOWED_SERVICE_KEYS = {
    "image", "command", "entrypoint", "environment", "depends_on", "healthcheck", "networks", "user",
    "cap_drop", "security_opt", "read_only", "tmpfs", "mem_limit", "cpus", "pids_limit", "volumes",
    "configs", "secrets", "ports", "logging", "restart",
}
# Keys that must never appear on a service or at the top level, whatever their value (D10 item 3,
# G4-120-01). `env_file` loads values into the container environment, so they show in `docker inspect`.
BANNED_KEYS = {
    "env_file", "build", "include", "extends", "privileged", "cap_add", "devices", "device_cgroup_rules",
    "network_mode", "pid", "ipc", "uts", "userns_mode", "cgroup", "cgroup_parent", "volumes_from",
}
# Overlay-owned keys, which the generated file must never carry (D1).
GENERATED_ALLOWED_SERVICE_KEYS = {"image", "command", "entrypoint", "environment", "depends_on", "healthcheck", "networks"}

MEMORY_CEILING_BYTES = int(3.5 * 1024 ** 3)
NO_NEW_PRIVILEGES = ("no-new-privileges:true", "no-new-privileges=true", "no-new-privileges")

SECRET_NAME_RE = re.compile(r"PASSWORD|SECRET|KEY|TOKEN|CONNECTIONSTRING|HEADERS|CREDENTIAL|ASPIRE_DASHBOARD", re.IGNORECASE)
# Names that match SECRET_NAME_RE by accident and hold no secret: the key ring's folder, and Keycloak's
# proxy-header mode (`xforwarded`). Adding a name here is a reviewed change.
NON_SECRET_NAMES = {"Bff__DataProtection__KeyRingPath", "KC_PROXY_HEADERS"}
USERINFO_RE = re.compile(r"://[^/@\s]+:[^/@\s]+@")


# --------------------------------------------------------------------------- small helpers

def _list(value: Any) -> list:
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def _network(text: str, strict: bool = True):
    try:
        return ipaddress.ip_network(text, strict=strict)
    except ValueError:
        return None


def _norm_net(text: Any) -> str:
    net = _network(str(text), strict=False)
    return str(net) if net is not None else str(text)


def _ref(key: str, interpolated: bool, values: dict, required: bool = False) -> str:
    """What a `${KEY}` reference looks like in the configuration under test."""
    if interpolated:
        return str(values.get(key, ""))
    return "${%s%s}" % (key, ":?" if required else "")


def _service_networks(service: dict) -> dict:
    nets = service.get("networks")
    if isinstance(nets, list):
        return {str(n): {} for n in nets}
    return {str(k): (v or {}) for k, v in (nets or {}).items()}


def _environment(service: dict) -> dict:
    env = service.get("environment")
    if isinstance(env, list):
        out = {}
        for item in env:
            key, _, value = str(item).partition("=")
            out[key] = value
        return out
    return {str(k): ("" if v is None else str(v)) for k, v in (env or {}).items()}


def _secret_entries(service: dict) -> list[tuple[str, str]]:
    entries = []
    for item in _list(service.get("secrets")):
        if isinstance(item, str):
            entries.append((item, item))
        else:
            source = str(item.get("source", ""))
            entries.append((source, str(item.get("target") or source)))
    return entries


def mem_bytes(value: Any) -> int | None:
    if value is None:
        return None
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return int(value)
    match = re.fullmatch(r"([0-9]+(?:\.[0-9]+)?)\s*([a-zA-Z]*)", str(value).strip())
    if not match:
        return None
    units = {"": 1, "b": 1, "k": 1024, "kb": 1024, "m": 1024 ** 2, "mb": 1024 ** 2, "g": 1024 ** 3, "gb": 1024 ** 3}
    unit = match.group(2).lower()
    if unit not in units:
        return None
    return int(float(match.group(1)) * units[unit])


# --------------------------------------------------------------------------- environment file values (G4-120-02)

HOST_RE = re.compile(r"[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+")
IMAGE_RE = re.compile(r"ghcr\.io/mpcs2013/decisya-(api|bff|migrator)@sha256:[0-9a-f]{64}")


def value_problem(key: str, value: str) -> str | None:
    """The reason a value is refused, without the value."""
    if key in HOST_KEYS:
        if len(value) > 253 or not HOST_RE.fullmatch(value):
            return "must be a lowercase DNS name: letters, digits and hyphens, at least two labels"
    elif key == "DECISYA_HTTPS_PORT":
        if not re.fullmatch(r"[0-9]{1,5}", value):
            return "must be an integer"
        if int(value) != HTTPS_PORT:
            return "must be 8443 (the back channel reaches Caddy on 8443)"
    elif key == "DECISYA_ADMIN_PORT":
        if not re.fullmatch(r"[0-9]{1,5}", value):
            return "must be an integer"
        if int(value) != ADMIN_PORT:
            return "must be 8444 (Caddy's admin listener and Keycloak's admin URL use it)"
    elif key == "DECISYA_BIND_ADDRESS":
        if not re.fullmatch(r"[0-9.]+", value):
            return "must be one IPv4 address"
        try:
            ip = ipaddress.IPv4Address(value)
        except ValueError:
            return "must be one IPv4 address"
        if ip.is_unspecified or ip.is_multicast or ip.is_reserved or ip.is_link_local:
            return "must be a single unicast address, never 0.0.0.0"
    elif key in IMAGE_KEYS:
        match = IMAGE_RE.fullmatch(value)
        if not match or match.group(1) != IMAGE_KEYS[key]:
            return "must be ghcr.io/mpcs2013/decisya-%s@sha256:<64 hex>" % IMAGE_KEYS[key]
    else:
        return "is not a key of the stack's environment file"
    return None


def parse_env_text(text: str) -> tuple[dict, list[str]]:
    """Parse the stack's environment file. Messages carry line numbers and key names only."""
    values: dict = {}
    problems: list[str] = []
    for number, raw in enumerate(text.lstrip("﻿").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            problems.append("environment file line %d: not KEY=VALUE" % number)
            continue
        key, _, value = line.partition("=")
        key, value = key.strip(), value.strip()
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            problems.append("environment file line %d: not a valid key name" % number)
            continue
        if key in values:
            problems.append("environment file line %d: duplicate key %s" % (number, key))
            continue
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            problems.append("%s: quotes are not allowed" % key)
            continue
        values[key] = value
    return values, problems


def env_problems(values: dict) -> list[str]:
    """Every key present, non-empty and valid; no key outside the allow-list (so no COMPOSE_*)."""
    problems: list[str] = []
    valid: dict = {}
    for key in ENV_KEYS:
        if key not in values:
            problems.append("%s: missing" % key)
        elif values[key] == "":
            problems.append("%s: empty" % key)
        else:
            reason = value_problem(key, values[key])
            if reason:
                problems.append("%s: %s" % (key, reason))
            else:
                valid[key] = values[key]
    for key in sorted(values):
        if key not in ENV_KEYS:
            problems.append("%s: not allowed in the stack's environment file (only the documented keys)" % key)
    hosts = [valid[k] for k in HOST_KEYS if k in valid]
    if len(hosts) == len(HOST_KEYS) and len(set(hosts)) != len(hosts):
        problems.append("DECISYA_APP_HOST, DECISYA_ID_HOST and DECISYA_API_HOST must all be different")
    bind = valid.get("DECISYA_BIND_ADDRESS")
    if bind:
        for name, cidr in COMMITTED_SUBNETS.items():
            if ipaddress.ip_address(bind) in ipaddress.ip_network(cidr):
                problems.append("DECISYA_BIND_ADDRESS lies inside the committed %s network" % name)
    return problems


# --------------------------------------------------------------------------- the merged configuration (D10 items 3 to 7)

def config_problems(
    cfg: dict,
    *,
    interpolated: bool,
    values: dict | None = None,
    scan_refs: dict | None = None,
    release_refs: dict | None = None,
) -> list[str]:
    """Guards over `docker compose -f <generated> -f <overlay> config --no-path-resolution --format json`.

    interpolated=False is the CI form (`--no-interpolate`): references stay `${KEY}` or `${KEY:?}`.
    interpolated=True takes the resolved values (documentation-range fixture, or the real stack).
    """
    values = values or {}
    services = cfg.get("services") or {}
    problems: list[str] = []
    problems += _top_level_problems(cfg)
    problems += _service_set_problems(services)
    problems += _isolation_problems(services)
    problems += _mount_problems(cfg, services)
    problems += _limit_problems(services)
    problems += _network_problems(cfg, services, interpolated, values)
    problems += _port_problems(services, interpolated, values)
    problems += _secret_problems(cfg, services)
    problems += _environment_problems(services, interpolated, values)
    problems += _generator_flag_problems(services)
    problems += _keycloak_problems(services, interpolated, values)
    problems += tls_problems(services)
    problems += _image_problems(services, interpolated, values, scan_refs, release_refs)
    return problems


# S-120-12: the image-scan exceptions for OpenSSL in postgres (libssl not reached) and redis (libssl
# not reached) hold only while TLS stays off in both.
POSTGRES_SSL_ON_RE = re.compile(r"(?i)(?:^|[\s\-,;'\"])ssl\s*=\s*['\"]?(?:on|true|yes|1)\b")
REDIS_TLS_RE = re.compile(r"(?i)(?:^|[\s;'\"])(?:--)?tls-[a-z-]+")


def _launch_strings(service: dict) -> list[str]:
    """Every string that can switch a server option on: command, entrypoint and environment values."""
    items = [str(i) for i in _list(service.get("command")) + _list(service.get("entrypoint"))]
    return items + list(_environment(service).values())


def tls_problems(services: dict) -> list[str]:
    """No `ssl=on` for postgres, no `tls-` option for redis (S-120-12)."""
    problems = []
    for text in _launch_strings(services.get("postgres", {})):
        if POSTGRES_SSL_ON_RE.search(text):
            problems.append("service postgres: ssl=on is not allowed (the OpenSSL exceptions assume TLS is off, S-120-12)")
            break
    for text in _launch_strings(services.get("redis", {})):
        if REDIS_TLS_RE.search(text):
            problems.append("service redis: a tls- option is not allowed (the OpenSSL exceptions assume TLS is off, S-120-12)")
            break
    return problems


def redis_conf_problems(text: str) -> list[str]:
    """No `tls-` directive in redis.conf, comments excluded (S-120-12)."""
    for line in text.splitlines():
        code = line.strip()
        if code and not code.startswith("#") and re.match(r"(?i)tls-[a-z-]+", code):
            return ["redis.conf: a tls- directive is not allowed (the OpenSSL exceptions assume TLS is off, S-120-12)"]
    return []


def _top_level_problems(cfg: dict) -> list[str]:
    problems = []
    # `x-*` extension keys (the overlay's x-common anchor holder) carry no configuration of their own.
    for key in sorted(k for k in set(cfg) - ALLOWED_TOP_KEYS if not str(k).startswith("x-")):
        problems.append("top-level key %s is not allowed" % key)
    for key in sorted(set(cfg) & BANNED_KEYS):
        problems.append("top-level key %s is banned" % key)
    if cfg.get("name") != STACK_NAME:
        problems.append("project name must be %s" % STACK_NAME)
    volumes = cfg.get("volumes") or {}
    if set(volumes) != ALL_NAMED_VOLUMES:
        problems.append("top-level volumes are %s, expected %s" % (sorted(volumes), sorted(ALL_NAMED_VOLUMES)))
    for name, volume in volumes.items():
        volume = volume or {}
        if volume.get("external"):
            problems.append("volume %s is external" % name)
        # G4-122-03 b: a named volume is a Docker-managed volume and nothing else. `driver` and
        # `driver_opts` could turn it into a bind of a host path, which the one-shot key-ring tools
        # (root, with file capabilities) would then change. A name override could point at another volume.
        for key in ("driver", "driver_opts"):
            if key in volume:
                problems.append("volume %s: %s is not allowed (a named volume is never a host path)" % (name, key))
        declared = volume.get("name")
        if declared is not None and declared not in (name, "%s_%s" % (STACK_NAME, name)):
            problems.append("volume %s: a name override is not allowed" % name)
    return problems


def _generator_flag_problems(services: dict) -> list[str]:
    """G4-122-03 c: the BFF image's certificate generator prints a private key on stdout. A Compose
    command, entrypoint or healthcheck that names its flag would write that key into the container log."""
    problems = []
    flag = GENERATOR_FLAG.lower()
    for name, svc in services.items():
        items = [str(i) for i in _list(svc.get("command")) + _list(svc.get("entrypoint"))]
        items += [str(i) for i in _list((svc.get("healthcheck") or {}).get("test"))]
        if any(flag in item.lower() for item in items):
            problems.append(
                "service %s: the key-ring certificate generator flag is not allowed in command, entrypoint or healthcheck" % name)
    return problems


# #122 (G1 Q1, G2 D5): the optional per-class limits a stack may set on the BFF. Same ranges as the BFF's
# own validator, so a typo or a value out of range is a `check` problem and not a start-up failure.
RATELIMIT_CLASSES = ("login", "backchannel_logout", "api", "admin")
_RATELIMIT_RANGES = {"permitlimit": (1, 100000), "windowseconds": (1, 3600)}


def ratelimit_env_problem(service: str, key: str, value: str) -> str | None:
    """None when the environment entry is not a rate-limit key or is a valid one; else the reason."""
    name = key.replace(":", "__")
    if not name.lower().startswith("bff__ratelimits__"):
        return None
    if service != "bff":
        return "service %s: %s is a BFF setting" % (service, key)
    parts = name.split("__")
    allowed = dict(_RATELIMIT_RANGES)
    if len(parts) == 4 and parts[2].lower() == "api":
        allowed["anonymouspermitlimit"] = _RATELIMIT_RANGES["permitlimit"]
    if len(parts) != 4 or parts[2].lower() not in RATELIMIT_CLASSES or parts[3].lower() not in allowed:
        return "service bff: %s is not a rate-limit setting (Bff__RateLimits__<class>__PermitLimit or __WindowSeconds, and __AnonymousPermitLimit for api)" % key
    low, high = allowed[parts[3].lower()]
    if not re.fullmatch(r"[0-9]{1,6}", value) or not low <= int(value) <= high:
        return "service bff: %s must be an integer from %d to %d" % (key, low, high)
    return None


def _service_set_problems(services: dict) -> list[str]:
    problems = []
    for name in sorted(set(services) - set(EXPECTED_SERVICES)):
        problems.append("unexpected service %s" % name)
    for name in sorted(set(EXPECTED_SERVICES) - set(services)):
        problems.append("service %s is missing" % name)
    for name, svc in services.items():
        for key in sorted(k for k in set(svc) - ALLOWED_SERVICE_KEYS if not str(k).startswith("x-")):
            problems.append("service %s: key %s is not allowed" % (name, key))
        for key in sorted(set(svc) & BANNED_KEYS):
            problems.append("service %s: key %s is banned" % (name, key))
    return problems


def _isolation_problems(services: dict) -> list[str]:
    problems = []
    for name, svc in services.items():
        if not any(str(opt) in NO_NEW_PRIVILEGES for opt in _list(svc.get("security_opt"))):
            problems.append("service %s: security_opt must hold no-new-privileges:true" % name)
        dropped = {str(c).upper() for c in _list(svc.get("cap_drop"))}
        if "ALL" not in dropped and "NET_RAW" not in dropped:
            problems.append("service %s: cap_drop must hold ALL (at least NET_RAW)" % name)
        # Caddy is the one root service: no capability at all, read-only root, one volume (B-2).
        user = str(svc.get("user") or "")
        if name != "caddy" and user.split(":")[0] in ("", "0", "root"):
            problems.append("service %s: must run as a non-root user" % name)
        # Keycloak augments the server on first start and is the one writable root filesystem.
        if name != "keycloak" and svc.get("read_only") is not True:
            problems.append("service %s: read_only must be true" % name)
        if not svc.get("restart"):
            problems.append("service %s: restart policy is missing" % name)
    return problems


def _mount_problems(cfg: dict, services: dict) -> list[str]:
    problems = []
    for name, svc in services.items():
        wanted_binds = dict(BIND_MOUNTS.get(name, {}))
        wanted_volumes = dict(NAMED_VOLUMES.get(name, {}))
        for item in _list(svc.get("volumes")):
            if not isinstance(item, dict):
                problems.append("service %s: a volume uses the short syntax (use the long syntax)" % name)
                continue
            kind = item.get("type")
            source = str(item.get("source", ""))
            target = str(item.get("target", ""))
            if "docker.sock" in source or "docker.sock" in target:
                problems.append("service %s: the Docker socket is mounted" % name)
                continue
            if kind == "bind":
                if wanted_binds.get(source) != target:
                    problems.append("service %s: bind mount of an unlisted path or target" % name)
                else:
                    wanted_binds.pop(source)
                    if item.get("read_only") is not True:
                        problems.append("service %s: bind mount %s must be read-only" % (name, source))
            elif kind == "volume":
                if wanted_volumes.get(source) != target:
                    problems.append("service %s: named volume %s is not allowed here" % (name, source))
                else:
                    wanted_volumes.pop(source)
                    if item.get("read_only") is True:
                        problems.append("service %s: volume %s must be read-write" % (name, source))
            elif kind == "tmpfs":
                continue
            else:
                problems.append("service %s: mount type %s is not allowed" % (name, kind))
        for source in wanted_binds:
            problems.append("service %s: expected bind mount %s is missing" % (name, source))
        for source in wanted_volumes:
            problems.append("service %s: expected volume %s is missing" % (name, source))
        for item in _list(svc.get("tmpfs")):
            if "docker.sock" in str(item):
                problems.append("service %s: tmpfs names the Docker socket" % name)
        # configs: only the exported public root, only into its three consumers.
        got = set()
        for item in _list(svc.get("configs")):
            source = str(item if isinstance(item, str) else item.get("source", ""))
            target = CADDY_ROOT_TARGET if isinstance(item, str) else str(item.get("target", ""))
            got.add(source)
            if target != CADDY_ROOT_TARGET:
                problems.append("service %s: config %s is mounted at an unexpected target" % (name, source))
        if got != CONFIG_CONSUMERS.get(name, set()):
            problems.append("service %s: configs are %s, expected %s" % (name, sorted(got), sorted(CONFIG_CONSUMERS.get(name, set()))))
    top = cfg.get("configs") or {}
    if set(top) != {"caddy-root"}:
        problems.append("top-level configs must be exactly caddy-root")
    for name, entry in top.items():
        entry = entry or {}
        if entry.get("file") != "./trust/caddy-root.crt" or set(entry) - {"file", "name"}:
            problems.append("config %s must be the file ./trust/caddy-root.crt" % name)
    return problems


def _limit_problems(services: dict) -> list[str]:
    problems = []
    total = 0
    for name, svc in services.items():
        memory = mem_bytes(svc.get("mem_limit"))
        if not memory or memory <= 0:
            problems.append("service %s: mem_limit is missing" % name)
        else:
            total += memory
        try:
            if float(svc.get("cpus")) <= 0:
                raise ValueError
        except (TypeError, ValueError):
            problems.append("service %s: cpus is missing" % name)
        try:
            if int(svc.get("pids_limit")) <= 0:
                raise ValueError
        except (TypeError, ValueError):
            problems.append("service %s: pids_limit is missing" % name)
        logging = svc.get("logging") or {}
        options = logging.get("options") or {}
        if logging.get("driver") != "json-file" or not options.get("max-size") or not options.get("max-file"):
            problems.append("service %s: logging must be json-file with max-size and max-file" % name)
    if total > MEMORY_CEILING_BYTES:
        problems.append("memory limits sum to %d MiB, more than the 3.5 GiB ceiling" % (total // 1024 ** 2))
    return problems


def _network_problems(cfg: dict, services: dict, interpolated: bool, values: dict) -> list[str]:
    problems = []
    top = cfg.get("networks") or {}
    if set(top) != TOP_LEVEL_NETWORKS:
        problems.append("top-level networks are %s, expected %s" % (sorted(top), sorted(TOP_LEVEL_NETWORKS)))
    for name, net in top.items():
        net = net or {}
        if net.get("external"):
            problems.append("network %s is external" % name)
        if (net.get("driver") or "bridge") != "bridge":
            problems.append("network %s: driver must be bridge" % name)
        want_internal = name in INTERNAL_NETWORKS
        if bool(net.get("internal")) != want_internal:
            problems.append("network %s: internal must be %s" % (name, str(want_internal).lower()))
        subnets = [c.get("subnet") for c in ((net.get("ipam") or {}).get("config") or [])]
        committed = COMMITTED_SUBNETS.get(name)
        if committed:
            if subnets != [committed]:
                problems.append("network %s: the committed subnet is missing or changed" % name)
        elif subnets:
            problems.append("network %s must not pin a subnet (Docker assigns it)" % name)
    id_host = _ref("DECISYA_ID_HOST", interpolated, values, True)
    api_host = _ref("DECISYA_API_HOST", interpolated, values, True)
    app_host = _ref("DECISYA_APP_HOST", interpolated, values, True)
    fixed = {
        ("caddy", "backchannel"): (CADDY_BACKCHANNEL_ADDRESS, sorted([id_host, api_host])),
        ("caddy", "idp"): (CADDY_IDP_ADDRESS, [app_host]),
    }
    for name, svc in services.items():
        nets = _service_networks(svc)
        wanted = EXPECTED_NETWORKS.get(name)
        if wanted is not None and set(nets) != wanted:
            problems.append("service %s: networks are %s, expected %s" % (name, sorted(nets), sorted(wanted)))
        for net_name, conf in nets.items():
            aliases = sorted(str(a) for a in _list(conf.get("aliases")))
            address = conf.get("ipv4_address")
            want_address, want_aliases = fixed.get((name, net_name), (None, []))
            if address != want_address or conf.get("ipv6_address"):
                problems.append("service %s on %s: fixed address differs from the D2 table" % (name, net_name))
            if aliases != want_aliases:
                problems.append("service %s on %s: aliases differ from the D2 table" % (name, net_name))
            extra = set(conf) - {"aliases", "ipv4_address"}
            if extra:
                problems.append("service %s on %s: unexpected network keys %s" % (name, net_name, sorted(extra)))
    return problems


def _port_problems(services: dict, interpolated: bool, values: dict) -> list[str]:
    problems = []
    for name, svc in services.items():
        ports = _list(svc.get("ports"))
        if name != "caddy":
            if ports:
                problems.append("service %s publishes a port; only caddy may" % name)
            continue
        # G-C1 (#178): exactly two tcp entries, each on the bind address, each with a required reference.
        bind = _ref("DECISYA_BIND_ADDRESS", interpolated, values, True)
        https = _ref("DECISYA_HTTPS_PORT", interpolated, values, True)
        admin = _ref("DECISYA_ADMIN_PORT", interpolated, values, True)
        if len(ports) != 2:
            problems.append("caddy must publish exactly two ports (the HTTPS port and the admin port)")
            continue
        wanted = sorted(["%s:%s:%d" % (bind, https, HTTPS_PORT), "%s:%s:%d" % (bind, admin, ADMIN_PORT)])
        got = sorted(_port_text(entry) for entry in ports)
        if got != wanted:
            problems.append("caddy's published ports must be exactly BIND_ADDRESS:HTTPS_PORT:8443 and BIND_ADDRESS:ADMIN_PORT:8444")
    return problems


def _port_text(entry: Any) -> str:
    """One published-port entry as the short form `host_ip:published:target`; anything but tcp is marked."""
    if isinstance(entry, str):
        return entry
    text = "%s:%s:%s" % (entry.get("host_ip", ""), entry.get("published", ""), entry.get("target", ""))
    protocol = str(entry.get("protocol", "tcp"))
    return text if protocol == "tcp" else "%s (%s)" % (text, protocol)


def _secret_problems(cfg: dict, services: dict) -> list[str]:
    problems = []
    top = cfg.get("secrets") or {}
    for name, entry in top.items():
        entry = entry or {}
        if entry.get("file") != "./secrets/%s" % name or set(entry) - {"file", "name"}:
            problems.append("secret %s must be the file ./secrets/%s" % (name, name))
    if set(top) != ALL_SECRETS:
        problems.append("top-level secrets differ from the D3 table")
    used = set()
    for name, svc in services.items():
        entries = _secret_entries(svc)
        sources = {source for source, _ in entries}
        used |= sources
        wanted = SECRET_CONSUMERS.get(name, set())
        if sources != wanted:
            extra = sorted(sources - wanted)
            lacking = sorted(wanted - sources)
            problems.append("service %s: secrets differ from the D3 table (extra %s, missing %s)" % (name, extra, lacking))
        for source, target in entries:
            if source != target:
                problems.append("service %s: secret %s must mount under its own name" % (name, source))
            if source not in top:
                problems.append("service %s: secret %s is not defined" % (name, source))
    for name in sorted(set(top) - used):
        problems.append("secret %s is defined but no service uses it" % name)
    return problems


def _environment_problems(services: dict, interpolated: bool, values: dict) -> list[str]:
    problems = []
    for name, svc in services.items():
        env = _environment(svc)
        targets = {target for _, target in _secret_entries(svc)}
        for key, value in env.items():
            if key == "POSTGRES_HOST_AUTH_METHOD":
                problems.append("service %s: POSTGRES_HOST_AUTH_METHOD is banned" % name)
            # G4-121-01 d: the Api's MFA switch is never set in the stack; its default (on) applies.
            if is_admin_mfa_setting(key):
                problems.append("service %s: %s must not be set in the stack (the default is on; only Development turns it off)" % (name, key))
            limit_problem = ratelimit_env_problem(name, key, value)
            if limit_problem:
                problems.append(limit_problem)
            # G4-121-03 b: the wrapper derives these two and dies when it finds one preset.
            if key in WRAPPER_DERIVED_NAMES:
                problems.append("service %s: environment %s must not be set (the Keycloak wrapper derives it)" % (name, key))
            if SECRET_NAME_RE.search(key):
                file_form = key.endswith("_FILE") and value.startswith("/run/secrets/")
                if not file_form and key not in NON_SECRET_NAMES:
                    problems.append("service %s: environment name %s looks like a secret" % (name, key))
                if file_form and value[len("/run/secrets/"):] not in targets:
                    problems.append("service %s: %s points at a secret the service does not mount" % (name, key))
            if re.search("password=", value, re.IGNORECASE):
                problems.append("service %s: environment %s holds a password= pair" % (name, key))
            if USERINFO_RE.search(value):
                problems.append("service %s: environment %s holds URL user info" % (name, key))
            if "development" in value.lower():
                problems.append("service %s: environment %s names Development" % (name, key))
        for item in _list((svc.get("healthcheck") or {}).get("test")):
            if re.search("password|secret|token", str(item), re.IGNORECASE):
                problems.append("service %s: healthcheck carries a credential-like argument" % name)
        test = [str(i) for i in _list((svc.get("healthcheck") or {}).get("test"))]
        for index, item in enumerate(test):
            if item in ("-a", "--pass") and index + 1 < len(test) and test[index + 1] != "":
                problems.append("service %s: healthcheck passes a non-empty password" % name)
    for name, key in (("api", "ASPNETCORE_ENVIRONMENT"), ("bff", "ASPNETCORE_ENVIRONMENT"), ("migrator", "DOTNET_ENVIRONMENT")):
        if _environment(services.get(name, {})).get(key) != "Production":
            problems.append("service %s: %s must be Production" % (name, key))
    for name, key in (("api", "DECISYA_API_HOST"), ("bff", "DECISYA_APP_HOST")):
        allowed = _environment(services.get(name, {})).get("AllowedHosts")
        if allowed is None or allowed == "*" or allowed != _ref(key, interpolated, values):
            problems.append("service %s: AllowedHosts must be exactly the %s host" % (name, key.split("_")[1].lower()))
    caddy_env = _environment(services.get("caddy", {}))
    if set(caddy_env) != set(CADDY_ENV_KEYS):
        problems.append("caddy: environment keys must be exactly the three DECISYA_* host names (G-C2)")
    for key in CADDY_ENV_KEYS:
        if caddy_env.get(key) != _ref(key, interpolated, values, True):
            problems.append("caddy: %s must be a required reference (an empty value stops the start)" % key)
    return problems


def _under_import_dir(path: str) -> bool:
    """True for the import folder itself (with or without a trailing slash) and anything below it."""
    normal = posixpath.normpath(path) if path else ""
    return normal == IMPORT_DIR or normal.startswith(IMPORT_DIR + "/")


def _keycloak_problems(services: dict, interpolated: bool, values: dict) -> list[str]:
    kc = services.get("keycloak")
    if not kc:
        return []
    problems = []
    if [str(a) for a in _list(kc.get("entrypoint"))] != ["/bin/sh", "/opt/decisya/entrypoint-stack.sh"]:
        problems.append("keycloak: the entrypoint must be the stack wrapper")
    if _list(kc.get("command")):
        problems.append("keycloak: no command arguments (the wrapper always runs `kc.sh start` and adds the realm import itself)")
    argv = [str(a) for a in _list(kc.get("entrypoint")) + _list(kc.get("command"))]
    for bad in KEYCLOAK_BANNED_ARGS:
        if bad in argv:
            problems.append("keycloak: argument %s is not allowed" % bad)
    # G2 D1 (b): the #120 rule "no realm mount" is replaced by "exactly one mount targets the import
    # folder, and it is the production realm file, read-only". A folder target, a named volume, a
    # tmpfs or a second file there is refused (F-77-2, T77-09).
    at_import = []
    for item in _list(kc.get("volumes")):
        if isinstance(item, dict) and _under_import_dir(str(item.get("target", ""))):
            at_import.append(item)
    for item in _list(kc.get("tmpfs")):
        if _under_import_dir(str(item).split(":")[0]):
            problems.append("keycloak: a tmpfs may not target the realm import folder")
    for item in at_import:
        target = str(item.get("target", ""))
        if posixpath.normpath(target) == IMPORT_DIR:
            problems.append("keycloak: the realm import folder itself may not be mounted; mount the one production realm file")
        elif item.get("type") != "bind":
            problems.append("keycloak: the realm file must be a bind mount, not a %s" % item.get("type"))
    exact = [
        i for i in at_import
        if i.get("type") == "bind" and str(i.get("target", "")) == REALM_TARGET
        and str(i.get("source", "")) == REALM_SOURCE and i.get("read_only") is True
    ]
    if len(at_import) != 1 or len(exact) != 1:
        problems.append("keycloak: exactly one mount may target the realm import folder, and it must be the production realm file, read-only")
    # G4-121-03: the two values the wrapper validates are required references, as for Caddy.
    env = _environment(kc)
    for key in KEYCLOAK_ENV_KEYS:
        if env.get(key) != _ref(key, interpolated, values, True):
            problems.append("keycloak: %s must be a required reference (an empty value stops the start)" % key)
    # G-C4 (#178 D3): the console is served on the admin port, so its URLs must name that port. The
    # generated file carries it as a plain reference; the interpolated form is https://<id host>:8444.
    admin_url = "https://%s:%s" % (_ref("DECISYA_ID_HOST", interpolated, values), _ref("DECISYA_ADMIN_PORT", interpolated, values))
    if env.get("KC_HOSTNAME_ADMIN") != admin_url:
        problems.append("keycloak: KC_HOSTNAME_ADMIN must be the id host on the admin port (8444), https://<id host>:<DECISYA_ADMIN_PORT>")
    return problems


def _image_problems(services: dict, interpolated: bool, values: dict, scan_refs: dict | None, release_refs: dict | None) -> list[str]:
    problems = []
    for name, svc in services.items():
        image = str(svc.get("image", ""))
        if name in RELEASE_SERVICES:
            key = RELEASE_SERVICES[name]
            if interpolated:
                expected = (release_refs or {}).get(name) or values.get(key, "")
                if not IMAGE_RE.fullmatch(image) or image != expected:
                    problems.append("service %s: image must be the validated release reference" % name)
            elif image != "${%s}" % key:
                problems.append("service %s: image must be the ${%s} placeholder" % (name, key))
        elif name in THIRD_PARTY_ALIAS:
            if scan_refs is None:
                if not re.search(r"@sha256:[0-9a-f]{64}$", image):
                    problems.append("service %s: image is not pinned by digest" % name)
            elif image != scan_refs.get(THIRD_PARTY_ALIAS[name]):
                problems.append("service %s: image differs from the ContainerImages.cs reference of scan alias %s" % (name, THIRD_PARTY_ALIAS[name]))
    return problems


def generated_problems(cfg: dict) -> list[str]:
    """The generated file alone: it carries no overlay key, and no secret-looking environment (D10 item 1)."""
    problems = []
    for key in sorted(k for k in set(cfg) - {"name", "services", "networks"} if not str(k).startswith("x-")):
        problems.append("generated file: top-level key %s belongs to the overlay" % key)
    if set(cfg.get("networks") or {}) - {"aspire"}:
        problems.append("generated file: only the default aspire network is allowed")
    services = cfg.get("services") or {}
    for name, svc in services.items():
        for key in sorted(set(svc) - GENERATED_ALLOWED_SERVICE_KEYS):
            problems.append("generated file: service %s carries overlay key %s" % (name, key))
        if set(_service_networks(svc)) - {"aspire"}:
            problems.append("generated file: service %s names a network other than aspire" % name)
        for key, value in _environment(svc).items():
            if SECRET_NAME_RE.search(key) and not (key.endswith("_FILE") and value.startswith("/run/secrets/")) and key not in NON_SECRET_NAMES:
                problems.append("generated file: service %s environment name %s looks like a secret" % (name, key))
            if re.search("password=", value, re.IGNORECASE) or USERINFO_RE.search(value):
                problems.append("generated file: service %s environment %s holds a credential" % (name, key))
    return problems


def yaml_text_problems(name: str, text: str) -> list[str]:
    """Line-level scan of a Compose file as written (config output can resolve some keys away)."""
    problems = []
    banned = "|".join(sorted(BANNED_KEYS))
    for number, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if stripped.startswith("#"):
            continue
        if re.match(r"(%s)\s*:" % banned, stripped):
            problems.append("%s line %d: banned key" % (name, number))
        if "docker.sock" in line:
            problems.append("%s line %d: names the Docker socket" % (name, number))
    return problems


# --------------------------------------------------------------------------- the Caddyfile (D10 item 8)

def strip_caddy_comments(text: str) -> str:
    return "\n".join(re.sub(r"(^|\s)#.*$", "", line) for line in text.splitlines())


def caddy_text_problems(text: str) -> list[str]:
    """Rules over the Caddyfile text, comments excluded."""
    code = strip_caddy_comments(text)
    problems = []
    for word in ("trusted_proxies", "log_credentials"):
        if re.search(r"\b%s\b" % word, code):
            problems.append("Caddyfile: %s is not allowed" % word)
    # S-120-12: Caddy links grpc-go only through the OTLP exporter of `tracing`. The image-scan
    # exceptions rest on the Caddyfile having no such directive.
    if re.search(r"(?m)^\s*tracing\b", code) or re.search(r"[{;]\s*tracing\b", code):
        problems.append("Caddyfile: the tracing directive is not allowed (S-120-12)")
    used = set(re.findall(r"\{\$([A-Za-z0-9_]+)\}", code))
    for var in sorted(used - set(CADDY_ENV_KEYS)):
        problems.append("Caddyfile: variable %s has no validator (add one before using it)" % var)
    for var in sorted(set(CADDY_ENV_KEYS) - used):
        problems.append("Caddyfile: variable %s is not used" % var)
    if re.search(r":(9000|8081)\b", code):
        problems.append("Caddyfile: names port 9000 or 8081")
    # #122 G2 D8, G4-122-01 (a, b): the BFF reads the client address from the X-Forwarded-For that stock
    # Caddy writes itself. A directive that sets, copies or deletes any forwarding header would let a
    # client's own value through, or hide the address the limiter partitions on.
    if CADDY_FORWARD_TEXT_RE.search(code):
        problems.append("Caddyfile: header_up and request_header must not touch X-Forwarded-* or Forwarded (the BFF's client address, #122)")
    # A 429 and its Retry-After must pass through the edge untouched.
    if re.search(r"(?i)\b(handle_response|intercept)\b", code):
        problems.append("Caddyfile: handle_response and intercept are not allowed (a 429 must pass through, #122)")
    if re.search(r"(?i)retry-after", code):
        problems.append("Caddyfile: Retry-After must not be set, copied or removed at the edge (#122)")
    return problems


CADDY_FORWARD_TEXT_RE = re.compile(
    r"""(?i)\b(?:header_up|request_header)\s+["']?[+\-?]*\s*["']?(?:x-forwarded-[a-z0-9-]+|forwarded)\b""")
_FORWARDING_NAME_RE = re.compile(r"(?i)^(?:x-forwarded-[a-z0-9-]+|forwarded)$")


def _header_ops(section: Any) -> list:
    """(operation, header name) for a Caddy header-ops object: set, add, replace (dicts), delete (a list)."""
    found = []
    if not isinstance(section, dict):
        return found
    for operation in ("set", "add", "replace", "require"):
        names = section.get(operation)
        if isinstance(names, dict):
            found += [(operation, str(n)) for n in names]
    deleted = section.get("delete")
    if isinstance(deleted, list):
        found += [("delete", str(n)) for n in deleted]
    return found


def caddy_forwarding_problems(adapted: dict) -> list[str]:
    """G4-122-01 / G2 D8 guard 1, over `caddy adapt` JSON: no site block sets, adds, replaces or deletes a
    request header named X-Forwarded-* or Forwarded (`header_up` is a reverse_proxy header op; `request_header`
    is a `headers` handler)."""
    problems = []
    for node in _iter_dicts(adapted):
        handler = node.get("handler")
        if handler == "reverse_proxy":
            sections = [(node.get("headers") or {}).get("request")]
        elif handler == "headers":
            sections = [node.get("request")]
        else:
            continue
        for section in sections:
            for operation, name in _header_ops(section):
                if _FORWARDING_NAME_RE.match(name.lstrip("+-?")):
                    problems.append("a %s handler changes the request header %s (%s): the client address must come from Caddy alone" % (handler, name, operation))
    return problems


def caddy_passthrough_problems(route: dict) -> list[str]:
    """G2 D8 guard 2, over one site block of `caddy adapt` JSON: a 429 from the BFF reaches the client
    with its status, body and Retry-After unchanged: no handle_response, no intercept, and no response
    header op on Retry-After."""
    problems = []
    for node in _iter_dicts(route):
        handler = node.get("handler")
        if handler == "intercept":
            problems.append("the app host has an intercept handler (a 429 and its Retry-After must pass through)")
        if handler == "reverse_proxy" and "handle_response" in node:
            problems.append("the app host has handle_response (a 429 and its Retry-After must pass through)")
        sections = []
        if handler == "reverse_proxy":
            sections.append((node.get("headers") or {}).get("response"))
        elif handler == "headers":
            sections.append(node.get("response"))
        for section in sections:
            for operation, name in _header_ops(section):
                if name.lower() == "retry-after":
                    problems.append("the app host changes Retry-After (%s)" % operation)
    return problems


def _iter_dicts(node: Any):
    if isinstance(node, dict):
        yield node
        for value in node.values():
            yield from _iter_dicts(value)
    elif isinstance(node, list):
        for value in node:
            yield from _iter_dicts(value)


def _match_ranges(raw: Any) -> frozenset:
    raw = raw.get("ranges") if isinstance(raw, dict) else raw
    return frozenset(_norm_net(r) for r in (raw or []))


def _negated_ranges(match: dict) -> frozenset | None:
    """The ranges under `not: [{remote_ip: ...}]`, None when the match has no `not`. A `not` that holds
    anything but remote_ip yields a marker, so it can never equal the committed ranges."""
    if "not" not in match:
        return None
    raw = match["not"]
    found: set = set()
    for item in (raw if isinstance(raw, list) else [raw]):
        if not isinstance(item, dict) or set(item) != {"remote_ip"}:
            return frozenset(["(a `not` over something other than remote_ip)"])
        found |= _match_ranges(item["remote_ip"])
    return frozenset(found)


# Both the proxy and the respond summary have the same eight fields:
#   (kind, ranges, negated, paths, methods, regexp, upstreams-or-status, extra)
# `ranges` is a positive remote_ip, `negated` the ranges under `not`, `extra` any other matcher kind
# (header, expression, vars, ...).
def _route_summary(route: dict) -> tuple:
    sets = route.get("match") or [{}]
    handlers = [h for h in (route.get("handle") or []) if isinstance(h, dict)]
    kinds = [h.get("handler") for h in handlers]
    if len(sets) != 1:
        return ("multi-match",)
    match = sets[0]
    ranges = _match_ranges(match["remote_ip"]) if match.get("remote_ip") is not None else None
    negated = _negated_ranges(match)
    paths = frozenset(match["path"]) if match.get("path") else None
    methods = frozenset(match["method"]) if match.get("method") else None
    regexp = match["path_regexp"].get("pattern") if isinstance(match.get("path_regexp"), dict) else None
    extra = tuple(sorted(set(match) - {"remote_ip", "not", "path", "method", "path_regexp"}))
    if "reverse_proxy" in kinds:
        handler = next(h for h in handlers if h.get("handler") == "reverse_proxy")
        upstreams = tuple(u.get("dial") for u in (handler.get("upstreams") or []))
        return ("proxy", ranges, negated, paths, methods, regexp, upstreams, extra)
    if "static_response" in kinds:
        handler = next(h for h in handlers if h.get("handler") == "static_response")
        return ("respond", ranges, negated, paths, methods, regexp, str(handler.get("status_code")), extra)
    return ("other", tuple(kinds))


# #178 (G2 D1, D4): path sets of the id host's routes. The admin paths are refused on 8443 and admitted
# only on 8444; the decisya realm is admitted only on 8443.
ADMIN_PATHS = ("/admin", "/admin/*", "/realms/master", "/realms/master/*")
REALM_PATHS = ("/realms/decisya", "/realms/decisya/*")
PUBLISHED_ID_PATHS = REALM_PATHS + ("/resources/*",)
ADMIN_LISTENER_PATHS = ADMIN_PATHS + ("/resources/*",)
# S-178-02: a path still holding an encoded dot, slash or backslash after Caddy's single decode.
ENCODED_PATH_PATTERN = "(?i)%(2e|2f|5c)"
DOTDOT_PATTERN = r"\.\."

# The NAS firewall rule ids the runbook names (G2 D5, D7 a).
FW_8443 = "NAS-FW-8443"
FW_ADMIN = "NAS-FW-ADMIN"
# G-E3: every published route, as (listener port, site label, path set or None for all, upstream,
# firewall rule). A published route is one with no source allow-list in Caddy: the NAS firewall
# decides who reaches it. Adding a published route means adding a row here and a rule to the runbook.
PUBLISHED_ROUTES = (
    (HTTPS_PORT, "app", None, "bff:8080", FW_8443),
    (HTTPS_PORT, "id", frozenset(PUBLISHED_ID_PATHS), "keycloak:8080", FW_8443),
    (ADMIN_PORT, "id", frozenset(ADMIN_LISTENER_PATHS), "keycloak:8080", FW_ADMIN),
)
FIREWALL_RULE_BY_PORT = {HTTPS_PORT: FW_8443, ADMIN_PORT: FW_ADMIN}

# G-E4: what must never be admitted on 8443 (the decoded, cleaned and case-folded forms of the admin
# paths, as Caddy's path matcher sees them), and what the admin listener must never admit (G-E5).
ADMIN_PROBES = (
    "/admin", "/admin/", "/admin/master/console/", "/Admin/master/console/", "/ADMIN/", "/Admin",
    "/realms/master", "/realms/master/", "/realms/master/protocol/openid-connect/token",
    "/REALMS/MASTER/account", "/Realms/Master/", "/realms/Master",
)
ADMIN_LISTENER_REFUSED = ("/realms/decisya/protocol/openid-connect/auth", "/bff/login", "/api/capabilities")


def caddy_path_matches(pattern: str, path: str) -> bool:
    """Caddy's `path` matcher for the patterns the id sites may hold: case-insensitive; an exact path;
    a `*` suffix (prefix match), a `*` prefix (suffix match), both (contains); an inner `*` is a glob
    where `*` does not cross a `/`. The path is the decoded, cleaned one. This is a faithful model only
    while the guard that refuses `%`, `//` and an inner `*` on the id sites holds (G4-178-01)."""
    pattern, path = pattern.lower(), path.lower()
    if pattern == "*":
        return True
    if "*" not in pattern:
        return path == pattern
    if "*" in pattern[1:-1]:
        glob = "".join("[^/]*" if char == "*" else re.escape(char) for char in pattern)
        return re.fullmatch(glob, path) is not None
    starts, ends = pattern.startswith("*"), pattern.endswith("*")
    core = pattern.strip("*")
    if starts and ends:
        return core in path
    return path.endswith(core) if starts else path.startswith(core)


def _expected_routes() -> dict:
    """The edge table, by (listener port, site label), in the order Caddy evaluates the routes."""
    backchannel = COMMITTED_SUBNETS["backchannel"]
    idp = COMMITTED_SUBNETS["idp"]
    committed = list(COMMITTED_SUBNETS.values())
    dotdot = ("respond", None, None, None, None, DOTDOT_PATTERN, "403", ())
    encoded = ("respond", None, None, None, None, ENCODED_PATH_PATTERN, "403", ())
    deny = ("respond", None, None, None, None, None, "403", ())

    def proxy(ranges, paths, methods, upstream, negated=None):
        return ("proxy", frozenset(ranges) if ranges else None, frozenset(negated) if negated else None,
                frozenset(paths) if paths else None, frozenset(methods) if methods else None, None, (upstream,), ())

    return {
        (HTTPS_PORT, "app"): [
            dotdot,
            proxy([idp], ["/bff/backchannel-logout"], ["POST"], "bff:8080"),
            ("respond", None, None, frozenset(["/bff/backchannel-logout*"]), None, None, "403", ()),
            proxy(None, None, None, "bff:8080", negated=committed),
            deny,
        ],
        (HTTPS_PORT, "id"): [
            dotdot,
            ("respond", None, None, frozenset(ADMIN_PATHS), None, None, "403", ()),
            encoded,
            proxy(None, PUBLISHED_ID_PATHS, None, "keycloak:8080", negated=committed),
            proxy([backchannel], REALM_PATHS, None, "keycloak:8080"),
            deny,
        ],
        (HTTPS_PORT, "api"): [
            dotdot,
            proxy([backchannel], ["/api/*"], None, "api:8080"),
            deny,
        ],
        (ADMIN_PORT, "id"): [
            dotdot,
            encoded,
            proxy(None, ADMIN_LISTENER_PATHS, None, "keycloak:8080", negated=committed),
            deny,
        ],
    }


def _proxy_route_list(site_route: dict) -> list:
    for node in _iter_dicts(site_route.get("handle") or []):
        routes = node.get("routes")
        if isinstance(routes, list) and any(
                isinstance(r, dict) and any(isinstance(h, dict) and h.get("handler") == "reverse_proxy" for h in (r.get("handle") or []))
                for r in routes):
            return routes
    return []


def _server_port(server: dict) -> int | None:
    """The one port a server listens on, or None when it has not exactly one listen address."""
    listens = [str(listen) for listen in (server.get("listen") or [])]
    if len(listens) != 1:
        return None
    tail = listens[0].rsplit(":", 1)[-1]
    return int(tail) if tail.isdigit() else None


def _id_pattern_problems(where: str, summary: tuple) -> list[str]:
    """G4-178-01: on the id sites a path pattern holds no `%`, no `//` and no inner `*`; those are the
    cases where `caddy_path_matches` would no longer model Caddy's matcher."""
    problems = []
    for pattern in sorted(summary[3] or ()):
        if "%" in pattern:
            problems.append("%s: a path pattern holds %% (it switches Caddy to the escaped path, G4-178-01)" % where)
        if "//" in pattern:
            problems.append("%s: a path pattern holds // (it switches off slash merging, G4-178-01)" % where)
        if "*" in pattern[1:-1]:
            problems.append("%s: a path pattern holds an inner * (G4-178-01)" % where)
    return problems


def _edge_rule_problems(sites: dict, layout: dict) -> list[str]:
    """G-E2 to G-E5 over the routes of every site, by listener port."""
    problems: list[str] = []
    for port, rule in FIREWALL_RULE_BY_PORT.items():
        for entry in PUBLISHED_ROUTES:
            if entry[0] == port and entry[4] != rule:
                problems.append("PUBLISHED_ROUTES: the %s site on %d must name the rule %s (G-E3)" % (entry[1], port, rule))
    committed = frozenset(_norm_net(c) for c in COMMITTED_SUBNETS.values())
    found = set()
    for port, hosts in layout.items():
        for host, label in hosts.items():
            route = sites.get(port, {}).get(host)
            if route is None:
                continue
            summaries = [_route_summary(r) for r in _proxy_route_list(route)]
            for index, summary in enumerate(summaries, 1):
                where = "%s site on %d, route %d" % (label, port, index)
                if len(summary) != 8:
                    problems.append("%s: the route has more than one match set or a handler this guard does not know" % where)
                    continue
                kind, ranges, negated, paths, methods, regexp, target, extra = summary
                if label == "id":
                    problems += _id_pattern_problems(where, summary)
                if kind != "proxy":
                    continue
                # G-E2: a back-channel route (a positive remote_ip inside the committed subnets) or a published one.
                if ranges:
                    if not ranges <= committed:
                        problems.append("%s: source allow-list on a published route (ADR-0016 R9.8 amended: the NAS firewall is the control)" % where)
                elif negated != committed:
                    problems.append("%s: a published route must carry `not remote_ip` over exactly the two committed subnets and no positive remote_ip (G-E2)" % where)
                else:
                    found.add((port, label, paths, target[0] if target else None))
                if label == "id" and (regexp is not None or extra):
                    problems.append("%s: an admitting route on the id host uses path_regexp, expression, header or vars (G4-178-01)" % where)
                # G-E4: no Keycloak route on 8443 may admit an admin path.
                if port == HTTPS_PORT and target == ("keycloak:8080",):
                    if paths is None or regexp is not None or methods is not None or extra:
                        problems.append("%s: a Keycloak route on 8443 must match on path only (G-E4)" % where)
                    else:
                        for probe in ADMIN_PROBES:
                            if any(caddy_path_matches(p, probe) for p in paths):
                                problems.append("%s: a Keycloak route on 8443 admits the admin path %s (G-E4)" % (where, probe))
                # G-E5: the admin listener admits the admin paths to Keycloak and nothing else.
                if port == ADMIN_PORT:
                    if target != ("keycloak:8080",) or paths != frozenset(ADMIN_LISTENER_PATHS):
                        problems.append("%s: the admin listener must admit exactly the admin paths to keycloak:8080 (G-E5)" % where)
                    elif any(caddy_path_matches(p, probe) for p in paths for probe in ADMIN_LISTENER_REFUSED):
                        problems.append("%s: the admin listener admits a path of the decisya realm, the BFF or the Api (G-E5)" % where)
            if port == HTTPS_PORT and label == "id":
                second = summaries[1] if len(summaries) > 1 else None
                if (second is None or len(second) != 8 or second[0] != "respond" or second[6] != "403" or second[3] is None
                        or not all(any(caddy_path_matches(p, probe) for p in second[3]) for probe in ADMIN_PROBES)):
                    problems.append("the id site on 8443: its second route must be an explicit 403 for every admin path (G-E4)")
            if port == ADMIN_PORT and (not summaries or summaries[-1] != ("respond", None, None, None, None, None, "403", ())):
                problems.append("the id site on 8444: the route list must end with the catch-all 403 (G-E5)")
    # G-E3: the published routes found equal the table, in both directions.
    table = {(port, label, paths, upstream) for port, label, paths, upstream, _ in PUBLISHED_ROUTES}
    for port, label, paths, upstream in sorted(found - table, key=str):
        problems.append("a published route (the %s site on %d, upstream %s) is not in PUBLISHED_ROUTES (G-E3)" % (label, port, upstream))
    for port, label, paths, upstream in sorted(table - found, key=str):
        problems.append("PUBLISHED_ROUTES lists the %s site on %d, but no such published route exists (G-E3)" % (label, port))
    return problems


def caddy_problems(adapted: dict, values: dict) -> list[str]:
    """Guards over `caddy adapt` JSON. `values` holds the three host names (fixture or real)."""
    problems: list[str] = []
    for key in CADDY_ENV_KEYS:
        if not values.get(key):
            return ["Caddy guard: %s is not set" % key]
    servers = (((adapted.get("apps") or {}).get("http") or {}).get("servers") or {})
    # G-E1: exactly two servers, one on :8443 and one on :8444, and sites collected per server so that the
    # 8444 id site can never overwrite the 8443 one.
    sites: dict = {}
    if len(servers) != 2:
        problems.append("there must be exactly two HTTP servers, one on :8443 and one on :8444 (G-E1)")
    for server in servers.values():
        port = _server_port(server)
        if port not in (HTTPS_PORT, ADMIN_PORT):
            problems.append("a server listens on an address other than a single :8443 or :8444 (G-E1)")
            continue
        if port in sites:
            problems.append("two servers listen on port %d (G-E1)" % port)
        for route in server.get("routes") or []:
            for match in route.get("match") or []:
                for host in match.get("host") or []:
                    sites.setdefault(port, {})[host] = route
    app, id_host, api = values["DECISYA_APP_HOST"], values["DECISYA_ID_HOST"], values["DECISYA_API_HOST"]
    layout = {HTTPS_PORT: {app: "app", id_host: "id", api: "api"}, ADMIN_PORT: {id_host: "id"}}
    if set(sites.get(HTTPS_PORT, {})) != set(layout[HTTPS_PORT]):
        problems.append("the 8443 listener must hold exactly the app, id and api site blocks (G-E1)")
    if set(sites.get(ADMIN_PORT, {})) != set(layout[ADMIN_PORT]):
        problems.append("the 8444 listener must hold exactly the id site block (G-E1)")
    expected = _expected_routes()
    for port, hosts in layout.items():
        for host, label in hosts.items():
            route = sites.get(port, {}).get(host)
            if route is None:
                continue
            got = [_route_summary(r) for r in _proxy_route_list(route)]
            want = expected[(port, label)]
            if len(got) != len(want):
                problems.append("%s site on %d: %d routes, expected %d (the edge table)" % (label, port, len(got), len(want)))
            else:
                for index, (g, w) in enumerate(zip(got, want), 1):
                    if g != w:
                        problems.append("%s site on %d: route %d differs from the edge table" % (label, port, index))
            if not any(d.get("handler") == "request_body" and d.get("max_size") for d in _iter_dicts(route.get("handle") or [])):
                problems.append("%s site on %d: request_body max_size is missing" % (label, port))
            if label == "app":
                problems += caddy_passthrough_problems(route)
    problems += _edge_rule_problems(sites, layout)
    problems += caddy_forwarding_problems(adapted)
    # tls internal on every site, no other issuer anywhere.
    policies = (((adapted.get("apps") or {}).get("tls") or {}).get("automation") or {}).get("policies") or []
    for policy in policies:
        issuers = policy.get("issuers") or []
        if not issuers or any(i.get("module") != "internal" for i in issuers):
            problems.append("a TLS policy uses an issuer other than internal")
    for host, label in layout[HTTPS_PORT].items():
        if not any(host in (p.get("subjects") or []) and p.get("issuers") for p in policies):
            problems.append("%s site: tls internal policy is missing" % label)
    # admin API off, no root install, nothing that widens trust or logging.
    if (adapted.get("admin") or {}).get("disabled") is not True:
        problems.append("the admin API must be off")
    pki = (((adapted.get("apps") or {}).get("pki") or {}).get("certificate_authorities") or {}).get("local") or {}
    if pki.get("install_trust") is not False:
        problems.append("skip_install_trust is missing")
    for node in _iter_dicts(adapted):
        for key in node:
            lowered = str(key).lower()
            if "trusted_proxies" in lowered or "credentials" in lowered:
                problems.append("the adapted config holds %s" % key)
    for node in _iter_dicts(adapted):
        dial = node.get("dial")
        if isinstance(dial, str) and re.search(r":(9000|8081)$", dial):
            problems.append("an upstream is on port 9000 or 8081")
    return problems


def runbook_firewall_problems(text: str) -> list[str]:
    """G-E3 and G4-178-03, static (no Docker): the runbook names every firewall rule of PUBLISHED_ROUTES on
    one line with its port, carries the 8444 "no connection" refusal row, and carries the STOP rule. Without
    them Done-when 4 of #178 could regress silently."""
    problems = []
    lines = text.splitlines()
    for rule, port in sorted({(entry[4], entry[0]) for entry in PUBLISHED_ROUTES}):
        # The port must stand apart from the rule id: "NAS-FW-8443" alone must not count as "8443".
        if not any(rule in line and re.search(r"\b%d\b" % port, line.replace(rule, " ")) for line in lines):
            problems.append("runbook: the firewall rule %s is not named with port %d on one line" % (rule, port))
    if not any("%d" % ADMIN_PORT in line and re.search(r"(?i)no connection", line) for line in lines):
        problems.append("runbook: the refusal row for port %d (\"no connection\") is missing" % ADMIN_PORT)
    if not re.search(r"\bSTOP\b", text):
        problems.append("runbook: the STOP rule for a failed refusal check is missing")
    return problems


# --------------------------------------------------------------------------- docker inspect after start (G4-120-03 c)

def keycloak_inspect_problems(container: dict) -> list[str]:
    """G2 D1 (f): the running Keycloak container's mounts equal the table, so a hand-edited stack is
    caught too. Compose secret files (/run/secrets/*) are bind mounts and are checked elsewhere."""
    problems = []
    mounts = [m for m in (container.get("Mounts") or []) if isinstance(m, dict)]
    by_destination = {posixpath.normpath(str(m.get("Destination", ""))): m for m in mounts}
    at_import = [d for d in by_destination if _under_import_dir(d)]
    if at_import != [REALM_TARGET]:
        problems.append("keycloak container: exactly one mount may sit in the realm import folder, and it must be the production realm file")
    expected = {target: source for source, target in BIND_MOUNTS["keycloak"].items()}
    bind_destinations = {
        d for d, m in by_destination.items()
        if str(m.get("Type")) == "bind" and not d.startswith("/run/secrets/")
    }
    allowed = set(expected) | {CADDY_ROOT_TARGET}
    if bind_destinations != allowed:
        problems.append("keycloak container: bind destinations differ from the mount table")
    for target, source in expected.items():
        mount = by_destination.get(target)
        if mount is None:
            continue
        if str(mount.get("Type")) != "bind":
            problems.append("keycloak container: %s is not a bind mount" % target)
            continue
        if mount.get("RW") is not False:
            problems.append("keycloak container: %s is not read-only" % target)
        if not str(mount.get("Source", "")).replace("\\", "/").endswith(source[1:]):
            problems.append("keycloak container: the host file behind %s is not the assembled one" % target)
    return problems


def inspect_problems(containers: list, *, bind: str | None = None, port: str | None = None, admin_port: str | None = None) -> list[str]:
    problems = []
    seen = set()
    for container in containers:
        labels = (container.get("Config") or {}).get("Labels") or {}
        service = labels.get("com.docker.compose.service", "?")
        if labels.get("com.docker.compose.project") != STACK_NAME:
            problems.append("a container of another project is on the stack")
            continue
        if service not in EXPECTED_SERVICES:
            problems.append("unknown container for service %s" % service)
            continue
        seen.add(service)
        host = container.get("HostConfig") or {}
        if host.get("Privileged"):
            problems.append("service %s runs privileged" % service)
        if host.get("CapAdd"):
            problems.append("service %s has added capabilities" % service)
        if "ALL" not in {str(c).upper() for c in (host.get("CapDrop") or [])}:
            problems.append("service %s does not drop ALL capabilities" % service)
        if not any(str(o).startswith("no-new-privileges") for o in (host.get("SecurityOpt") or [])):
            problems.append("service %s lacks no-new-privileges" % service)
        for field in ("NetworkMode", "PidMode", "IpcMode", "UsernsMode", "UTSMode"):
            if str(host.get(field) or "") == "host":
                problems.append("service %s uses the host namespace (%s)" % (service, field))
        if host.get("Devices"):
            problems.append("service %s has devices" % service)
        mounts = [str(b) for b in (host.get("Binds") or [])] + [str(m.get("Source", "")) for m in (container.get("Mounts") or [])]
        if any("docker.sock" in m for m in mounts):
            problems.append("service %s mounts the Docker socket" % service)
        bindings = host.get("PortBindings") or {}
        if service == "caddy":
            # G-C3 (#178): exactly 8443/tcp and 8444/tcp, each bound to the bind address and its own port.
            if set(bindings) != {"8443/tcp", "8444/tcp"}:
                problems.append("caddy must publish 8443/tcp and 8444/tcp only")
            elif bind is not None:
                for key, wanted in (("8443/tcp", port), ("8444/tcp", admin_port)):
                    if wanted is not None and [(b.get("HostIp"), b.get("HostPort")) for b in bindings[key]] != [(bind, wanted)]:
                        problems.append("caddy's published %s is not the configured bind address and port" % key)
        elif bindings:
            problems.append("service %s publishes a port" % service)
        networks = set((container.get("NetworkSettings") or {}).get("Networks") or {})
        wanted = {"%s_%s" % (STACK_NAME, n) for n in EXPECTED_NETWORKS[service]}
        if networks != wanted:
            problems.append("service %s is on unexpected networks" % service)
        if service == "keycloak":
            problems += keycloak_inspect_problems(container)
    for service in sorted(set(EXPECTED_SERVICES) - seen):
        problems.append("service %s has no container" % service)
    return problems


# --------------------------------------------------------------------------- the identity check (#121, G2 D5, G3 G4-121-03 d and G4-121-05)

# The keys deploy/keycloak/production/identity-check.sql prints, in its order, with the kind of value
# each carries. The parser accepts nothing else, so the output can never carry a name, an address or
# a secret into a log line. A test ties this table to the SQL file.
IDENTITY_KEYS = {
    "realm_present": "bool",
    "ssl_required_all": "bool",
    "events_enabled": "bool",
    "admin_events_enabled": "bool",
    "admin_events_details_off": "bool",
    "events_expiration_ok": "bool",
    "admin_events_expiration_ok": "bool",
    "jboss_logging_listener": "bool",
    "browser_flow_ok": "bool",
    "level_2_admin_conditional": "bool",
    "level_2_tenant_conditional": "bool",
    "password_policy_ok": "bool",
    "breached_list_in_policy": "bool",
    "users_total": "int",
    "users_without_synthetic": "int",
    "master_users_without_otp": "int",
    "bff_secret_unresolved": "bool",
    "bff_secret_short": "bool",
    "bff_redirect_unresolved": "bool",
    "bff_logout_uris_unresolved": "bool",
    # #178 G4-178-02: the master realm has brute-force detection on, with no permanent lockout.
    "master_brute_force_ok": "bool",
}
_IDENTITY_LINE_RE = re.compile(r"([a-z0-9_]+)=(0|[1-9][0-9]{0,8}|true|false)")
# psql prints these two transaction tags when it is not run with -q; they carry nothing.
_PSQL_TAGS = ("BEGIN", "ROLLBACK")


def parse_identity_output(text: str) -> tuple[dict | None, str | None]:
    """(facts, None) for exactly the 21 `key=value` lines, else (None, reason). The reason never
    repeats a line: an unexpected line could be a name or a secret."""
    facts: dict = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line in _PSQL_TAGS:
            continue
        match = _IDENTITY_LINE_RE.fullmatch(line)
        if not match:
            return None, "the identity check printed a line that is not key=value"
        key, value = match.groups()
        kind = IDENTITY_KEYS.get(key)
        if kind is None:
            return None, "the identity check printed an unknown key"
        if key in facts:
            return None, "the identity check printed a key twice"
        if kind == "bool":
            if value not in ("true", "false"):
                return None, "the identity check printed a number where a flag belongs"
            facts[key] = value == "true"
        else:
            if value in ("true", "false"):
                return None, "the identity check printed a flag where a count belongs"
            facts[key] = int(value)
    if set(facts) != set(IDENTITY_KEYS):
        return None, "the identity check did not print all %d keys" % len(IDENTITY_KEYS)
    return facts, None


def identity_problems(facts: dict, *, bootstrap_retired: bool) -> list[str]:
    """What `verify` reports from the parsed check. Flags and counts only, never a name or a value."""
    problems: list[str] = []
    if not facts["realm_present"]:
        problems.append("identity check: realm decisya is missing (Keycloak has not imported it yet, or its database was recreated)")
    else:
        expectations = (
            ("ssl_required_all", True, "the realm does not require TLS for every request"),
            ("events_enabled", True, "login events are off"),
            ("admin_events_enabled", True, "admin events are off"),
            ("admin_events_details_off", True, "admin event details are on (they would carry credentials)"),
            ("events_expiration_ok", True, "login event retention is not 90 days"),
            ("admin_events_expiration_ok", True, "admin event retention is not 90 days"),
            ("jboss_logging_listener", False, "the jboss-logging event listener is on (names and addresses would reach the container log)"),
            ("browser_flow_ok", True, "the browser flow is not decisya-browser"),
            ("level_2_admin_conditional", True, "the administrator second-factor step is not conditional (admin MFA is off)"),
            ("password_policy_ok", True, "the password policy lacks a required part"),
            ("bff_secret_unresolved", False, "the decisya-bff client secret holds an unresolved placeholder"),
            ("bff_secret_short", False, "the decisya-bff client secret is missing or shorter than 32 characters"),
            ("bff_redirect_unresolved", False, "a decisya-bff redirect URI holds a placeholder or a wildcard"),
            ("bff_logout_uris_unresolved", False, "a decisya-bff logout URI holds a placeholder or a wildcard"),
        )
        for key, wanted, text in expectations:
            if facts[key] is not wanted:
                problems.append("identity check: %s" % text)
        # The C-02 trip-wire (G4-121-05 a): a real account is a visible failure while either switch is off.
        count = facts["users_without_synthetic"]
        tenant_mfa, breached = facts["level_2_tenant_conditional"], facts["breached_list_in_policy"]
        if count > 0 and not (tenant_mfa and breached):
            problems.append(
                "identity check: %d user(s) without synthetic=true while a C-02 switch is off (tenant MFA %s, breached-password list %s); see the go-live gate"
                % (count, "on" if tenant_mfa else "off", "on" if breached else "off"))
    # T121-15: a password-only master-realm administrator bypasses admin MFA entirely. The temporary
    # bootstrap administrator has no OTP by design, so this applies once its secret is retired.
    if bootstrap_retired and facts["master_users_without_otp"] > 0:
        problems.append(
            "identity check: %d master-realm user(s) or service account(s) without an OTP credential after the bootstrap secret was retired"
            % facts["master_users_without_otp"])
    # G4-178-02: once 8444 is reachable, the master realm's password and OTP are the barrier behind the
    # firewall, and Keycloak's default is brute-force detection off. Same trigger as the OTP rule above.
    if bootstrap_retired and not facts["master_brute_force_ok"]:
        problems.append("identity check: master realm brute-force detection is off")
    return problems


# What the identity check may do. It runs as the Postgres superuser, so "a SELECT" is not read-only on
# its own (T121-12): psql meta-commands, side-effecting functions and COPY ... PROGRAM all run.
_SQL_FORBIDDEN_RE = re.compile(
    r"\b(copy|program|set_config|lo_\w*|pg_read\w*|pg_write\w*|pg_terminate\w*|pg_reload\w*|pg_ls\w*|pg_stat_file|"
    r"pg_cancel\w*|pg_sleep\w*|dblink\w*|insert|update|delete|drop|alter|create|grant|revoke|truncate|vacuum|"
    r"call|do|execute|listen|notify|lock|load|set|reset|username|email|first_name|last_name)\b",
    re.IGNORECASE)
_SQL_KEY_RE = re.compile(r"\(\s*\d+\s*,\s*'([a-z0-9_]+)='")


def identity_sql_problems(text: str) -> list[str]:
    """Static rules for identity-check.sql (G4-121-05 c). `stackctl` refuses to send a file that
    fails them, so an edited copy in the stack folder never reaches psql."""
    problems: list[str] = []
    no_comments = "\n".join(re.sub(r"--.*$", "", line) for line in text.splitlines())
    if "/*" in no_comments:
        problems.append("identity-check.sql: block comments are not allowed")
    keys = _SQL_KEY_RE.findall(no_comments)
    if keys != list(IDENTITY_KEYS):
        problems.append("identity-check.sql: the printed keys differ from the parser's list")
    code = re.sub(r"'(?:[^']|'')*'", "''", no_comments)
    if "\\" in code:
        problems.append("identity-check.sql: a backslash (a psql meta-command) is not allowed")
    statements = [re.sub(r"\s+", " ", s).strip() for s in code.split(";") if s.strip()]
    shape_ok = (
        len(statements) == 3
        and statements[0].upper() == "BEGIN TRANSACTION READ ONLY"
        and statements[2].upper() == "ROLLBACK"
        and re.match(r"(?i)SELECT t\.line FROM \( ?VALUES\b", statements[1]) is not None
    )
    if not shape_ok:
        problems.append("identity-check.sql: must be BEGIN TRANSACTION READ ONLY, one SELECT of t.line over VALUES, then ROLLBACK")
    for match in sorted({m.group(1).lower() for m in _SQL_FORBIDDEN_RE.finditer(code)}):
        problems.append("identity-check.sql: the word %s is not allowed" % match)
    return problems


# --------------------------------------------------------------------------- the key ring (#122, G3 G4-122-02 and G4-122-04)

# The strict "plaintext" rule at text level. The BFF's start-up check is the authority (it parses the XML);
# the one-shot `reset` and `verify` scripts apply this same rule with grep, so the patterns are written once
# here, are plain ERE that Python's `re` and `grep -E` read the same way, and are put into the scripts by
# stackctl. A key file passes when:
#   - it holds no masterKey or unencryptedKey anywhere (case-insensitive, stricter than "an element");
#   - it holds exactly one encryptedSecret element, whose decryptorType is the framework's certificate
#     decryptor, and an EncryptedData element;
#   - or it is a revocation file with no secret in it.
# `NullXmlEncryptor` writes an encryptedSecret around a clear masterKey, so "has an encryptedSecret" is not enough.
KEYRING_PLAINTEXT_PATTERN = r"masterKey|unencryptedKey"
KEYRING_SECRET_PATTERN = r"<([A-Za-z0-9_.-]+:)?encryptedSecret"
KEYRING_REVOCATION_PATTERN = r"<revocation[ >/]"
KEYRING_DECRYPTOR_PATTERN = (
    r'decryptorType="Microsoft\.AspNetCore\.DataProtection\.XmlEncryption\.EncryptedXmlDecryptor[,"]')
KEYRING_ENCRYPTED_DATA_PATTERN = r"<([A-Za-z0-9_.-]+:)?EncryptedData"


def keyring_file_problem(text: str) -> str | None:
    """None when the key file text passes the strict rule, else the reason (a closed list, no content)."""
    if re.search(KEYRING_PLAINTEXT_PATTERN, text, re.IGNORECASE):
        return "holds plaintext key material"
    opened = len(re.findall(KEYRING_SECRET_PATTERN, text, re.IGNORECASE))
    if opened == 0:
        return None if re.search(KEYRING_REVOCATION_PATTERN, text) else "holds no encrypted secret"
    if opened != 1:
        return "holds more than one encrypted secret"
    if not re.search(KEYRING_DECRYPTOR_PATTERN, text):
        return "is not wrapped by the certificate decryptor"
    if not re.search(KEYRING_ENCRYPTED_DATA_PATTERN, text):
        return "holds no EncryptedData"
    return None


# What the read-only `verify` one-shot prints: exactly these `key=value` lines, counts only (never a file
# name or a content). Anything else is a failed check, as for the identity check.
KEYRING_REPORT_KEYS = (
    "refused_entries", "xml_files", "xml_ok", "xml_failing", "other_files", "wrong_owner", "wrong_mode",
)
_KEYRING_LINE_RE = re.compile(r"([a-z_]+)=(0|[1-9][0-9]{0,8})")


def parse_keyring_report(text: str) -> tuple[dict | None, str | None]:
    """(facts, None) for exactly the report lines, else (None, reason). The reason never repeats a line."""
    facts: dict = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue
        match = _KEYRING_LINE_RE.fullmatch(line)
        if not match:
            return None, "the key-ring check printed a line that is not key=value"
        key, value = match.groups()
        if key not in KEYRING_REPORT_KEYS:
            return None, "the key-ring check printed an unknown key"
        if key in facts:
            return None, "the key-ring check printed a key twice"
        facts[key] = int(value)
    if set(facts) != set(KEYRING_REPORT_KEYS):
        return None, "the key-ring check did not print all %d keys" % len(KEYRING_REPORT_KEYS)
    return facts, None


def keyring_problems(facts: dict) -> list[str]:
    """What `verify` reports from the key-ring counts (G4-122-02, G4-122-04 a)."""
    problems = []
    if facts["refused_entries"] > 0:
        problems.append("key ring: %d entr(y/ies) are neither a file nor a folder (a link, pipe, socket or device); inspect the volume by hand" % facts["refused_entries"])
    if facts["xml_failing"] > 0:
        problems.append(
            "key ring: %d key file(s) are plaintext or not wrapped by the certificate; run `keyring reset --confirm` (everyone signs in again)"
            % facts["xml_failing"])
    if facts["wrong_owner"] > 0 or facts["wrong_mode"] > 0:
        problems.append("key ring: %d entr(y/ies) have the wrong owner and %d the wrong mode; run `keyring prepare`" % (facts["wrong_owner"], facts["wrong_mode"]))
    return problems


# --------------------------------------------------------------------------- the production realm name stays where G2 D1 (h) puts it

REALM_SCOPE_EXACT = (
    "deploy/compose/stackctl.py",
    "deploy/compose/stackguards.py",
    "deploy/compose/docker-compose.stack.yaml",
)
REALM_SCOPE_PREFIXES = ("deploy/tests/", "tests/", "docs/")
APPHOST_PREFIX = "src/decisya.apphost/"


def realm_scope_problems(relative_path: str, text: str) -> list[str]:
    """The production realm file name may appear in the stack tooling, in tests, in docs and in
    Markdown, and never in the dev AppHost (S-121-02: case-insensitive, whatever the case)."""
    if REALM_FILE_NAME not in text.lower():
        return []
    path = relative_path.replace("\\", "/")
    if path.lower().startswith(APPHOST_PREFIX):
        return ["%s: the production realm file name must never appear under src/Decisya.AppHost/**" % path]
    if path in REALM_SCOPE_EXACT or path.startswith(REALM_SCOPE_PREFIXES) or path.lower().endswith(".md"):
        return []
    return ["%s: names the production realm file outside the places the architecture note allows (D1 h)" % path]


# --------------------------------------------------------------------------- inputs

def load_scan_module(repo: Path):
    """The CI image scan's own module, loaded from the clone (one parser, one predicate, one validator)."""
    spec = importlib.util.spec_from_file_location("_decisya_image_scan", Path(repo) / ".github" / "scripts" / "image_scan.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_scan_refs(repo: Path) -> dict:
    """alias -> reference, read by the CI image scan's own parser (ContainerImages.cs is the one source)."""
    repo = Path(repo)
    module = load_scan_module(repo)
    return module.parse_targets((repo / "src" / "Decisya.AppHost" / "ContainerImages.cs").read_text(encoding="utf-8"))


def parse_images_txt(text: str) -> dict:
    """service -> reference, from a release's images.txt (one reference per line)."""
    refs = {}
    for line in text.splitlines():
        line = line.strip()
        match = IMAGE_RE.fullmatch(line)
        if match:
            refs[match.group(1)] = line
    return refs


def dumps(obj: Any) -> str:
    return json.dumps(obj, indent=2, sort_keys=True)
