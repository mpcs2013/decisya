#!/usr/bin/env python3
"""stackctl: assemble, check and run the deployable stack (issue #120, ADR-0018, G3 G4-120-01 to 03).

Standard library only. Run it from a clone at the release tag, on the host that runs the stack:

  assemble     --stack DIR --published FILE   copy the reviewed files into the stack folder
  secrets init --stack DIR                    generate every secret file (never overwrites); the key ring's
                                              certificate comes from the BFF image's generator (#122)
  secrets add dataprotection_cert             create the four key-ring certificate files of an existing
                                              #120 stack (refuses when any of them exists) (#122)
  secrets rotate NAME --stack DIR             write a fresh credential (DB roles: --apply-db or --files-only);
                                              dataprotection_cert moves the current pair to previous (#122)
  secrets retire keycloak_bootstrap           empty the single-use bootstrap password file
  secrets retire dataprotection_previous      empty the previous certificate pair, once no live key needs it
  keyring prepare --stack DIR                 make the key-ring volume right: owner 1654, folder 0700, files 0600
  keyring reset --confirm --stack DIR         stop the BFF and delete the key files that are not wrapped by
                                              the certificate; everyone signs in again (#122)
  check        --stack DIR                    every rule, over the real files and the real environment
  export-root  --stack DIR                    copy Caddy's public root into <stack>/trust
  up           --stack DIR                    check, start Caddy, export the root, prepare the key ring, start
                                              the rest, verify
  verify       --stack DIR                    guard the running containers (docker inspect), count the key
                                              ring's plaintext leftovers, and run the read-only identity
                                              check on Keycloak's database (#121)
  realm rebuild --confirm --stack DIR         drop and recreate Keycloak's database for a fresh realm
                                              import; refused unless every user is synthetic (#121)

Rules this file keeps:
  - No secret value is ever printed, logged or put on a command line: messages name files and keys.
  - `secrets init` refuses when any target file exists. Every write is a temporary file in the same
    folder, its mode set, then a rename, so nothing is half-written and no `.old` copy is left.
  - The assembled Compose files get non-default names, so a bare `docker compose up` finds neither.
    Every Compose call here passes both with `-f`, a project directory and the stack's environment
    file, and strips COMPOSE_* and DECISYA_* from the shell's environment so only the file counts.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import hashlib
import hmac
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import stackguards as guards

REPO_ROOT = Path(__file__).resolve().parents[2]

GENERATED_NAME = "stack.generated.yaml"
OVERLAY_NAME = "stack.overlay.yaml"
MANIFEST_NAME = "MANIFEST.sha256"
ENV_NAME = ".env"
IMAGES_NAME = "images.txt"
ENV_TEMPLATE = "deploy/compose/stack.env.example"
COMMITTED_GENERATED = "deploy/compose/docker-compose.yaml"

# (path in the clone, path in the stack folder). Explicit on purpose: a new file must be added here
# and, if mounted, to the guards' mount table.
ASSEMBLE_MAP = (
    (COMMITTED_GENERATED, GENERATED_NAME),
    ("deploy/compose/docker-compose.stack.yaml", OVERLAY_NAME),
    ("deploy/caddy/Caddyfile", "config/caddy/Caddyfile"),
    ("deploy/otel-collector/config.yaml", "config/otel-collector/config.yaml"),
    ("deploy/redis/redis.conf", "config/redis/redis.conf"),
    ("deploy/postgres/init/10-keycloak-db.sh", "config/postgres/init/10-keycloak-db.sh"),
    ("deploy/postgres/init/20-decisya-db.sh", "config/postgres/init/20-decisya-db.sh"),
    ("deploy/keycloak/entrypoint-stack.sh", "config/keycloak/entrypoint-stack.sh"),
    # #121: the production realm and its password list are mounted into Keycloak, one read-only file
    # each (stackguards.BIND_MOUNTS). The identity check is not mounted anywhere: `verify` and
    # `realm rebuild` send it to psql over stdin, after a static check, so it is copied here to be
    # covered by the manifest like everything else.
    ("deploy/keycloak/production/realm-decisya.json", "config/keycloak/realm-decisya.json"),
    ("deploy/keycloak/production/common-passwords.txt", "config/keycloak/common-passwords.txt"),
    ("deploy/keycloak/production/identity-check.sql", "config/keycloak/identity-check.sql"),
)
IDENTITY_SQL_DEST = "config/keycloak/identity-check.sql"
BOOTSTRAP_SECRET = "keycloak_bootstrap_admin_password"
# The only database `realm rebuild` touches, written out here and nowhere built from input.
REBUILD_SQL = (
    "DROP DATABASE IF EXISTS keycloak WITH (FORCE);\n"
    "CREATE DATABASE keycloak OWNER keycloak;\n"
    "REVOKE ALL ON DATABASE keycloak FROM PUBLIC;\n"
)
PSQL_BASE = ["exec", "-T", "postgres", "psql", "-q", "-At", "-U", "postgres", "-v", "ON_ERROR_STOP=1"]
STACK_TOP_FILES = {GENERATED_NAME, OVERLAY_NAME, MANIFEST_NAME, ENV_NAME, IMAGES_NAME}
STACK_TOP_DIRS = {"config", "secrets", "trust"}
COMPOSE_DEFAULT_NAME = re.compile(r"(docker-)?compose(\.override)?\.ya?ml", re.IGNORECASE)

KEYCLOAK_WAIT_SECONDS = 420

SECRET_DIR_MODE = 0o700
SECRET_FILE_MODE = 0o444  # Compose ignores secret uid/gid/mode, so this is the container's view
PASSWORD_LENGTH = 48
ALNUM = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"

# The one Redis ACL policy (C-11). deploy/redis/redis.conf documents the same three lines and
# deploy/tests asserts they agree. {bff_hash} is the SHA-256 of the BFF's password (the `#` form),
# so the clear password is only ever in ConnectionStrings__redis.
REDIS_USER = "decisya-bff"
REDIS_ACL_POLICY = (
    "user default off resetpass resetkeys resetchannels -@all",
    "user health on nopass -@all +ping",
    "user decisya-bff on #{bff_hash} ~decisya:bff:* -@all +get +set +del +expire +pexpire +exists "
    "+sadd +srem +smembers +multi +exec +discard +watch +unwatch +ping +echo +client +info +hello",
)

LOGICAL_SECRETS = (
    "postgres_superuser", "migrator_db", "tenancy_db", "entitlements_db", "keycloak_db",
    "keycloak_bootstrap", "redis", "bff_client", "user_id_hash_key",
)
# Credentials whose role the stack's Postgres holds: rotate must change the role and the file together.
ROTATE_SQL_ROLE = {"postgres_superuser": "postgres", "migrator_db": "decisya_migrator", "keycloak_db": "keycloak"}
NEXT_STEPS = {
    "postgres_superuser": "Used by init scripts and break-glass only; no restart is needed.",
    "migrator_db": "Only the migrator uses it: it picks the new value up the next time it runs (`up`, or start the migrator service once).",
    "tenancy_db": "Run the migrator (it re-sets the role's SCRAM verifier), then restart the api.",
    "entitlements_db": "Run the migrator (it re-sets the role's SCRAM verifier), then restart the api.",
    "keycloak_db": "Restart keycloak.",
    "keycloak_bootstrap": "Single use: it only matters at a Keycloak start on an empty database (the first start, or after `realm rebuild`).",
    "redis": "Restart redis and the bff. Sessions end (Redis is not persistent).",
    "bff_client": "Keycloak imported the old secret once and keeps it. In Phase 0 run `realm rebuild --confirm` (it imports the realm again with the new secret), then restart the bff. After go-live this is the migration path of go-live gate item C-03b.",
    "user_id_hash_key": "Restart api, bff and migrator. Correlation across the rotation is lost (accepted).",
    "dataprotection_cert": (
        "Restart the bff (sessions survive: the previous certificate still unwraps the old keys). Copy the two new files off the NAS "
        "to your own store. After 90 days and 10 hours (or the date in the bff's `keyring.previous_certificate` event, plus 10 hours), "
        "run `secrets retire dataprotection_previous` and restart the bff."),
}
DB_ROLES = {"migrator_db": "decisya_migrator", "tenancy_db": "decisya_tenancy", "entitlements_db": "decisya_entitlements"}


class StackError(Exception):
    """A usage or environment error: reported by name, never with a value."""


# --------------------------------------------------------------------------- the key ring tools (#122)

DP_LOGICAL = "dataprotection_cert"
DP_PREVIOUS_LOGICAL = "dataprotection_previous"
DP_PFX_MAX_BYTES = 16 * 1024
BFF_ASSEMBLY = "/app/Decisya.Bff.dll"
KEYRING_OWNER = "%d:%d" % (guards.KEYRING_UID, guards.KEYRING_UID)
KEYRING_ONESHOT_CAPS = ("CHOWN", "DAC_OVERRIDE", "FOWNER")
PINNED_IMAGE_RE = re.compile(r"[a-z0-9][a-z0-9./_-]*(?::[A-Za-z0-9._-]+)?@sha256:[0-9a-f]{64}")

# The shell the one-shot containers run. Module constants, never built from input (G4-122-03 a). The rule
# patterns come from stackguards, which also applies them in Python, so the two cannot drift apart. `find`
# is never told to follow links (its default), `chown -h` does not either, and the volume is refused
# before anything changes when any entry is neither a regular file nor a folder: a link, pipe, socket or
# device. Nothing here prints a name or a content, only counts. (`find -P` is the default and BusyBox
# find does not parse the flag, so it is not passed.)
_KEYRING_RULE_FUNCTION = r"""
keyring_rule_ok() {
  f=$1
  if grep -qiE '@PLAINTEXT@' "$f"; then return 1; fi
  n=$(grep -oiE '@SECRET@' "$f" | wc -l | tr -d ' ')
  if [ "$n" = 0 ]; then
    if grep -qE '@REVOCATION@' "$f"; then return 0; fi
    return 1
  fi
  if [ "$n" != 1 ]; then return 1; fi
  grep -qE '@DECRYPTOR@' "$f" || return 1
  grep -qE '@ENCRYPTED_DATA@' "$f" || return 1
  return 0
}
""".replace("@PLAINTEXT@", guards.KEYRING_PLAINTEXT_PATTERN).replace("@SECRET@", guards.KEYRING_SECRET_PATTERN).replace(
    "@REVOCATION@", guards.KEYRING_REVOCATION_PATTERN).replace("@DECRYPTOR@", guards.KEYRING_DECRYPTOR_PATTERN).replace(
    "@ENCRYPTED_DATA@", guards.KEYRING_ENCRYPTED_DATA_PATTERN)

_KEYRING_REFUSE = r"""
cd @TARGET@
bad=$(find . -xdev ! -type f ! -type d | wc -l | tr -d ' ')
if [ "$bad" != 0 ]; then
  echo "refused: $bad entries are neither files nor folders" >&2
  exit 3
