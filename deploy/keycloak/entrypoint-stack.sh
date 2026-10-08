#!/bin/sh
# Keycloak production-start wrapper for the deployable stack (#120 G4-120-01, ADR-0018 D3/D6;
# #121 G4-121-03, G2 D6).
#
# Keycloak reads its database and bootstrap-admin passwords only from environment variables.
# Compose never puts them in `environment:` (they would show in `docker inspect`), so this
# wrapper reads them from the mounted secret files and exports them into the process
# environment of Keycloak alone, then `exec`s `kc.sh start`. They are readable only through
# /proc inside this container (ADR-0018 records this as accepted).
#
# #121: the wrapper also owns the one-shot realm import. It validates the two Compose values
# (DECISYA_APP_HOST, DECISYA_HTTPS_PORT) and the BFF client secret file, derives the two
# placeholders the production realm file uses (DECISYA_REALM_APP_ORIGIN, DECISYA_BFF_CLIENT_SECRET)
# and runs `kc.sh start --import-realm` itself. The validation is a security control, not input
# hygiene: Keycloak substitutes the values into the realm JSON text, so a quote, comma or
# wildcard in a value would widen the client's redirect URIs (T121-06).
#
# Rules, enforced by deploy/tests and by tests/Decisya.Identity.Tests:
#   - no `set -x`, and no echo, printf or log of any value, anywhere in this file;
#   - messages name the variable, never a path's content or a value;
#   - always `kc.sh start`; never `start-dev`; `--import-realm` is added here and nowhere else.
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

# The realm placeholders are derived below, from validated inputs only. A value that arrives
# in the environment (a Compose `environment:` entry, an `-e` flag) is refused outright.
if [ -n "${DECISYA_BFF_CLIENT_SECRET+set}" ]; then
    die "DECISYA_BFF_CLIENT_SECRET must not be set; the wrapper reads the secret file"
fi
if [ -n "${DECISYA_REALM_APP_ORIGIN+set}" ]; then
    die "DECISYA_REALM_APP_ORIGIN must not be set; the wrapper derives it"
fi

# Only the production start. Reject the development server and the realm import as caller
# arguments, whether they arrive as arguments or as the KC_COMMAND-style environment of a wrapper.
for argument in "$@"; do
    case "$argument" in
        start-dev | --import-realm | import | build | export)
            die "an argument that starts the development server or runs an import is not allowed in the stack"
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

# Required: the public application host. Lowercase DNS name, at least two labels, labels of 1 to
# 63 letters, digits and hyphens that neither start nor end with a hyphen (the same rule as
# deploy/compose/stackguards.py value_problem). Explicit character lists, not ranges, so the
# check does not depend on the locale's collation. Anything else, a quote, backslash, comma,
# asterisk, slash, colon, whitespace or control character included, is refused.
valid_host() {
    host_value=$1
    [ -n "$host_value" ] || return 1
    [ "${#host_value}" -le 253 ] || return 1
    case "$host_value" in
        *[!abcdefghijklmnopqrstuvwxyz0123456789.-]*) return 1 ;;
        *.*) ;;
        *) return 1 ;;
    esac
    case "$host_value" in
        .* | *. | *..*) return 1 ;;
    esac
    saved_ifs=$IFS
    IFS=.
    for label in $host_value; do
        if [ -z "$label" ] || [ "${#label}" -gt 63 ]; then
            IFS=$saved_ifs
            return 1
        fi
        case "$label" in
            -* | *-)
                IFS=$saved_ifs
                return 1
                ;;
        esac
    done
    IFS=$saved_ifs
    return 0
}

if [ -z "${DECISYA_APP_HOST:-}" ]; then
    die "DECISYA_APP_HOST is required"
fi
if ! valid_host "$DECISYA_APP_HOST"; then
    die "DECISYA_APP_HOST must be a lowercase DNS name"
fi
if [ -z "${DECISYA_HTTPS_PORT:-}" ]; then
    die "DECISYA_HTTPS_PORT is required"
fi
if [ "$DECISYA_HTTPS_PORT" != "8443" ]; then
    die "DECISYA_HTTPS_PORT must be 8443"
fi

# Required: the BFF client secret, from the same secret file the BFF reads (the name is the
# BFF's own configuration key). One line of letters and digits, at least 32 of them. A second
# line, a carriage return or any other character is refused (T121-06).
secret_file=/run/secrets/Bff__Oidc__ClientSecret
if [ ! -f "$secret_file" ] || [ ! -r "$secret_file" ]; then
    die "the Bff__Oidc__ClientSecret secret file is missing or unreadable"
fi
bff_client_secret=
extra_line=
{
    IFS= read -r bff_client_secret || [ -n "$bff_client_secret" ] || true
    if IFS= read -r extra_line; then
        die "the Bff__Oidc__ClientSecret secret file must hold one line"
    fi
    if [ -n "$extra_line" ]; then
        die "the Bff__Oidc__ClientSecret secret file must hold one line"
    fi
} < "$secret_file"
case "$bff_client_secret" in
    "" | *[!ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789]*)
        die "the Bff__Oidc__ClientSecret secret file must hold letters and digits only"
        ;;
esac
if [ "${#bff_client_secret}" -lt 32 ]; then
    die "the Bff__Oidc__ClientSecret secret file must hold at least 32 characters"
fi

DECISYA_REALM_APP_ORIGIN="https://$DECISYA_APP_HOST:$DECISYA_HTTPS_PORT"
DECISYA_BFF_CLIENT_SECRET=$bff_client_secret
export DECISYA_REALM_APP_ORIGIN DECISYA_BFF_CLIENT_SECRET
unset bff_client_secret

exec /opt/keycloak/bin/kc.sh start --import-realm "$@"
