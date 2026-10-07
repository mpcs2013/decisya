#!/bin/sh
# Keycloak production-start wrapper for the deployable stack (#120, G4-120-01, ADR-0018 D3/D6).
#
# Keycloak reads its database and bootstrap-admin passwords only from environment variables.
# Compose never puts them in `environment:` (they would show in `docker inspect`), so this
# wrapper reads them from the mounted secret files and exports them into the process
# environment of Keycloak alone, then `exec`s `kc.sh start`. They are readable only through
# /proc inside this container (ADR-0018 records this as accepted).
#
# Rules, enforced by deploy/tests and by tests/Decisya.Bff.Tests (the wrapper test):
#   - no `set -x`, and no echo, printf or log of any value, anywhere in this file;
#   - messages name the variable, never a path's content or a value;
#   - always `kc.sh start`; never `start-dev`, never `--import-realm` (the realm is #121).
#
# The file is LF-only (.gitattributes: * text=auto eol=lf).
set -eu

die() {
    echo "entrypoint-stack: $1" >&2
    exit 1
}

# The password variables must come from the files, never from the environment: a value set
# directly would be visible to `docker inspect` and would silently win over the file.
if [ -n "${KC_DB_PASSWORD+set}" ]; then
    die "KC_DB_PASSWORD must not be set; use KC_DB_PASSWORD_FILE"
fi
if [ -n "${KC_BOOTSTRAP_ADMIN_PASSWORD+set}" ]; then
    die "KC_BOOTSTRAP_ADMIN_PASSWORD must not be set; use KC_BOOTSTRAP_ADMIN_PASSWORD_FILE"
fi

# Only the production start. Reject the development server and realm import outright,
# whether they arrive as arguments or as the KC_COMMAND-style environment of a wrapper.
for argument in "$@"; do
    case "$argument" in
        start-dev | --import-realm | import | build | export)
            die "argument not allowed in the stack: $argument"
            ;;
    esac
done

# Required: the database password file.
if [ -z "${KC_DB_PASSWORD_FILE:-}" ]; then
    die "KC_DB_PASSWORD_FILE is required"
fi
if [ ! -f "$KC_DB_PASSWORD_FILE" ] || [ ! -r "$KC_DB_PASSWORD_FILE" ]; then
    die "KC_DB_PASSWORD_FILE does not name a readable file"
fi
IFS= read -r KC_DB_PASSWORD < "$KC_DB_PASSWORD_FILE" || [ -n "$KC_DB_PASSWORD" ]
if [ -z "$KC_DB_PASSWORD" ]; then
    die "KC_DB_PASSWORD_FILE is empty"
fi
export KC_DB_PASSWORD
unset KC_DB_PASSWORD_FILE

# Optional: the single-use bootstrap admin password. After the first start the file is
# deleted (S-120-04) and this block is skipped, so the temporary admin is never re-exported.
if [ -n "${KC_BOOTSTRAP_ADMIN_PASSWORD_FILE:-}" ] && [ -f "$KC_BOOTSTRAP_ADMIN_PASSWORD_FILE" ]; then
    if [ ! -r "$KC_BOOTSTRAP_ADMIN_PASSWORD_FILE" ]; then
        die "KC_BOOTSTRAP_ADMIN_PASSWORD_FILE is not readable"
    fi
    IFS= read -r KC_BOOTSTRAP_ADMIN_PASSWORD < "$KC_BOOTSTRAP_ADMIN_PASSWORD_FILE" || [ -n "$KC_BOOTSTRAP_ADMIN_PASSWORD" ]
    if [ -n "$KC_BOOTSTRAP_ADMIN_PASSWORD" ]; then
        export KC_BOOTSTRAP_ADMIN_PASSWORD
    else
        unset KC_BOOTSTRAP_ADMIN_PASSWORD
    fi
fi
unset KC_BOOTSTRAP_ADMIN_PASSWORD_FILE

exec /opt/keycloak/bin/kc.sh start "$@"