fi
""".replace("@TARGET@", guards.KEYRING_MOUNT_TARGET)

KEYRING_PREPARE_SCRIPT = "set -eu\n" + _KEYRING_REFUSE + r"""
find . -xdev -exec chown -h @OWNER@ {} \;
find . -xdev -type d -exec chmod 0700 {} \;
find . -xdev -type f -exec chmod 0600 {} \;
files=$(find . -xdev -type f | wc -l | tr -d ' ')
dirs=$(find . -xdev -type d | wc -l | tr -d ' ')
echo "prepared files=$files dirs=$dirs"
""".replace("@OWNER@", KEYRING_OWNER)

# Deletes (does not move) every key file that fails the strict rule, at any depth: G4-122-04 (a).
KEYRING_RESET_SCRIPT = "set -eu\n" + _KEYRING_RULE_FUNCTION + _KEYRING_REFUSE + r"""
result=$(find . -xdev -type f -name '*.xml' | while IFS= read -r f; do
  if keyring_rule_ok "$f"; then echo kept; else rm -f -- "$f"; echo deleted; fi
done)
kept=$(printf '%s\n' "$result" | grep -c '^kept$' || true)
deleted=$(printf '%s\n' "$result" | grep -c '^deleted$' || true)
echo "reset kept=$kept deleted=$deleted"
"""

# The suspected-compromise form (`reset --all-keys`, S-122-04): a key wrapped by a leaked certificate passes the
# strict rule, so the plain reset keeps it. This one deletes every key file, wrapped or not. Same refusal first.
KEYRING_RESET_ALL_SCRIPT = "set -eu\n" + _KEYRING_REFUSE + r"""
deleted=$(find . -xdev -type f -name '*.xml' | wc -l | tr -d ' ')
find . -xdev -type f -name '*.xml' -exec rm -f -- {} \;
echo "reset kept=0 deleted=$deleted"
"""

# Read-only: counts under the same rule, plus owner and mode. Prints exactly the lines of
# stackguards.KEYRING_REPORT_KEYS.
KEYRING_VERIFY_SCRIPT = "set -eu\n" + _KEYRING_RULE_FUNCTION + r"""
cd @TARGET@
count() { wc -l | tr -d ' '; }
refused=$(find . -xdev ! -type f ! -type d | count)
result=$(find . -xdev -type f -name '*.xml' | while IFS= read -r f; do
  if keyring_rule_ok "$f"; then echo ok; else echo failing; fi
done)
xml_ok=$(printf '%s\n' "$result" | grep -c '^ok$' || true)
xml_failing=$(printf '%s\n' "$result" | grep -c '^failing$' || true)
other=$(find . -xdev -type f ! -name '*.xml' | count)
owner_user=$(find . -xdev ! -user @UID@ | count)
owner_group=$(find . -xdev ! -group @UID@ | count)
mode_dirs=$(find . -xdev -type d ! -perm 0700 | count)
mode_files=$(find . -xdev -type f ! -perm 0600 | count)
echo "refused_entries=$refused"
echo "xml_files=$((xml_ok + xml_failing))"
echo "xml_ok=$xml_ok"
echo "xml_failing=$xml_failing"
echo "other_files=$other"
echo "wrong_owner=$((owner_user + owner_group))"
echo "wrong_mode=$((mode_dirs + mode_files))"
""".replace("@TARGET@", guards.KEYRING_MOUNT_TARGET).replace("@UID@", str(guards.KEYRING_UID))


def require_pinned_image(image: str) -> str:
    if not PINNED_IMAGE_RE.fullmatch(image or ""):
        raise StackError("the image reference is not pinned by digest")
    return image


def keyring_oneshot_argv(image: str, script: str, *, docker: str = "docker") -> list:
    """The one argv shape of the two privileged tools, `prepare` and `reset` (G3 G4-122-03 a): no network, a
    read-only root, no new privileges, every capability dropped but the approved three, bounded processes
    and memory, the pinned Caddy image, and exactly one mount: the named key-ring volume. Never a bind
    mount, the Docker socket, `-e`, `--env-file`, `--privileged`, `--pid` or `--ipc`."""
    argv = [docker, "run", "--rm", "--network", "none", "--read-only", "--security-opt", "no-new-privileges", "--cap-drop", "ALL"]
    for capability in KEYRING_ONESHOT_CAPS:
        argv += ["--cap-add", capability]
    argv += [
        "--user", "0:0", "--pids-limit", "64", "--memory", "64m",
        "--mount", "type=volume,source=%s,target=%s" % (guards.KEYRING_VOLUME_NAME, guards.KEYRING_MOUNT_TARGET),
        "--entrypoint", "sh", require_pinned_image(image), "-c", script,
    ]
    return argv


def keyring_prepare_argv(image: str, *, docker: str = "docker") -> list:
    return keyring_oneshot_argv(image, KEYRING_PREPARE_SCRIPT, docker=docker)


def keyring_reset_argv(image: str, *, docker: str = "docker", all_keys: bool = False) -> list:
    return keyring_oneshot_argv(image, KEYRING_RESET_ALL_SCRIPT if all_keys else KEYRING_RESET_SCRIPT, docker=docker)


def keyring_verify_argv(image: str, *, docker: str = "docker") -> list:
    """Read-only check: the BFF's own uid, no capability at all (it reads files that uid owns), the volume
    mounted read-only."""
    return [
        docker, "run", "--rm", "--network", "none", "--read-only", "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
        "--user", KEYRING_OWNER, "--pids-limit", "64", "--memory", "64m",
        "--mount", "type=volume,source=%s,target=%s,readonly" % (guards.KEYRING_VOLUME_NAME, guards.KEYRING_MOUNT_TARGET),
        "--entrypoint", "sh", require_pinned_image(image), "-c", KEYRING_VERIFY_SCRIPT,
    ]


def keyring_generator_argv(image: str, *, docker: str = "docker") -> list:
    """G2 D12 / G3 G4-122-03 c, exactly: the BFF image's generator, no network, a read-only root, every
    capability dropped, the BFF's uid. The password goes on stdin (`-i`), never on the command line."""
    if not IMAGE_BFF_RE.fullmatch(image or ""):
        raise StackError("the BFF image reference is not ghcr.io/mpcs2013/decisya-bff@sha256:<64 hex>")
    return [
        docker, "run", "--rm", "-i", "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
        "--user", KEYRING_OWNER, "--entrypoint", "dotnet", image, BFF_ASSEMBLY, guards.GENERATOR_FLAG,
    ]


IMAGE_BFF_RE = re.compile(r"ghcr\.io/mpcs2013/decisya-bff@sha256:[0-9a-f]{64}")


# --------------------------------------------------------------------------- secrets: generation and shapes

def generate_password(length: int = PASSWORD_LENGTH) -> str:
    return "".join(secrets.choice(ALNUM) for _ in range(length))


def generate_hash_key() -> str:
    return base64.b64encode(secrets.token_bytes(32)).decode("ascii")


def generate_credential(logical: str) -> str:
    return generate_hash_key() if logical == "user_id_hash_key" else generate_password()


def connection_string(role: str, password: str) -> str:
    # Never Include Error Detail and never Persist Security Info (G4-120-01).
    return "Host=postgres;Port=5432;Database=decisya;Username=%s;Password=%s" % (role, password)


def redis_connection_string(password: str) -> str:
    return "redis:6379,user=%s,password=%s" % (REDIS_USER, password)


