#!/usr/bin/env python3
"""Guards shared by CI, `stackctl.py check` and `stackctl.py verify` (issue #120, G3 G4-120-03).

One module runs in three places, so what runs is what was guarded:
  (a) CI (deploy/tests), over `docker compose config --no-interpolate` of the committed files and
      over the interpolated configuration of an assembled folder with documentation-range values;
  (b) `stackctl.py check`, over the interpolated configuration of the real stack folder;
  (c) `stackctl.py verify`, over `docker inspect` of the running containers.

Stdlib only. Every function returns a list of problem strings. A problem names a key, a service or a
rule. It never contains a value from the stack's environment file or from its secrets: the allow-list
values are home-network addresses, and a log line must not repeat them.

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
CADDY_BACKCHANNEL_ADDRESS = "10.120.0.2"
CADDY_IDP_ADDRESS = "10.120.1.2"
COMMITTED_SUBNETS = {"backchannel": "10.120.0.0/24", "idp": "10.120.1.0/24"}

HOST_KEYS = ("DECISYA_APP_HOST", "DECISYA_ID_HOST", "DECISYA_API_HOST")
IMAGE_KEYS = {
    "DECISYA_API_IMAGE": "api",
    "DECISYA_BFF_IMAGE": "bff",
    "DECISYA_MIGRATOR_IMAGE": "migrator",
}
ENV_KEYS = (
    ("DECISYA_BIND_ADDRESS", "DECISYA_HTTPS_PORT", "DECISYA_LAN_SUBNET", "DECISYA_WORKSTATION_ADDRESS")
    + HOST_KEYS
    + tuple(IMAGE_KEYS)
)
CADDY_ENV_KEYS = HOST_KEYS + ("DECISYA_LAN_SUBNET", "DECISYA_WORKSTATION_ADDRESS")

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


def _overlaps(a, b) -> bool:
    return a.version == b.version and a.overlaps(b)


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
    elif key == "DECISYA_BIND_ADDRESS":
        if not re.fullmatch(r"[0-9.]+", value):
            return "must be one IPv4 address"
        try:
            ip = ipaddress.IPv4Address(value)
        except ValueError:
            return "must be one IPv4 address"
        if ip.is_unspecified or ip.is_multicast or ip.is_reserved or ip.is_link_local:
            return "must be a single unicast address, never 0.0.0.0"
    elif key == "DECISYA_WORKSTATION_ADDRESS":
        if not re.fullmatch(r"[0-9A-Fa-f:./]+", value):
            return "must be a single host address (bare, or a /32 or /128)"
        net = _network(value)
        if net is None or net.num_addresses != 1:
            return "must be a single host address (bare, or a /32 or /128)"
        ip = net.network_address
        if ip.is_unspecified or ip.is_multicast or ip.is_reserved:
            return "must be a unicast host address"
    elif key == "DECISYA_LAN_SUBNET":
        if "/" not in value or not re.fullmatch(r"[0-9A-Fa-f:./]+", value):
            return "must be one CIDR network"
        net = _network(value)
        if net is None:
            return "must be one CIDR network with no host bits set"
        address = net.network_address
        if (address.is_loopback or address.is_link_local or address.is_unspecified
                or address.is_multicast or address.is_reserved or not net.is_private):
            return "must be a private LAN network"
        if net.version == 4 and net.prefixlen < 16:
            return "must be /16 or narrower"
        if net.version == 6 and net.prefixlen < 48:
            return "must be /48 or narrower"
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
    committed = [("committed " + name, cidr) for name, cidr in COMMITTED_SUBNETS.items()]
    problems += overlap_problems(valid, committed)
    bind = valid.get("DECISYA_BIND_ADDRESS")
    if bind:
        for name, cidr in COMMITTED_SUBNETS.items():
            if ipaddress.ip_address(bind) in ipaddress.ip_network(cidr):
                problems.append("DECISYA_BIND_ADDRESS lies inside the committed %s network" % name)
    return problems


def overlap_problems(values: dict, subnets: list[tuple[str, str]]) -> list[str]:
    """No allow-list value may overlap a Docker network. Only the network's label is reported:
    its subnet may be the home network."""
    problems = []
    allow = {}
    for key in ("DECISYA_LAN_SUBNET", "DECISYA_WORKSTATION_ADDRESS"):
        net = _network(values[key]) if values.get(key) else None
        if net is not None:
            allow[key] = net
    for label, cidr in subnets:
        net = _network(cidr, strict=False)
        if net is None:
            continue
        for key, mine in allow.items():
            if _overlaps(mine, net):
                problems.append("%s overlaps the Docker network %s" % (key, label))
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
        bind = _ref("DECISYA_BIND_ADDRESS", interpolated, values, True)
        port = _ref("DECISYA_HTTPS_PORT", interpolated, values, True)
        if len(ports) != 1:
            problems.append("caddy must publish exactly one port")
            continue
        entry = ports[0]
        if isinstance(entry, str):
            ok = entry == "%s:%s:8443" % (bind, port)
        else:
            ok = (str(entry.get("host_ip", "")) == bind and str(entry.get("published", "")) == str(port)
                  and str(entry.get("target", "")) == "8443" and str(entry.get("protocol", "tcp")) == "tcp")
        if not ok:
            problems.append("caddy's published port must be exactly BIND_ADDRESS:HTTPS_PORT:8443")
    return problems


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
        problems.append("caddy: environment keys must be exactly the five DECISYA_* values")
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


def _route_summary(route: dict) -> tuple:
    sets = route.get("match") or [{}]
    handlers = [h for h in (route.get("handle") or []) if isinstance(h, dict)]
    kinds = [h.get("handler") for h in handlers]
    if len(sets) != 1:
        return ("multi-match",)
    match = sets[0]
    ranges = None
    if match.get("remote_ip") is not None:
        raw = match["remote_ip"]
        raw = raw.get("ranges") if isinstance(raw, dict) else raw
        ranges = frozenset(_norm_net(r) for r in (raw or []))
    paths = frozenset(match["path"]) if match.get("path") else None
    methods = frozenset(match["method"]) if match.get("method") else None
    regexp = match["path_regexp"].get("pattern") if isinstance(match.get("path_regexp"), dict) else None
    extra = tuple(sorted(set(match) - {"remote_ip", "path", "method", "path_regexp"}))
    if "reverse_proxy" in kinds:
        handler = next(h for h in handlers if h.get("handler") == "reverse_proxy")
        upstreams = tuple(u.get("dial") for u in (handler.get("upstreams") or []))
        return ("proxy", ranges, paths, methods, regexp, upstreams, extra)
    if "static_response" in kinds:
        handler = next(h for h in handlers if h.get("handler") == "static_response")
        return ("respond", ranges, paths, methods, regexp, str(handler.get("status_code")), extra)
    return ("other", tuple(kinds))


def _expected_routes(values: dict) -> dict:
    lan = _norm_net(values["DECISYA_LAN_SUBNET"])
    workstation = _norm_net(values["DECISYA_WORKSTATION_ADDRESS"])
    backchannel = COMMITTED_SUBNETS["backchannel"]
    idp = COMMITTED_SUBNETS["idp"]
    dotdot = ("respond", None, None, None, r"\.\.", "403", ())
    deny = ("respond", None, None, None, None, "403", ())

    def proxy(ranges, paths, methods, upstream):
        return ("proxy", frozenset(ranges), frozenset(paths) if paths else None,
                frozenset(methods) if methods else None, None, (upstream,), ())

    return {
        "app": [
            dotdot,
            proxy([idp], ["/bff/backchannel-logout"], ["POST"], "bff:8080"),
            ("respond", None, frozenset(["/bff/backchannel-logout*"]), None, None, "403", ()),
            proxy([lan], None, None, "bff:8080"),
            deny,
        ],
        "id": [
            dotdot,
            proxy([workstation], ["/admin", "/admin/*", "/realms/master", "/realms/master/*"], None, "keycloak:8080"),
            proxy([lan], ["/realms/decisya", "/realms/decisya/*", "/resources/*"], None, "keycloak:8080"),
            proxy([backchannel], ["/realms/decisya", "/realms/decisya/*"], None, "keycloak:8080"),
            deny,
        ],
        "api": [
            dotdot,
            proxy([backchannel], ["/api/*"], None, "api:8080"),
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


def caddy_problems(adapted: dict, values: dict) -> list[str]:
    """Guards over `caddy adapt` JSON. `values` holds the five DECISYA_* values (fixture or real)."""
    problems: list[str] = []
    for key in CADDY_ENV_KEYS:
        if not values.get(key):
            return ["Caddy guard: %s is not set" % key]
    servers = (((adapted.get("apps") or {}).get("http") or {}).get("servers") or {})
    sites = {}
    for server in servers.values():
        for listen in server.get("listen") or []:
            if not str(listen).endswith(":%d" % HTTPS_PORT):
                problems.append("a server listens on a port other than 8443")
        for route in server.get("routes") or []:
            for match in route.get("match") or []:
                for host in match.get("host") or []:
                    sites[host] = route
    labels = {values["DECISYA_APP_HOST"]: "app", values["DECISYA_ID_HOST"]: "id", values["DECISYA_API_HOST"]: "api"}
    if set(sites) != set(labels):
        problems.append("the Caddyfile must hold exactly the app, id and api site blocks")
    expected = _expected_routes(values)
    for host, label in labels.items():
        route = sites.get(host)
        if route is None:
            continue
        got = [_route_summary(r) for r in _proxy_route_list(route)]
        want = expected[label]
        if len(got) != len(want):
            problems.append("%s site: %d routes, expected %d (the edge table)" % (label, len(got), len(want)))
        else:
            for index, (g, w) in enumerate(zip(got, want), 1):
                if g != w:
                    problems.append("%s site: route %d differs from the edge table" % (label, index))
        if not any(d.get("handler") == "request_body" and d.get("max_size") for d in _iter_dicts(route.get("handle") or [])):
            problems.append("%s site: request_body max_size is missing" % label)
        if label == "app":
            problems += caddy_passthrough_problems(route)
    problems += caddy_forwarding_problems(adapted)
    # tls internal on every site, no other issuer anywhere.
    policies = (((adapted.get("apps") or {}).get("tls") or {}).get("automation") or {}).get("policies") or []
    for policy in policies:
        issuers = policy.get("issuers") or []
        if not issuers or any(i.get("module") != "internal" for i in issuers):
            problems.append("a TLS policy uses an issuer other than internal")
    for host, label in labels.items():
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


def inspect_problems(containers: list, *, bind: str | None = None, port: str | None = None) -> list[str]:
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
            if set(bindings) != {"8443/tcp"}:
                problems.append("caddy must publish 8443/tcp only")
            elif bind is not None and port is not None:
                if [(b.get("HostIp"), b.get("HostPort")) for b in bindings["8443/tcp"]] != [(bind, port)]:
                    problems.append("caddy's published port is not the configured bind address and port")
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
}
_IDENTITY_LINE_RE = re.compile(r"([a-z0-9_]+)=(0|[1-9][0-9]{0,8}|true|false)")
# psql prints these two transaction tags when it is not run with -q; they carry nothing.
_PSQL_TAGS = ("BEGIN", "ROLLBACK")


def parse_identity_output(text: str) -> tuple[dict | None, str | None]:
    """(facts, None) for exactly the 20 `key=value` lines, else (None, reason). The reason never
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

def load_scan_refs(repo: Path) -> dict:
    """alias -> reference, read by the CI image scan's own parser (ContainerImages.cs is the one source)."""
    repo = Path(repo)
    spec = importlib.util.spec_from_file_location("_decisya_image_scan", repo / ".github" / "scripts" / "image_scan.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
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