def redis_acl_text(password: str) -> str:
    digest = hashlib.sha256(password.encode("utf-8")).hexdigest()
    return "\n".join(line.format(bff_hash=digest) for line in REDIS_ACL_POLICY)


def files_for(logical: str, value: str) -> dict:
    """Every secret file derived from one credential (one generator owns each credential)."""
    if logical == "postgres_superuser":
        return {"postgres_superuser_password": value}
    if logical == "migrator_db":
        return {"migrator_db_password": value, "ConnectionStrings__decisya": connection_string("decisya_migrator", value)}
    if logical == "tenancy_db":
        return {"Migrator__TenancyRolePassword": value, "ConnectionStrings__tenancy": connection_string("decisya_tenancy", value)}
    if logical == "entitlements_db":
        return {
            "Migrator__EntitlementsRolePassword": value,
            "ConnectionStrings__entitlements": connection_string("decisya_entitlements", value),
        }
    if logical == "keycloak_db":
        return {"keycloak_db_password": value}
    if logical == "keycloak_bootstrap":
        return {"keycloak_bootstrap_admin_password": value}
    if logical == "redis":
        return {"redis_acl": redis_acl_text(value), "ConnectionStrings__redis": redis_connection_string(value)}
    if logical == "bff_client":
        return {"Bff__Oidc__ClientSecret": value}
    if logical == "user_id_hash_key":
        return {"Decisya__Observability__UserIdHashKey": value}
    if logical == DP_LOGICAL:
        # `value` is (base64 PKCS#12, password). The previous pair is written empty: nothing is previous yet.
        certificate, password = value
        return {guards.DP_CERT: certificate, guards.DP_CERT_PASSWORD: password, guards.DP_PREVIOUS: "", guards.DP_PREVIOUS_PASSWORD: ""}
    raise StackError("unknown credential name")


SECRET_FILE_NAMES = tuple(sorted(guards.ALL_SECRETS))

_PASSWORD_FILES = {
    "postgres_superuser_password", "migrator_db_password", "keycloak_db_password",
    "Migrator__TenancyRolePassword", "Migrator__EntitlementsRolePassword", "Bff__Oidc__ClientSecret",
}
_CONNECTION_USERS = {
    "ConnectionStrings__decisya": "decisya_migrator",
    "ConnectionStrings__tenancy": "decisya_tenancy",
    "ConnectionStrings__entitlements": "decisya_entitlements",
}


def _acl_line_patterns() -> list:
    patterns = []
    for line in REDIS_ACL_POLICY:
        head, marker, tail = line.partition("{bff_hash}")
        patterns.append(re.compile(re.escape(head) + (r"[0-9a-f]{64}" + re.escape(tail) if marker else "")))
    return patterns


def pfx_shape_problem(text: str) -> str | None:
    """Why a key-ring certificate file is malformed, without the content: strict base64 on one line (no
    line breaks, no padding games) that decodes to 1 byte up to 16 KiB. The BFF loads it as a PKCS#12."""
    if not re.fullmatch(r"[A-Za-z0-9+/]+={0,2}", text):
        return "must be base64 on one line, with no line break"
    try:
        raw = base64.b64decode(text, validate=True)
    except ValueError:
        return "must be base64 on one line, with no line break"
    if not 1 <= len(raw) <= DP_PFX_MAX_BYTES:
        return "must decode to 1 byte up to 16 KiB"
    return None


def shape_problem(name: str, text: str) -> str | None:
    """Why a secret file's content is malformed, without the content."""
    if name == guards.DP_CERT:
        return pfx_shape_problem(text)
    if name == guards.DP_PREVIOUS:
        return None if text == "" else pfx_shape_problem(text)
    if name == guards.DP_CERT_PASSWORD:
        return None if re.fullmatch(r"[A-Za-z0-9]{32,256}", text) else "must be 32 to 256 letters and digits"
    if name == guards.DP_PREVIOUS_PASSWORD:
        return None if text == "" or re.fullmatch(r"[A-Za-z0-9]{32,256}", text) else "must be empty or 32 to 256 letters and digits"
    if name in _PASSWORD_FILES:
        return None if re.fullmatch(r"[A-Za-z0-9]{32,}", text) else "must be at least 32 letters and digits"
    if name == "keycloak_bootstrap_admin_password":
        # Empty means retired (stackctl secrets retire): the wrapper skips an empty value.
        return None if text == "" or re.fullmatch(r"[A-Za-z0-9]{32,}", text) else "must be empty (retired) or at least 32 letters and digits"
    if name == "Decisya__Observability__UserIdHashKey":
        try:
            raw = base64.b64decode(text, validate=True)
        except ValueError:
            return "must be base64"
        return None if len(raw) >= 32 else "must decode to at least 32 bytes"
    if name in _CONNECTION_USERS:
        pattern = "Host=postgres;Port=5432;Database=decisya;Username=%s;Password=[A-Za-z0-9]{32,}" % _CONNECTION_USERS[name]
        return None if re.fullmatch(pattern, text) else "must be the generated connection string form"
    if name == "ConnectionStrings__redis":
        pattern = "redis:6379,user=%s,password=[A-Za-z0-9]{32,}" % re.escape(REDIS_USER)
        return None if re.fullmatch(pattern, text) else "must be the generated Redis connection string form"
    if name == "redis_acl":
        lines = text.split("\n")
        patterns = _acl_line_patterns()
        if len(lines) != len(patterns) or not all(p.fullmatch(line) for p, line in zip(patterns, lines)):
            return "must be the ACL policy generated by stackctl"
        return None
    return "is not a known secret file"


def _password_of(text: str) -> str | None:
    match = re.search(r"[Pp]assword=([A-Za-z0-9]+)$", text)
    return match.group(1) if match else None


def derived_problems(contents: dict) -> list[str]:
    """A derived file must carry the credential it derives from (catches a half-finished rotation)."""
    problems = []
    pairs = (
        ("ConnectionStrings__decisya", "migrator_db_password"),
        ("ConnectionStrings__tenancy", "Migrator__TenancyRolePassword"),
        ("ConnectionStrings__entitlements", "Migrator__EntitlementsRolePassword"),
    )
    for derived, source in pairs:
        if derived in contents and source in contents and shape_problem(derived, contents[derived]) is None:
            if _password_of(contents[derived]) != contents[source]:
                problems.append("secret %s does not carry the credential in %s (half-rotated?)" % (derived, source))
    # #122: the previous certificate pair is empty together, and a rotation that stopped half way leaves
    # the same certificate in both places.
    previous, previous_password = contents.get(guards.DP_PREVIOUS), contents.get(guards.DP_PREVIOUS_PASSWORD)
    if previous is not None and previous_password is not None and (previous == "") != (previous_password == ""):
        problems.append("secrets %s and %s must be empty together (half-finished rotation or retirement?)" % (guards.DP_PREVIOUS, guards.DP_PREVIOUS_PASSWORD))
    if previous and previous == contents.get(guards.DP_CERT):
        problems.append("secret %s holds the current certificate (half-finished rotation?)" % guards.DP_PREVIOUS)
    redis_cs, acl = contents.get("ConnectionStrings__redis"), contents.get("redis_acl")
    if redis_cs and acl and shape_problem("ConnectionStrings__redis", redis_cs) is None and shape_problem("redis_acl", acl) is None:
        digest = hashlib.sha256((_password_of(redis_cs) or "").encode("utf-8")).hexdigest()
        if "#" + digest not in acl:
            problems.append("secret redis_acl does not carry the hash of the password in ConnectionStrings__redis (half-rotated?)")
    return problems


def secret_problems(secret_dir: Path) -> list[str]:
    problems: list[str] = []
    if not secret_dir.is_dir():
        return ["secrets folder is missing (run `secrets init`)"]
    if os.name == "posix":
        info = secret_dir.stat()
        if info.st_mode & 0o077:
            problems.append("secrets folder must be mode 0700 (it is readable or writable by others)")
        if info.st_uid != os.getuid():
            problems.append("secrets folder is owned by another account")
    present = {p.name for p in secret_dir.iterdir()}
    for extra in sorted(present - set(SECRET_FILE_NAMES)):
        problems.append("unexpected entry %s in the secrets folder" % extra)
    contents: dict = {}
    for name in SECRET_FILE_NAMES:
        path = secret_dir / name
        if not path.is_file():
            problems.append("secret %s: missing" % name)
            continue
        if os.name == "posix":
            info = path.stat()
            if info.st_mode & 0o022:
                problems.append("secret %s: writable by group or others" % name)
            if info.st_uid != os.getuid():
                problems.append("secret %s: owned by another account" % name)
        raw = path.read_bytes()
        if raw.endswith((b"\n", b"\r")):
            problems.append("secret %s: ends with a newline" % name)
            continue
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            problems.append("secret %s: not valid UTF-8" % name)
            continue
        reason = shape_problem(name, text)
        if reason:
            problems.append("secret %s: %s" % (name, reason))
        else:
            contents[name] = text
    problems += derived_problems(contents)
    return problems


def scram_verifier(password: str, salt: bytes | None = None, iterations: int = 4096) -> str:
    """SCRAM-SHA-256 verifier computed client-side, as MigrationRunner's ScramSha256Verifier does."""
    salt = salt if salt is not None else secrets.token_bytes(16)
    salted = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, iterations)
    client_key = hmac.new(salted, b"Client Key", hashlib.sha256).digest()
    stored_key = hashlib.sha256(client_key).digest()
    server_key = hmac.new(salted, b"Server Key", hashlib.sha256).digest()

    def b64(data: bytes) -> str:
        return base64.b64encode(data).decode("ascii")

    return "SCRAM-SHA-256$%d:%s$%s:%s" % (iterations, b64(salt), b64(stored_key), b64(server_key))


def alter_role_sql(role: str, verifier: str) -> str:
    """Sent over stdin, never argv. Statement logging off and failed statements not logged, so the
    verifier cannot reach the server log (as in the init scripts)."""
    if not re.fullmatch(r"[a-z_]+", role) or not re.fullmatch(r"SCRAM-SHA-256\$[0-9]+:[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+", verifier):
        raise StackError("refusing to build SQL from an unexpected role or verifier")
    return (
        "SET log_statement = 'none';\n"
        "SET log_min_error_statement = 'panic';\n"
        "ALTER ROLE \"%s\" PASSWORD '%s';\n" % (role, verifier)
    )


# --------------------------------------------------------------------------- files

def write_atomic(path: Path, data: bytes, mode: int) -> None:
    """Temporary file in the same folder, mode set, then rename: never half-written, no copy left."""
    path = Path(path)
    descriptor, temporary = tempfile.mkstemp(dir=str(path.parent), prefix=".tmp-")
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(temporary, mode)
        if os.name == "nt" and path.exists():
            os.chmod(path, 0o666)  # a read-only target cannot be replaced on Windows
        os.replace(temporary, path)
    except BaseException:
        with contextlib.suppress(OSError):
            os.chmod(temporary, 0o600)
            os.unlink(temporary)
        raise


def sha256_file(path: Path) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def location_problems(stack: Path) -> list[str]:
    for candidate in [stack, *stack.parents]:
        if (candidate / ".git").exists():
            return ["the stack folder is inside a Git working tree; use a folder outside every clone"]
    return []


def _ensure_dir(path: Path, mode: int) -> None:
    path.mkdir(parents=True, exist_ok=True)
    if os.name == "posix":
        os.chmod(path, mode)


def _manifest_text(stack: Path) -> str:
    lines = []
    for _, dest in ASSEMBLE_MAP:
        lines.append("%s  %s" % (sha256_file(stack / dest), dest))
    return "\n".join(sorted(lines, key=lambda line: line.split("  ", 1)[1])) + "\n"


def parse_manifest(text: str) -> dict:
    entries = {}
    for line in text.splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (\S.*)", line)
        if match:
            entries[match.group(2)] = match.group(1)
    return entries


# --------------------------------------------------------------------------- docker

class Runner:
    """Every Docker call. Tests replace it; nothing else in this file starts a process."""

    def __init__(self, stack: Path, docker: str = "docker"):
        self.stack = Path(stack)
        self.docker = docker

    def environment(self) -> dict:
        # Only the stack's environment file may feed interpolation, and no COMPOSE_* variable may steer it.
        return {k: v for k, v in os.environ.items() if not k.startswith(("COMPOSE_", "DECISYA_"))}

    def base(self) -> list:
        return [
            self.docker, "compose", "--project-directory", str(self.stack), "--env-file", str(self.stack / ENV_NAME),
            "-f", str(self.stack / GENERATED_NAME), "-f", str(self.stack / OVERLAY_NAME),
        ]

    def run(self, argv: list, input_text: str | None = None, timeout: int = 600) -> subprocess.CompletedProcess:
        return subprocess.run(
            argv, input=input_text, capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=self.environment(), timeout=timeout,
        )

    def run_bytes(self, argv: list, input_bytes: bytes, timeout: int = 600) -> subprocess.CompletedProcess:
        """Bytes in, bytes out, nothing decoded or echoed: the certificate generator's stdin and stdout."""
        return subprocess.run(argv, input=input_bytes, capture_output=True, env=self.environment(), timeout=timeout)

    def compose(self, args: list, input_text: str | None = None, timeout: int = 600) -> subprocess.CompletedProcess:
        return self.run(self.base() + list(args), input_text=input_text, timeout=timeout)

    def config_json(self) -> dict:
        proc = self.compose(["config", "--no-path-resolution", "--format", "json"])
        if proc.returncode != 0:
            first = (proc.stderr or "").strip().splitlines()[:1]
            raise StackError("docker compose config failed: %s" % (first[0][:200] if first else "no message"))
        return json.loads(proc.stdout)

    def caddy_adapt(self, image: str, caddyfile: Path, values: dict) -> dict:
        caddyfile = Path(caddyfile).resolve()
        if "," in str(caddyfile):
            raise StackError("the stack path must not contain a comma")
        with tempfile.TemporaryDirectory() as folder:
            env_path = Path(folder) / "caddy.env"
            env_path.write_text("".join("%s=%s\n" % (k, values[k]) for k in guards.CADDY_ENV_KEYS), encoding="utf-8")
            if os.name == "posix":
                os.chmod(env_path, 0o600)
            argv = [
                self.docker, "run", "--rm", "--network", "none", "--read-only", "--env-file", str(env_path),
                "--mount", "type=bind,source=%s,target=/etc/caddy/Caddyfile,readonly" % caddyfile,
                image, "caddy", "adapt", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile",
            ]
            proc = self.run(argv, timeout=300)
        if proc.returncode != 0:
            raise StackError("caddy adapt failed (exit %d); run it by hand to read the message" % proc.returncode)
        return json.loads(proc.stdout)

    def docker_subnets(self) -> list:
        listing = self.run([self.docker, "network", "ls", "-q"])
        if listing.returncode != 0:
            raise StackError("docker network ls failed")
        ids = listing.stdout.split()
        if not ids:
            return []
        proc = self.run([self.docker, "network", "inspect", *ids])
        if proc.returncode != 0:
            raise StackError("docker network inspect failed")
        subnets = []
        for network in json.loads(proc.stdout):
            for entry in (network.get("IPAM") or {}).get("Config") or []:
                if entry.get("Subnet"):
                    subnets.append((network.get("Name", "?"), entry["Subnet"]))
        return subnets

    def inspect_project(self) -> list:
        listing = self.compose(["ps", "-aq"])
        ids = listing.stdout.split()
        if listing.returncode != 0 or not ids:
            raise StackError("no containers found for the project")
        proc = self.run([self.docker, "inspect", *ids])
        if proc.returncode != 0:
            raise StackError("docker inspect failed")
        return json.loads(proc.stdout)


# --------------------------------------------------------------------------- the key ring: certificate and volume (#122)

def bff_image(stack: Path) -> str:
    """The BFF image reference from the stack's environment file, validated as `check` validates it."""
    values, problems = _read_env(Path(stack))
    image = values.get("DECISYA_BFF_IMAGE", "")
    if not image:
        raise StackError("DECISYA_BFF_IMAGE is missing in the stack's environment file (the certificate generator runs from that image)")
    reason = guards.value_problem("DECISYA_BFF_IMAGE", image)
    if reason:
        raise StackError("DECISYA_BFF_IMAGE %s" % reason)
    return image


def ensure_image(runner, image: str) -> None:
    """The image by digest, present locally: pulled when it is not."""
    if runner.run([runner.docker, "image", "inspect", image], timeout=120).returncode == 0:
        return
    if runner.run([runner.docker, "pull", image], timeout=1800).returncode != 0:
        raise StackError("could not pull the BFF image by the digest in the environment file")


def generate_certificate_pair(runner, image: str) -> tuple[str, str]:
    """(base64 PKCS#12, password) from the BFF image's generator, G2 D12 and G3 G4-122-03 c.

    The password is made here (CSPRNG) and goes in on stdin; the output is held in memory and checked for
    shape. Neither value is printed, logged or put on a command line, and the generator's stderr is not shown
    (its messages are fixed text, but Docker's own are not ours)."""
    ensure_image(runner, image)
    password = generate_password()
    argv = keyring_generator_argv(image, docker=runner.docker)
    try:
        proc = runner.run_bytes(argv, (password + "\n").encode("ascii"), timeout=900)
    except (subprocess.SubprocessError, OSError):
        raise StackError("the certificate generator did not complete (timeout or Docker error)") from None
    if proc.returncode == 2:
        raise StackError("the certificate generator refused its argument list or its output (exit 2)")
    if proc.returncode != 0:
        raise StackError("the certificate generator failed (exit %d)" % proc.returncode)
    try:
        certificate = (proc.stdout or b"").decode("ascii")
    except UnicodeDecodeError:
        raise StackError("the certificate generator's output is not ASCII") from None
    reason = pfx_shape_problem(certificate)
    if reason:
        raise StackError("the certificate generator's output %s" % reason)
    return certificate, password


def pinned_caddy_image(repo: Path) -> str:
    """The digest-pinned Caddy reference of ContainerImages.cs, from the resolver `check` uses."""
    try:
        image = guards.load_scan_refs(Path(repo)).get("caddy", "")
    except (OSError, ValueError, KeyError, AttributeError, ImportError, SyntaxError):
        raise StackError("could not read the pinned Caddy image from src/Decisya.AppHost/ContainerImages.cs") from None
    if not PINNED_IMAGE_RE.fullmatch(image or ""):
        raise StackError("the pinned Caddy image is not a digest reference")
    return image


def keyring_volume_exists(runner) -> bool:
    return runner.run([runner.docker, "volume", "inspect", guards.KEYRING_VOLUME_NAME], timeout=60).returncode == 0


def _keyring_outcome(label: str, proc) -> str:
    if proc.returncode == 3:
        raise StackError(
            "keyring %s refused: the volume holds an entry that is neither a file nor a folder (a link, pipe, socket or device); "
            "nothing was changed; inspect the volume by hand" % label)
    if proc.returncode != 0:
        raise StackError("keyring %s failed (exit %d); nothing is printed from the volume" % (label, proc.returncode))
    for line in (proc.stdout or "").splitlines():
        if re.fullmatch(r"(prepared|reset)( [a-z]+=[0-9]{1,9})+", line.strip()):
            return line.strip()
    return "%s done" % label


def keyring_prepare(stack: Path, repo: Path, runner) -> str:
    """Owner 1654, folders 0700, files 0600 on the key-ring volume (idempotent). On a first start the
    volume does not exist: Compose creates it (so it carries the project's labels, and `down --volumes`
    finds it), not the one-shot container."""
    image = pinned_caddy_image(repo)
    if not keyring_volume_exists(runner):
        created = runner.compose(["create", "bff"], timeout=900)
        if created.returncode != 0:
            raise StackError(
                "could not create the key-ring volume through Compose (exit %d); on a first start run `up`, "
                "which exports Caddy's root and then prepares the volume" % created.returncode)
    return _keyring_outcome("prepare", runner.run(keyring_prepare_argv(image, docker=runner.docker), timeout=300))


def keyring_reset(stack: Path, repo: Path, runner, all_keys: bool = False) -> str:
    """Stop the BFF, delete the key files that are not certificate-wrapped (all of them with `all_keys`), then
    prepare the volume."""
    image = pinned_caddy_image(repo)
    if not keyring_volume_exists(runner):
        raise StackError("the key-ring volume does not exist: there is nothing to reset")
    stopped = runner.compose(["stop", "bff"], timeout=300)
    if stopped.returncode != 0:
        raise StackError("could not stop the bff (exit %d); nothing was changed" % stopped.returncode)
    outcome = _keyring_outcome("reset", runner.run(keyring_reset_argv(image, docker=runner.docker, all_keys=all_keys), timeout=300))
    return outcome + "; " + keyring_prepare(stack, repo, runner)


def keyring_check(stack: Path, repo: Path, runner, notes: list | None = None) -> list[str]:
    """`verify`'s key-ring step: counts of plaintext leftovers, owner and mode, never a name or a content."""
    try:
        image = pinned_caddy_image(repo)
    except StackError as error:
        return ["key ring check: %s" % error]
    try:
        proc = runner.run(keyring_verify_argv(image, docker=runner.docker), timeout=120)
    except (subprocess.SubprocessError, OSError):
        return ["key ring check: did not complete (timeout or Docker error)"]
    if proc.returncode != 0:
        return ["key ring check: could not run (exit %d); is Docker up and the Caddy image present?" % proc.returncode]
    facts, reason = guards.parse_keyring_report(proc.stdout or "")
    if facts is None:
        return ["key ring check: %s" % reason]
    if notes is not None:
        notes.append("keyring: key_files=%d wrapped=%d failing=%d other_files=%d" % (
            facts["xml_files"], facts["xml_ok"], facts["xml_failing"], facts["other_files"]))
    return guards.keyring_problems(facts)


# --------------------------------------------------------------------------- check

def _read_env(stack: Path) -> tuple[dict, list[str]]:
    path = stack / ENV_NAME
    if not path.is_file():
        return {}, ["the stack's environment file is missing (run `assemble`, then fill it in)"]
    values, problems = guards.parse_env_text(path.read_text(encoding="utf-8", errors="replace"))
    if os.name == "posix" and path.stat().st_mode & 0o022:
        problems.append("the stack's environment file is writable by group or others")
    return values, problems


def layout_problems(stack: Path) -> list[str]:
    problems = []
    for entry in sorted(stack.iterdir(), key=lambda p: p.name):
        if COMPOSE_DEFAULT_NAME.fullmatch(entry.name):
            problems.append("%s carries a Compose default name, so a bare `docker compose up` would load it" % entry.name)
        elif entry.is_file() and entry.name not in STACK_TOP_FILES:
            problems.append("unexpected file %s in the stack folder (no extra Compose or environment files)" % entry.name)
        elif entry.is_dir() and entry.name not in STACK_TOP_DIRS:
            problems.append("unexpected folder %s in the stack folder" % entry.name)
    for name in (GENERATED_NAME, OVERLAY_NAME):
        if not (stack / name).is_file():
            problems.append("%s is missing (run `assemble`)" % name)
    return problems


def manifest_problems(stack: Path, repo: Path) -> list[str]:
    path = stack / MANIFEST_NAME
    if not path.is_file():
        return ["%s is missing (run `assemble`)" % MANIFEST_NAME]
    recorded = parse_manifest(path.read_text(encoding="utf-8"))
    problems = []
    expected = {dest for _, dest in ASSEMBLE_MAP}
    if set(recorded) != expected:
        problems.append("%s does not list exactly the assembled files" % MANIFEST_NAME)
    for source, dest in ASSEMBLE_MAP:
        target = stack / dest
        if not target.is_file():
            problems.append("assembled file %s is missing" % dest)
            continue
        actual = sha256_file(target)
        if recorded.get(dest) != actual:
            problems.append("assembled file %s was changed after assemble (hash differs from the manifest)" % dest)
        clone_file = repo / source
        if not clone_file.is_file():
            problems.append("%s is missing from the clone" % source)
        elif sha256_file(clone_file) != actual:
            problems.append("assembled file %s differs from the clone (check out the release tag and re-run assemble)" % dest)
    config_dir = stack / "config"
    if config_dir.is_dir():
        for file in config_dir.rglob("*"):
            if file.is_file() and file.relative_to(stack).as_posix() not in expected:
                problems.append("unexpected file %s under config" % file.relative_to(stack).as_posix())
    return problems


def root_problems(path: Path) -> list[str]:
    try:
        text = Path(path).read_bytes().decode("ascii")
    except (OSError, UnicodeDecodeError):
        return ["trust/caddy-root.crt is not readable ASCII PEM"]
    problems = []
    if text.count("-----BEGIN CERTIFICATE-----") != 1 or text.count("-----END CERTIFICATE-----") != 1:
        problems.append("trust/caddy-root.crt must hold exactly one certificate")
    if "PRIVATE KEY" in text:
        problems.append("trust/caddy-root.crt holds a private key")
    return problems


def trust_problems(stack: Path) -> list[str]:
    folder = stack / "trust"
    problems = []
    if folder.is_dir():
        for entry in folder.iterdir():
            if entry.name != "caddy-root.crt":
                problems.append("unexpected entry %s in the trust folder (only the public root belongs there)" % entry.name)
        if (folder / "caddy-root.crt").exists():
            problems += root_problems(folder / "caddy-root.crt")
    return problems


def static_problems(stack: Path, repo: Path) -> list[str]:
    """Everything that needs no Docker: location, layout, environment values, images, hashes, secrets."""
    stack, repo = Path(stack), Path(repo)
    if not stack.is_dir():
        return ["the stack folder does not exist"]
    problems = location_problems(stack)
    problems += layout_problems(stack)
    shell_keys = sorted(k for k in os.environ if k.startswith("COMPOSE_"))
    if shell_keys:
        problems.append("COMPOSE_* variables are set in the shell (%s); unset them" % ", ".join(shell_keys))
    values, env_parse = _read_env(stack)
    problems += env_parse
    if values or not env_parse:
        problems += guards.env_problems(values)
    problems += _images_problems(stack, values)
    problems += manifest_problems(stack, repo)
    problems += secret_problems(stack / "secrets")
    problems += trust_problems(stack)
    for name in (GENERATED_NAME, OVERLAY_NAME):
        if (stack / name).is_file():
            problems += guards.yaml_text_problems(name, (stack / name).read_text(encoding="utf-8"))
    return problems


def _images_problems(stack: Path, values: dict) -> list[str]:
    path = stack / IMAGES_NAME
    if not path.is_file():
        return ["%s is missing: save the release's images.txt in the stack folder" % IMAGES_NAME]
    refs = guards.parse_images_txt(path.read_text(encoding="utf-8"))
    problems = []
    for key, service in guards.IMAGE_KEYS.items():
        if service not in refs:
            problems.append("%s has no line for %s" % (IMAGES_NAME, service))
        elif values.get(key) and values[key] != refs[service]:
            problems.append("%s differs from the digest in %s" % (key, IMAGES_NAME))
    return problems


def docker_problems(stack: Path, repo: Path, runner) -> list[str]:
    """The merged configuration with the real environment, the Caddyfile with the real values, and
    the host's Docker networks."""
    stack, repo = Path(stack), Path(repo)
    values, parse_problems = _read_env(stack)
    if parse_problems or guards.env_problems(values):
        return ["Docker checks skipped: fix the environment file first"]
    problems = []
    try:
        scan_refs = guards.load_scan_refs(repo)
        release_refs = guards.parse_images_txt((stack / IMAGES_NAME).read_text(encoding="utf-8")) if (stack / IMAGES_NAME).is_file() else {}
        cfg = runner.config_json()
        problems += guards.config_problems(cfg, interpolated=True, values=values, scan_refs=scan_refs, release_refs=release_refs)
        caddyfile = stack / "config" / "caddy" / "Caddyfile"
        problems += guards.caddy_text_problems(caddyfile.read_text(encoding="utf-8"))
        problems += guards.redis_conf_problems((stack / "config" / "redis" / "redis.conf").read_text(encoding="utf-8"))
        adapted = runner.caddy_adapt(str((cfg.get("services") or {}).get("caddy", {}).get("image", "")), caddyfile, values)
        problems += guards.caddy_problems(adapted, values)
        problems += guards.overlap_problems(values, runner.docker_subnets())
    except (StackError, OSError, ValueError, KeyError) as error:
        problems.append("Docker checks could not complete: %s" % error)
    return problems


def report(problems: list[str]) -> int:
    for problem in problems:
        print("PROBLEM: %s" % problem)
    if problems:
        print("check: %d problem(s)" % len(problems))
        return 1
    print("check: OK")
    return 0


# --------------------------------------------------------------------------- commands

def cmd_assemble(args, runner_factory) -> int:
    repo = Path(args.repo).resolve() if args.repo else REPO_ROOT
    stack = Path(args.stack).resolve()
    committed = repo / COMMITTED_GENERATED
    published = Path(args.published)
    if not committed.is_file() or not published.is_file():
        raise StackError("the committed generated file or the published file is missing")
    if sha256_file(published) != sha256_file(committed):
        raise StackError(
            "the freshly published Compose file differs from the committed %s (drift): "
            "check out the release tag and publish again" % COMMITTED_GENERATED)
    problems = location_problems(stack)
    if problems:
        raise StackError(problems[0])
    for source, _ in ASSEMBLE_MAP:
        if not (repo / source).is_file():
            raise StackError("%s is missing from the clone" % source)
    if not (repo / ENV_TEMPLATE).is_file():
        raise StackError("%s is missing from the clone" % ENV_TEMPLATE)
    _ensure_dir(stack, 0o755)
    config_dir = stack / "config"
    if config_dir.exists():
        shutil.rmtree(config_dir)  # stack-owned; rebuilt below so no stale file survives
    for source, dest in ASSEMBLE_MAP:
        target = stack / dest
        _ensure_dir(target.parent, 0o755)
        write_atomic(target, (repo / source).read_bytes(), 0o755 if dest.endswith(".sh") else 0o644)
    write_atomic(stack / MANIFEST_NAME, _manifest_text(stack).encode("utf-8"), 0o644)
    _ensure_dir(stack / "secrets", SECRET_DIR_MODE)
    _ensure_dir(stack / "trust", 0o755)
    env_path = stack / ENV_NAME
    if not env_path.exists():
        descriptor = os.open(str(env_path), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        with os.fdopen(descriptor, "wb") as handle:
            handle.write((repo / ENV_TEMPLATE).read_bytes())
        print("created the stack's environment file from %s: fill in every value" % ENV_TEMPLATE)
    else:
        print("kept the existing environment file")
    print("assembled %d files into the stack folder (manifest %s)" % (len(ASSEMBLE_MAP), MANIFEST_NAME))
    print("next: save the release's images.txt as %s, fill in the environment file, run `secrets init`, then `check`" % IMAGES_NAME)
    return 0


def cmd_secrets_init(args, runner_factory) -> int:
    stack = Path(args.stack).resolve()
    if not stack.is_dir():
        raise StackError("the stack folder does not exist (run `assemble` first)")
    problems = location_problems(stack)
    if problems:
        raise StackError(problems[0])
    secret_dir = stack / "secrets"
    existing = [n for n in SECRET_FILE_NAMES if (secret_dir / n).exists()]
    if existing:
        raise StackError("refusing to overwrite %d existing secret file(s): %s" % (len(existing), ", ".join(existing)))
    if secret_dir.exists():
        if os.name == "posix" and secret_dir.stat().st_mode & 0o077:
            raise StackError("the secrets folder must be mode 0700")
    # The key ring's certificate first: when the generator cannot run, no file has been written yet.
    certificate = generate_certificate_pair(runner_factory(stack), bff_image(stack))
    if not secret_dir.exists():
        _ensure_dir(secret_dir, SECRET_DIR_MODE)
    written = []
    for logical in LOGICAL_SECRETS:
        for name, content in files_for(logical, generate_credential(logical)).items():
            write_atomic(secret_dir / name, content.encode("utf-8"), SECRET_FILE_MODE)
            written.append(name)
    for name, content in files_for(DP_LOGICAL, certificate).items():
        write_atomic(secret_dir / name, content.encode("utf-8"), SECRET_FILE_MODE)
        written.append(name)
    print("wrote %d secret files: %s" % (len(written), ", ".join(sorted(written))))
    print("no value was printed; the files are the only copy")
    print("next: copy %s and %s off the NAS to your own store (the key ring cannot be read without them)" % (guards.DP_CERT, guards.DP_CERT_PASSWORD))
    return 0


def _write_pair(secret_dir: Path, pairs: dict) -> None:
    for name, content in pairs.items():
        write_atomic(secret_dir / name, content.encode("utf-8"), SECRET_FILE_MODE)


def cmd_secrets_add(args, runner_factory) -> int:
    """Create the four key-ring certificate files on an existing #120 stack. It refuses when any of the four
    exists (a half-written set is the operator's to remove) and never touches another credential."""
    stack = Path(args.stack).resolve()
    secret_dir = stack / "secrets"
    if not secret_dir.is_dir():
        raise StackError("the secrets folder does not exist (run `secrets init` on a new stack)")
    if os.name == "posix" and secret_dir.stat().st_mode & 0o077:
        raise StackError("the secrets folder must be mode 0700")
    existing = [n for n in guards.DP_SECRETS if (secret_dir / n).exists()]
    if existing:
        raise StackError("refusing to overwrite %d existing secret file(s): %s" % (len(existing), ", ".join(existing)))
    certificate = generate_certificate_pair(runner_factory(stack), bff_image(stack))
    _write_pair(secret_dir, files_for(DP_LOGICAL, certificate))
    print("added %s: wrote %s" % (DP_LOGICAL, ", ".join(sorted(guards.DP_SECRETS))))
    print("no value was printed; the files are the only copy")
    print("next: copy %s and %s off the NAS to your own store; then `assemble`, `keyring reset --confirm` (the ring #120 wrote is unencrypted) and `up`" % (guards.DP_CERT, guards.DP_CERT_PASSWORD))
    return 0


def _read_secret(secret_dir: Path, name: str) -> str:
    path = secret_dir / name
    if not path.is_file():
        raise StackError("secret %s does not exist" % name)
    return path.read_text(encoding="utf-8")


def rotate_certificate(args, runner_factory) -> int:
    """`rotate dataprotection_cert`: the current pair becomes the previous pair, then a new current pair is
    written. The BFF unwraps old keys with the previous certificate, so sessions survive."""
    stack = Path(args.stack).resolve()
    secret_dir = stack / "secrets"
    current = _read_secret(secret_dir, guards.DP_CERT)
    current_password = _read_secret(secret_dir, guards.DP_CERT_PASSWORD)
    previous = _read_secret(secret_dir, guards.DP_PREVIOUS)
    previous_password = _read_secret(secret_dir, guards.DP_PREVIOUS_PASSWORD)
    for name, text in ((guards.DP_CERT, current), (guards.DP_CERT_PASSWORD, current_password),
                       (guards.DP_PREVIOUS, previous), (guards.DP_PREVIOUS_PASSWORD, previous_password)):
        reason = shape_problem(name, text)
        if reason:
            raise StackError("secret %s: %s; repair it before a rotation" % (name, reason))
    if (previous == "") != (previous_password == ""):
        raise StackError("the previous certificate pair is half empty; repair it before a rotation")
    if previous and not args.drop_previous:
        raise StackError(
            "the previous certificate pair is not empty: a rotation would drop it. Run `secrets retire dataprotection_previous` "
            "once no live key needs it, or pass --drop-previous (the bff then refuses to start if a live key was wrapped by "
            "the dropped certificate)")
    fresh = generate_certificate_pair(runner_factory(stack), bff_image(stack))
    # Previous first, then current: an interruption leaves the same certificate in both places, which `check` names.
    _write_pair(secret_dir, {guards.DP_PREVIOUS: current, guards.DP_PREVIOUS_PASSWORD: current_password})
    _write_pair(secret_dir, {guards.DP_CERT: fresh[0], guards.DP_CERT_PASSWORD: fresh[1]})
    print("rotated %s: wrote %s" % (DP_LOGICAL, ", ".join(sorted(guards.DP_SECRETS))))
    print("next: %s" % NEXT_STEPS[DP_LOGICAL])
    return 0


def cmd_secrets_rotate(args, runner_factory) -> int:
    stack = Path(args.stack).resolve()
    secret_dir = stack / "secrets"
    logical = args.name
    if not secret_dir.is_dir():
        raise StackError("the secrets folder does not exist")
    if args.drop_previous and logical != DP_LOGICAL:
        raise StackError("--drop-previous only applies to dataprotection_cert")
    if logical == DP_LOGICAL:
        if args.apply_db or args.files_only:
            raise StackError("--apply-db and --files-only only apply to database credentials")
        return rotate_certificate(args, runner_factory)
    if logical in ROTATE_SQL_ROLE and not (args.apply_db or args.files_only):
        raise StackError("this credential belongs to a database role: pass --apply-db (alter the role, then the files) or --files-only")
    if args.apply_db and logical not in ROTATE_SQL_ROLE:
        raise StackError("--apply-db only applies to postgres_superuser, migrator_db and keycloak_db")
    value = generate_credential(logical)
    files = files_for(logical, value)
    for name in files:
        if not (secret_dir / name).is_file():
            raise StackError("secret %s does not exist: rotate only replaces, `secrets init` creates" % name)
    if args.apply_db:
        sql = alter_role_sql(ROTATE_SQL_ROLE[logical], scram_verifier(value))
        runner = runner_factory(stack)
        proc = runner.compose(["exec", "-T", "postgres", "psql", "-U", "postgres", "-d", "postgres", "-v", "ON_ERROR_STOP=1"], input_text=sql)
        if proc.returncode != 0:
            # psql's message can quote the statement, so it is not shown. The files are unchanged.
            raise StackError("ALTER ROLE failed (exit %d); no file was changed" % proc.returncode)
    for name, content in files.items():
        write_atomic(secret_dir / name, content.encode("utf-8"), SECRET_FILE_MODE)
    print("rotated %s: wrote %s" % (logical, ", ".join(sorted(files))))
    print("next: %s" % NEXT_STEPS[logical])
    return 0


def retire_previous_certificate(args) -> int:
    """Empty the previous certificate pair (both files, together). Run it once no live key needs the old
    certificate: 90 days and 10 hours after a rotation, or after the date in the bff's event 1831 plus 10
    hours. Too early, the bff refuses to start and writes nothing (fail closed)."""
    secret_dir = Path(args.stack).resolve() / "secrets"
    previous = _read_secret(secret_dir, guards.DP_PREVIOUS)
    previous_password = _read_secret(secret_dir, guards.DP_PREVIOUS_PASSWORD)
    if previous == "" and previous_password == "":
        print("nothing to retire: the previous certificate pair is already empty")
        return 0
    _write_pair(secret_dir, {guards.DP_PREVIOUS: "", guards.DP_PREVIOUS_PASSWORD: ""})
    print("retired %s: the previous certificate pair is empty" % DP_PREVIOUS_LOGICAL)
    print("next: restart the bff; if it refuses to start, a live key still needs the old certificate (restore it from your off-NAS copy and wait)")
    return 0


def cmd_secrets_retire(args, runner_factory) -> int:
    if args.name == DP_PREVIOUS_LOGICAL:
        return retire_previous_certificate(args)
    secret_dir = Path(args.stack).resolve() / "secrets"
    target = secret_dir / "keycloak_bootstrap_admin_password"
    if not target.is_file():
        raise StackError("secret keycloak_bootstrap_admin_password does not exist")
    write_atomic(target, b"", SECRET_FILE_MODE)
    print("retired keycloak_bootstrap_admin_password: the file is empty, the wrapper skips it")
    print("next: restart keycloak after you created your permanent admin and removed the temporary one")
    return 0


def cmd_check(args, runner_factory) -> int:
    stack = Path(args.stack).resolve()
    repo = Path(args.repo).resolve() if args.repo else REPO_ROOT
    problems = static_problems(stack, repo)
    if not args.static_only and stack.is_dir():
        problems += docker_problems(stack, repo, runner_factory(stack))
    return report(problems)


def export_root(stack: Path, runner, attempts: int = 30, pause: float = 2.0) -> None:
    trust = stack / "trust"
    _ensure_dir(trust, 0o755)
    temporary = trust / ".root-export.tmp"
    last = None
    for _ in range(attempts):
        proc = runner.compose(["cp", "caddy:/data/caddy/pki/authorities/local/root.crt", str(temporary)])
        if proc.returncode == 0 and temporary.is_file():
            problems = root_problems(temporary)
            if problems:
                with contextlib.suppress(OSError):
                    temporary.unlink()
                raise StackError(problems[0])
            write_atomic(trust / "caddy-root.crt", temporary.read_bytes(), 0o644)
            temporary.unlink()
            return
        last = proc.returncode
        time.sleep(pause)
    raise StackError("Caddy's root certificate did not appear (last exit %s); is caddy running?" % last)


def cmd_export_root(args, runner_factory) -> int:
    stack = Path(args.stack).resolve()
    export_root(stack, runner_factory(stack))
    print("exported Caddy's public root to trust/caddy-root.crt (certificate only, never the key)")
    print("next: restart keycloak, api and bff if they were already running")
    return 0


def wait_healthy(runner, service: str, seconds: int) -> bool:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        listing = runner.compose(["ps", "-q", service])
        container = listing.stdout.strip()
        if container:
            proc = runner.run([runner.docker, "inspect", "--format", "{{.State.Health.Status}}", container])
            if proc.stdout.strip() == "healthy":
                return True
        time.sleep(2)
    return False


def bootstrap_retired(stack: Path) -> bool:
    """True when the single-use bootstrap password file is empty (`secrets retire`) or gone. Only the
    size is read, never the content."""
    try:
        return (Path(stack) / "secrets" / BOOTSTRAP_SECRET).stat().st_size == 0
    except FileNotFoundError:
        return True
    except OSError:
        return False


def run_identity_check(stack: Path, runner) -> tuple[dict | None, str | None]:
    """Run identity-check.sql read-only against Keycloak's database. Returns (facts, None), or
    (None, reason) when the file fails its static rules, psql fails or times out, or the output is
    not exactly the 20 `key=value` lines (fail closed, G4-121-05 b). The reason never repeats output."""
    path = Path(stack) / IDENTITY_SQL_DEST
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None, "identity-check.sql is missing from the stack folder (run `assemble`)"
    broken = guards.identity_sql_problems(text)
    if broken:
        return None, broken[0]
    try:
        proc = runner.compose(PSQL_BASE + ["-d", "keycloak"], input_text=text, timeout=120)
    except (subprocess.SubprocessError, OSError):
        return None, "the identity check did not complete (timeout or Docker error)"
    if proc.returncode != 0:
        return None, "the identity check could not run (exit %d); is postgres up and the keycloak database present?" % proc.returncode
    return guards.parse_identity_output(proc.stdout or "")


def identity_check_problems(stack: Path, runner, notes: list | None = None) -> list[str]:
    """Problems from the identity check. `notes`, when given, receives one line of counts and
    on/off flags (the C-02 switch states), which the go-live gate reads. Never a name or a value."""
    facts, reason = run_identity_check(stack, runner)
    if facts is None:
        return ["identity check: %s" % reason]
    if notes is not None:
        notes.append("identity: users_total=%d users_without_synthetic=%d tenant_mfa=%s breached_list=%s" % (
            facts["users_total"], facts["users_without_synthetic"],
            "on" if facts["level_2_tenant_conditional"] else "off", "on" if facts["breached_list_in_policy"] else "off"))
    return guards.identity_problems(facts, bootstrap_retired=bootstrap_retired(stack))


def verify_problems(stack: Path, runner, notes: list | None = None, repo: Path | None = None) -> list[str]:
    values, _ = _read_env(stack)
    problems = guards.inspect_problems(runner.inspect_project(), bind=values.get("DECISYA_BIND_ADDRESS"), port=values.get("DECISYA_HTTPS_PORT"))
    # On the first `up` the edge network does not exist when `check` runs, so look again now that it does.
    problems += guards.overlap_problems(values, runner.docker_subnets())
    # #122: no plaintext key and the right owner and modes on the key ring (G4-122-02, G4-122-04 a).
    problems += keyring_check(stack, Path(repo) if repo else REPO_ROOT, runner, notes)
    # #121: the realm, the events, the C-02 trip-wire and the master-realm OTP check (G4-121-05 a).
    problems += identity_check_problems(stack, runner, notes)
    return problems


def cmd_keyring_prepare(args, runner_factory) -> int:
    stack = Path(args.stack).resolve()
    repo = Path(args.repo).resolve() if args.repo else REPO_ROOT
    print("key ring: %s" % keyring_prepare(stack, repo, runner_factory(stack)))
    return 0


def cmd_keyring_reset(args, runner_factory) -> int:
    """Delete the key files that are not certificate-wrapped and prepare the volume. Everyone signs in again:
    the old cookies cannot be read any more. It stops the bff and leaves it stopped: `up` starts it."""
    stack = Path(args.stack).resolve()
    if not args.confirm:
        raise StackError("keyring reset deletes the key files that are not wrapped by the certificate and ends every session: pass --confirm")
    repo = Path(args.repo).resolve() if args.repo else REPO_ROOT
    print("key ring: %s" % keyring_reset(stack, repo, runner_factory(stack), all_keys=args.all_keys))
    print("next: `up` starts the bff; it creates a new certificate-wrapped key, and everyone signs in once more")
    return 0


def cmd_verify(args, runner_factory) -> int:
    stack = Path(args.stack).resolve()
    notes: list = []
    problems = verify_problems(stack, runner_factory(stack), notes)
    for note in notes:
        print("note: %s" % note)
    for problem in problems:
        print("PROBLEM: %s" % problem)
    print("verify: %s" % ("%d problem(s)" % len(problems) if problems else "OK"))
    return 1 if problems else 0


def cmd_up(args, runner_factory) -> int:
    stack = Path(args.stack).resolve()
    repo = Path(args.repo).resolve() if args.repo else REPO_ROOT
    runner = runner_factory(stack)
    problems = static_problems(stack, repo) + docker_problems(stack, repo, runner)
    if problems:
        return report(problems)
    for step, argv in (("start caddy", ["up", "-d", "caddy"]),):
        proc = runner.compose(argv)
        if proc.returncode != 0:
            raise StackError("%s failed (exit %d)" % (step, proc.returncode))
    if not wait_healthy(runner, "caddy", 120):
        raise StackError("caddy did not become healthy within 120 seconds")
    export_root(stack, runner)
    # #122: the key-ring volume gets its owner and modes before the bff starts (it settles #120's writability item).
    print("key ring: %s" % keyring_prepare(stack, repo, runner))
    proc = runner.compose(["up", "-d"])
    if proc.returncode != 0:
        raise StackError("start failed (exit %d)" % proc.returncode)
    # #121: `verify` now reads Keycloak's database, and the realm exists only after Keycloak's first
    # start has imported it (about a minute and a half), so wait for it before the final check.
    print("waiting for keycloak to become healthy (the first start imports the realm)")
    if not wait_healthy(runner, "keycloak", KEYCLOAK_WAIT_SECONDS):
        print("keycloak did not become healthy within %d seconds; `verify` below will say what is missing" % KEYCLOAK_WAIT_SECONDS)
    return cmd_verify(args, runner_factory)


def cmd_realm_rebuild(args, runner_factory) -> int:
    """Drop and recreate Keycloak's database so the next start imports the realm file again.

    Allowed only while every user is synthetic (G2 D2; ADR-0016: Phase 0 holds no real account).
    Refuses before it changes anything when the identity check cannot run or its output is not
    exactly the expected lines (G4-121-05 b), when any user lacks synthetic=true, or when the
    bootstrap secret is retired (the rebuilt Keycloak would have no administrator). The database
    name is a constant. Nothing printed here is a value.
    """
    stack = Path(args.stack).resolve()
    if not args.confirm:
        raise StackError("realm rebuild drops the identity database: pass --confirm (allowed only while every user is synthetic)")
    if not stack.is_dir():
        raise StackError("the stack folder does not exist")
    runner = runner_factory(stack)
    facts, reason = run_identity_check(stack, runner)
    if facts is None:
        raise StackError("refusing to rebuild: %s" % reason)
    unsynthetic = facts["users_without_synthetic"]
    if unsynthetic > 0:
        raise StackError(
            "refusing to rebuild: %d user(s) without synthetic=true exist and a rebuild would destroy them "
            "(go-live gate item C-03b replaces this command)" % unsynthetic)
    if bootstrap_retired(stack):
        raise StackError(
            "refusing to rebuild: the bootstrap administrator secret is retired (empty), so the rebuilt Keycloak would have "
            "no administrator; run `secrets rotate keycloak_bootstrap` first")
    steps = (
        ("stop keycloak", ["stop", "keycloak"], None),
        ("drop and recreate the keycloak database", PSQL_BASE + ["-d", "postgres"], REBUILD_SQL),
        ("start keycloak", ["up", "-d", "keycloak"], None),
    )
    for label, argv, sql in steps:
        try:
            proc = runner.compose(argv, input_text=sql, timeout=300)
        except (subprocess.SubprocessError, OSError):
            raise StackError("%s did not complete (timeout or Docker error); check `ps` before you retry" % label) from None
        if proc.returncode != 0:
            raise StackError("%s failed (exit %d); check `ps` before you retry" % (label, proc.returncode))
    print("rebuilt: keycloak's database was dropped and recreated, and keycloak was started on an empty database")
    print("next: wait until keycloak is healthy (it imports the realm), then create your permanent admin with TOTP and "
          "re-provision the synthetic users (docs/runbooks/deployable-stack.md, Realm changes), `secrets retire keycloak_bootstrap`, then run `verify`")
    return 0


# --------------------------------------------------------------------------- entry point

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="stackctl.py", description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    def stack_arg(p):
        p.add_argument("--stack", required=True, help="the stack folder (outside every Git working tree)")
        return p

    assemble = stack_arg(sub.add_parser("assemble", help="copy the reviewed files into the stack folder"))
    assemble.add_argument("--published", required=True, help="the file `--operation publish` just wrote; it must equal the committed one")
    assemble.add_argument("--repo", help="the clone (default: the clone holding this script)")
    assemble.set_defaults(func=cmd_assemble)

    secrets_parser = sub.add_parser("secrets", help="init, add, rotate or retire secret files")
    secrets_sub = secrets_parser.add_subparsers(dest="action", required=True)
    init = stack_arg(secrets_sub.add_parser("init", help="generate every secret file; never overwrites"))
    init.set_defaults(func=cmd_secrets_init)
    add = stack_arg(secrets_sub.add_parser("add", help="create the key-ring certificate files of an existing stack; never overwrites"))
    add.add_argument("name", choices=(DP_LOGICAL,))
    add.set_defaults(func=cmd_secrets_add)
    rotate = stack_arg(secrets_sub.add_parser("rotate", help="write a fresh credential"))
    rotate.add_argument("name", choices=LOGICAL_SECRETS + (DP_LOGICAL,))
    rotate.add_argument("--apply-db", action="store_true", help="alter the Postgres role first, over stdin, then write the files")
    rotate.add_argument("--files-only", action="store_true", help="write the files only (you alter the role yourself)")
    rotate.add_argument("--drop-previous", action="store_true", help="dataprotection_cert only: replace a non-empty previous pair (the bff then refuses to start if a live key needs it)")
    rotate.set_defaults(func=cmd_secrets_rotate)
    retire = stack_arg(secrets_sub.add_parser("retire", help="empty the single-use bootstrap password file, or the previous certificate pair"))
    retire.add_argument("name", choices=("keycloak_bootstrap", DP_PREVIOUS_LOGICAL))
    retire.set_defaults(func=cmd_secrets_retire)

    keyring = sub.add_parser("keyring", help="key-ring volume operations")
    keyring_sub = keyring.add_subparsers(dest="action", required=True)
    prepare = stack_arg(keyring_sub.add_parser("prepare", help="owner 1654, folder 0700, files 0600 on the key-ring volume"))
    prepare.add_argument("--repo", help="the clone that holds the pinned Caddy image reference (default: the clone holding this script)")
    prepare.set_defaults(func=cmd_keyring_prepare)
    reset = stack_arg(keyring_sub.add_parser("reset", help="delete the key files that are not certificate-wrapped; every session ends"))
    reset.add_argument("--confirm", action="store_true", help="required: the old key files are deleted and everyone signs in again")
    reset.add_argument("--all-keys", action="store_true", help="suspected compromise: delete every key file, certificate-wrapped ones too (a key wrapped by a leaked certificate passes the plain reset)")
    reset.add_argument("--repo", help="the clone that holds the pinned Caddy image reference (default: the clone holding this script)")
    reset.set_defaults(func=cmd_keyring_reset)

    check = stack_arg(sub.add_parser("check", help="every rule over the real files and environment"))
    check.add_argument("--repo", help="the clone to compare the assembled files with")
    check.add_argument("--static-only", action="store_true", help="skip the Docker checks (not enough to deploy)")
    check.set_defaults(func=cmd_check)

    export = stack_arg(sub.add_parser("export-root", help="copy Caddy's public root into the trust folder"))
    export.set_defaults(func=cmd_export_root)

    up = stack_arg(sub.add_parser("up", help="check, start caddy, export the root, start the rest, verify"))
    up.add_argument("--repo", help="the clone to compare the assembled files with")
    up.set_defaults(func=cmd_up)

    verify = stack_arg(sub.add_parser("verify", help="guard the running containers and run the read-only identity check"))
    verify.set_defaults(func=cmd_verify)

    realm = sub.add_parser("realm", help="realm operations")
    realm_sub = realm.add_subparsers(dest="action", required=True)
    rebuild = stack_arg(realm_sub.add_parser(
        "rebuild", help="drop and recreate Keycloak's database for a fresh realm import (only while every user is synthetic)"))
    rebuild.add_argument("--confirm", action="store_true", help="required: the command destroys Keycloak's realm, users and events")
    rebuild.set_defaults(func=cmd_realm_rebuild)
    return parser


def main(argv: list | None = None, runner_factory=None) -> int:
    runner_factory = runner_factory or Runner
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return args.func(args, runner_factory)
    except StackError as error:
        print("stackctl: %s" % error, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
